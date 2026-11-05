using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class AbonentCard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Consent",
                table: "Abonents",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConsentAtUtc",
                table: "Abonents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Abonents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnershipDocument",
                table: "Abonents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AbonentFacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AbonentId = table.Column<string>(type: "text", nullable: false),
                    FromPeriod = table.Column<string>(type: "text", nullable: false),
                    Area = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Residents = table.Column<int>(type: "integer", nullable: false),
                    Who = table.Column<string>(type: "text", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AbonentFacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AbonentFacts_Abonents_AbonentId",
                        column: x => x.AbonentId,
                        principalTable: "Abonents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Residents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AbonentId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ToDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Residents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Residents_Abonents_AbonentId",
                        column: x => x.AbonentId,
                        principalTable: "Abonents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AbonentFacts_AbonentId_FromPeriod",
                table: "AbonentFacts",
                columns: new[] { "AbonentId", "FromPeriod" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Residents_AbonentId",
                table: "Residents",
                column: "AbonentId");

            // Текущие площадь и жильцы становятся первой записью истории
            migrationBuilder.Sql("""
                INSERT INTO "AbonentFacts" ("AbonentId", "FromPeriod", "Area", "Residents", "Who", "AtUtc")
                SELECT "Id", '2000-01', "Area", "Residents", 'Перенос при обновлении', now() FROM "Abonents";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AbonentFacts");

            migrationBuilder.DropTable(
                name: "Residents");

            migrationBuilder.DropColumn(
                name: "Consent",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "ConsentAtUtc",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "OwnershipDocument",
                table: "Abonents");
        }
    }
}
