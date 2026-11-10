using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class AbonentRegistrationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "BirthDate",
                table: "Abonents",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Abonents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kpp",
                table: "Abonents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PassportProtected",
                table: "Abonents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BirthDate",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "Kpp",
                table: "Abonents");

            migrationBuilder.DropColumn(
                name: "PassportProtected",
                table: "Abonents");
        }
    }
}
