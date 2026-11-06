using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class MeterUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MeterUnit",
                table: "ServiceLines",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MeterUnit",
                table: "ServiceLines");
        }
    }
}
