using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class OldSystemParity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Office",
                table: "Payments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Period",
                table: "Payments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HideIn1C",
                table: "Abonents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HideInPlatosfera",
                table: "Abonents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IgnoreDuplicateCheck",
                table: "Abonents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalArea",
                table: "Abonents",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Office",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Period",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "HideIn1C",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "HideInPlatosfera",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "IgnoreDuplicateCheck",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "TotalArea",
                table: "Abonents");
        }
    }
}
