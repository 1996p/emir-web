using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class TariffHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {


            migrationBuilder.AddColumn<decimal>(
                name: "Price",
                table: "ChargeEntries",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TariffId",
                table: "ChargeEntries",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServiceTerms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AbonentId = table.Column<string>(type: "text", nullable: false),
                    ServiceId = table.Column<string>(type: "text", nullable: false),
                    FromPeriod = table.Column<string>(type: "text", nullable: false),
                    TariffId = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "text", nullable: false),
                    Who = table.Column<string>(type: "text", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceTerms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceTerms_ServiceLines_AbonentId_ServiceId",
                        columns: x => new { x.AbonentId, x.ServiceId },
                        principalTable: "ServiceLines",
                        principalColumns: new[] { "AbonentId", "ServiceId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServiceTerms_Tariffs_TariffId",
                        column: x => x.TariffId,
                        principalTable: "Tariffs",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TariffPrices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TariffId = table.Column<string>(type: "text", nullable: false),
                    FromPeriod = table.Column<string>(type: "text", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Who = table.Column<string>(type: "text", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TariffPrices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TariffPrices_Tariffs_TariffId",
                        column: x => x.TariffId,
                        principalTable: "Tariffs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceTerms_AbonentId_ServiceId_FromPeriod",
                table: "ServiceTerms",
                columns: new[] { "AbonentId", "ServiceId", "FromPeriod" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceTerms_TariffId",
                table: "ServiceTerms",
                column: "TariffId");

            migrationBuilder.CreateIndex(
                name: "IX_TariffPrices_TariffId_FromPeriod",
                table: "TariffPrices",
                columns: new[] { "TariffId", "FromPeriod" },
                unique: true);

            // Существующие цены и условия становятся первой записью истории: они действовали и раньше
            migrationBuilder.Sql("""
                INSERT INTO "TariffPrices" ("TariffId", "FromPeriod", "Price", "Who", "AtUtc", "Reason")
                SELECT "Id", '2000-01', "Price", 'Перенос при обновлении', now(), NULL FROM "Tariffs";
                INSERT INTO "ServiceTerms" ("AbonentId", "ServiceId", "FromPeriod", "TariffId", "State", "Who", "AtUtc", "Reason")
                SELECT "AbonentId", "ServiceId", '2000-01', "TariffId", "State", 'Перенос при обновлении', now(), NULL FROM "ServiceLines" WHERE "ServiceId" <> 'penalty';
                """);

            migrationBuilder.DropColumn(
                name: "Price",
                table: "Tariffs");

            migrationBuilder.DropColumn(
                name: "MeterTariff",
                table: "ServiceLines");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Price",
                table: "ChargeEntries");

            migrationBuilder.DropColumn(
                name: "TariffId",
                table: "ChargeEntries");

            migrationBuilder.AddColumn<decimal>(
                name: "Price",
                table: "Tariffs",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "MeterTariff",
                table: "ServiceLines",
                type: "bigint",
                nullable: true);

            // Откат возвращает последнюю цену, действующую на сегодня, и цену счётчика в копейках
            migrationBuilder.Sql("""
                UPDATE "Tariffs" t SET "Price" = COALESCE((SELECT p."Price" FROM "TariffPrices" p WHERE p."TariffId" = t."Id" AND p."FromPeriod" <= to_char(now(), 'YYYY-MM') ORDER BY p."FromPeriod" DESC LIMIT 1), 0);
                UPDATE "ServiceLines" l SET "MeterTariff" = (SELECT round(t."Price" * 100) FROM "Tariffs" t WHERE t."Id" = l."TariffId") WHERE l."MeterNumber" IS NOT NULL;
                """);

            migrationBuilder.DropTable(
                name: "ServiceTerms");

            migrationBuilder.DropTable(
                name: "TariffPrices");
        }
    }
}
