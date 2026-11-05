using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class AbonentHouseHeatNorm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "HouseHeatNorm",
                table: "Abonents",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HouseHeatNorm",
                table: "Abonents");
        }
    }
}
