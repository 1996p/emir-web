using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class ReferenceTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReferenceTables",
                columns: table => new
                {
                    Name = table.Column<string>(type: "text", nullable: false),
                    ColumnsJson = table.Column<string>(type: "text", nullable: false),
                    RowsJson = table.Column<string>(type: "text", nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    Sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    LoadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceTables", x => x.Name);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReferenceTables");
        }
    }
}
