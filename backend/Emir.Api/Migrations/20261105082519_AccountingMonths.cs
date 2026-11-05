using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class AccountingMonths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AppliedAmount",
                table: "PeriodReadings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<decimal>(
                name: "Area",
                table: "ChargeEntries",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContractNorm",
                table: "ChargeEntries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Residents",
                table: "ChargeEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountingMonths",
                columns: table => new
                {
                    Period = table.Column<string>(type: "text", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedBy = table.Column<string>(type: "text", nullable: true),
                    ReopenedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReopenedBy = table.Column<string>(type: "text", nullable: true),
                    ReopenReason = table.Column<string>(type: "text", nullable: true),
                    ReclosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReclosedBy = table.Column<string>(type: "text", nullable: true),
                    ReclosedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingMonths", x => x.Period);
                });

            migrationBuilder.CreateTable(
                name: "ChargeRevisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Period = table.Column<string>(type: "text", nullable: false),
                    AbonentId = table.Column<string>(type: "text", nullable: false),
                    ServiceId = table.Column<string>(type: "text", nullable: false),
                    OldAmount = table.Column<long>(type: "bigint", nullable: false),
                    NewAmount = table.Column<long>(type: "bigint", nullable: false),
                    OldPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    NewPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Who = table.Column<string>(type: "text", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargeRevisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingMonths_ReopenedAtUtc",
                table: "AccountingMonths",
                column: "ReopenedAtUtc",
                unique: true,
                filter: "\"ReopenedAtUtc\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChargeRevisions_Period_AbonentId",
                table: "ChargeRevisions",
                columns: new[] { "Period", "AbonentId" });

            // Существующая база продолжает ноябрь 2026. Новая задаёт первый месяц: через начальные данные или команду start-period
            migrationBuilder.Sql("""
                INSERT INTO "AccountingMonths" ("Period", "ClosedAtUtc", "ClosedBy", "ReclosedCount")
                SELECT '2026-11',
                       CASE WHEN EXISTS (SELECT 1 FROM "PeriodSteps" WHERE "Id" = 'close' AND "State" = 'done') THEN now() END,
                       CASE WHEN EXISTS (SELECT 1 FROM "PeriodSteps" WHERE "Id" = 'close' AND "State" = 'done') THEN 'Перенос при обновлении' END,
                       0
                WHERE EXISTS (SELECT 1 FROM "PeriodSteps");

                UPDATE "PeriodReadings" r SET "AppliedAmount" = COALESCE((
                    SELECT round((r."Value" - r."StartValue") * p."Price" * 100)
                    FROM "ServiceTerms" st
                    JOIN "Tariffs" t ON t."Id" = st."TariffId" AND t."Scheme" = 'MeterApartment'
                    JOIN LATERAL (SELECT "Price" FROM "TariffPrices" tp WHERE tp."TariffId" = t."Id" AND tp."FromPeriod" <= r."Period" ORDER BY tp."FromPeriod" DESC LIMIT 1) p ON true
                    WHERE st."AbonentId" = r."AbonentId" AND st."ServiceId" = r."ServiceId" AND st."FromPeriod" <= r."Period" AND st."State" = 'Работает'
                    ORDER BY st."FromPeriod" DESC LIMIT 1), 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountingMonths");

            migrationBuilder.DropTable(
                name: "ChargeRevisions");

            migrationBuilder.DropColumn(
                name: "AppliedAmount",
                table: "PeriodReadings");

            migrationBuilder.DropColumn(
                name: "Area",
                table: "ChargeEntries");

            migrationBuilder.DropColumn(
                name: "ContractNorm",
                table: "ChargeEntries");

            migrationBuilder.DropColumn(
                name: "Residents",
                table: "ChargeEntries");
        }
    }
}
