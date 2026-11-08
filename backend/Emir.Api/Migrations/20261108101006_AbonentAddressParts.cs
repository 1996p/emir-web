using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class AbonentAddressParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "House",
                table: "Abonents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StreetName",
                table: "Abonents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "House",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "StreetName",
                table: "Abonents");
        }
    }
}
