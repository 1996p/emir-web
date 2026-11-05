using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class UserAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedLogins",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLoginUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedUntilUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SessionStamp",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Действующие сотрудники остаются включёнными. Старые сессии без отметки завершаются: нужно войти заново
            migrationBuilder.Sql("""
                UPDATE "Users" SET "IsActive" = true, "SessionStamp" = replace(gen_random_uuid()::text, '-', '');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedLogins",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastLoginUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LockedUntilUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SessionStamp",
                table: "Users");
        }
    }
}
