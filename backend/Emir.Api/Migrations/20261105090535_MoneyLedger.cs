using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Emir.Api.Migrations
{
    /// <inheritdoc />
    public partial class MoneyLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LedgerEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AbonentId = table.Column<string>(type: "text", nullable: false),
                    ServiceId = table.Column<string>(type: "text", nullable: true),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Period = table.Column<string>(type: "text", nullable: true),
                    DebtDelta = table.Column<long>(type: "bigint", nullable: false),
                    CreditDelta = table.Column<long>(type: "bigint", nullable: false),
                    Who = table.Column<string>(type: "text", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reference = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_AbonentId_Id",
                table: "LedgerEntries",
                columns: new[] { "AbonentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_AtUtc",
                table: "LedgerEntries",
                column: "AtUtc");

            // Текущие остатки становятся первой записью журнала, дальше он только дополняется
            migrationBuilder.Sql("""
                INSERT INTO "LedgerEntries" ("AbonentId", "ServiceId", "Kind", "DebtDelta", "CreditDelta", "Who", "AtUtc")
                SELECT "AbonentId", "ServiceId", 'opening', "Debt", 0, 'Перенос при обновлении', now() FROM "ServiceLines" WHERE "Debt" <> 0;
                INSERT INTO "LedgerEntries" ("AbonentId", "ServiceId", "Kind", "DebtDelta", "CreditDelta", "Who", "AtUtc")
                SELECT "Id", NULL, 'opening', 0, "Credit", 'Перенос при обновлении', now() FROM "Abonents" WHERE "Credit" <> 0;

                CREATE FUNCTION emir_ledger_is_append_only() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Журнал денег нельзя менять: исправления проводятся новыми записями';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER ledger_no_change BEFORE UPDATE OR DELETE ON "LedgerEntries" FOR EACH ROW EXECUTE FUNCTION emir_ledger_is_append_only();
                CREATE TRIGGER ledger_no_truncate BEFORE TRUNCATE ON "LedgerEntries" FOR EACH STATEMENT EXECUTE FUNCTION emir_ledger_is_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS ledger_no_change ON "LedgerEntries";
                DROP TRIGGER IF EXISTS ledger_no_truncate ON "LedgerEntries";
                DROP FUNCTION IF EXISTS emir_ledger_is_append_only();
                """);

            migrationBuilder.DropTable(
                name: "LedgerEntries");
        }
    }
}
