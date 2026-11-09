using Emir.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

/// <summary>Офисы приёма платежей. Как в старой системе, касса выбирает офис при оплате. Список ведётся в «Справочниках».</summary>
public static class Offices
{
    public const string Directory = "Офисы приёма платежей";
    private const string Column = "Название";

    public static async Task<List<string>> List(EmirDb db)
    {
        var table = await db.ReferenceTables.AsNoTracking().FirstOrDefaultAsync(item => item.Name == Directory);
        if (table is null) return [];
        return DirectoryData.Options(DirectoryData.Columns(table), DirectoryData.Rows(table), Column, null);
    }

    /// <summary>Справочник заводится с кассами ФЛ и ЮЛ, как в форме оплаты старой системы. Заведённый не трогаем.</summary>
    public static async Task EnsureTable(EmirDb db)
    {
        if (await db.ReferenceTables.AnyAsync(item => item.Name == Directory)) return;
        var table = new ReferenceTable { Name = Directory, ColumnsJson = System.Text.Json.JsonSerializer.Serialize(new[] { "ID", Column }, DirectoryData.Plain), RowsJson = "[]", LoadedAtUtc = DateTime.UtcNow };
        DirectoryData.SetRows(table, [["1", "Чистоозерное. Касса. ФЛ."], ["2", "Чистоозерное. Касса. ЮЛ."]]);
        db.ReferenceTables.Add(table);
        await db.SaveChangesAsync();
    }
}
