using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class AbonentAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Boiler",
                table: "Abonents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "District",
                table: "Abonents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HeatChamber",
                table: "Abonents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HouseCategory",
                table: "Abonents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WallMaterial",
                table: "Abonents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Boiler",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "District",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "HeatChamber",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "HouseCategory",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "WallMaterial",
                table: "Abonents");
        }
    }
}
