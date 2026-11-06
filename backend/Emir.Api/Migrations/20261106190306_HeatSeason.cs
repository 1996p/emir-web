using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class HeatSeason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HeatSeasonEndMonth",
                table: "OrganizationProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<int>(
                name: "HeatSeasonStartMonth",
                table: "OrganizationProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 10);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeatSeasonEndMonth",
                table: "OrganizationProfiles");

            migrationBuilder.DropColumn(
                name: "HeatSeasonStartMonth",
                table: "OrganizationProfiles");
        }
    }
}
