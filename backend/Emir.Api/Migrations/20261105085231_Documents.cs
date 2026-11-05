using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class Documents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "Tariffs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BillingDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Period = table.Column<string>(type: "text", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    AbonentId = table.Column<string>(type: "text", nullable: false),
                    Number = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Stale = table.Column<bool>(type: "boolean", nullable: false),
                    Total = table.Column<long>(type: "bigint", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false),
                    Who = table.Column<string>(type: "text", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillingDocuments_Abonents_AbonentId",
                        column: x => x.AbonentId,
                        principalTable: "Abonents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentCounters",
                columns: table => new
                {
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Last = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentCounters", x => new { x.Year, x.Kind });
                });

            migrationBuilder.CreateTable(
                name: "OrganizationProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Inn = table.Column<string>(type: "text", nullable: false),
                    Kpp = table.Column<string>(type: "text", nullable: true),
                    Address = table.Column<string>(type: "text", nullable: false),
                    BankName = table.Column<string>(type: "text", nullable: true),
                    Bik = table.Column<string>(type: "text", nullable: true),
                    CorrAccount = table.Column<string>(type: "text", nullable: true),
                    Account = table.Column<string>(type: "text", nullable: true),
                    Director = table.Column<string>(type: "text", nullable: true),
                    ChiefAccountant = table.Column<string>(type: "text", nullable: true),
                    VatRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingDocuments_AbonentId_Period_Kind",
                table: "BillingDocuments",
                columns: new[] { "AbonentId", "Period", "Kind" },
                unique: true,
                filter: "\"Status\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_BillingDocuments_Kind_Year_Number_Revision",
                table: "BillingDocuments",
                columns: new[] { "Kind", "Year", "Number", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingDocuments");

            migrationBuilder.DropTable(
                name: "DocumentCounters");

            migrationBuilder.DropTable(
                name: "OrganizationProfiles");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "Tariffs");
        }
    }
}
