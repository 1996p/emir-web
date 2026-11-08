using System.Globalization;
using System.Text.Json;
using Emir.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

public record HouseCategoryItem(string Name, decimal? Norm);

/// <summary>Чтение и запись справочников старой системы, которые хранятся таблицей целиком (колонки + строки в JSON).</summary>
public static class DirectoryData
{
    // Русский текст храним как есть, а не кодами \uXXXX
    public static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static List<string> Columns(ReferenceTable table) => JsonSerializer.Deserialize<List<string>>(table.ColumnsJson) ?? [];
    public static List<List<string>> Rows(ReferenceTable table) => JsonSerializer.Deserialize<List<List<string>>>(table.RowsJson) ?? [];

    public static void SetRows(ReferenceTable table, List<List<string>> rows)
    {
        table.RowsJson = JsonSerializer.Serialize(rows, Plain);
        table.RowCount = rows.Count;
    }

    public static string Cell(List<string> row, int index) => index >= 0 && index < row.Count ? row[index].Trim() : "";

    /// <summary>Уникальные непустые значения колонки, по алфавиту. Where: колонка источника → нужное значение (каскад).</summary>
    public static List<string> Options(List<string> columns, List<List<string>> rows, string column, IReadOnlyDictionary<string, string>? where)
    {
        var index = columns.IndexOf(column);
        var filters = (where ?? new Dictionary<string, string>()).Select(pair => (at: columns.IndexOf(pair.Key), value: pair.Value.Trim())).ToList();
        return rows.Where(row => filters.All(filter => Cell(row, filter.at) == filter.value))
            .Select(row => Cell(row, index)).Where(value => value != "")
            .Distinct().OrderBy(value => value, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Норма из справочника: «.01875», «0,01875» и «0.01875» — одно и то же. Пусто или мусор — нормы нет.</summary>
    public static decimal? ParseNorm(string text)
    {
        var value = text.Trim().Replace(',', '.');
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var norm) && norm > 0 ? norm : null;
    }

    public static string FormatNorm(decimal norm) => norm.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Категории домов из справочника «Категории домов» — единый список для абонентов, домов и норм теплопотребления.</summary>
    public static async Task<List<HouseCategoryItem>> HouseCategories(EmirDb db)
    {
        var table = await db.ReferenceTables.AsNoTracking().FirstOrDefaultAsync(item => item.Name == Catalog.HouseCategories);
        if (table is null) return [];
        var columns = Columns(table);
        int name = columns.IndexOf(Catalog.HouseCategoryName), norm = columns.IndexOf(Catalog.HouseCategoryNorm);
        return Rows(table).Select(row => new HouseCategoryItem(Cell(row, name), ParseNorm(Cell(row, norm))))
            .Where(item => item.Name != "").GroupBy(item => item.Name).Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Справочника «Типы домов» в старой системе не было: собираем его из колонки «Тип» абонентов ЖКХ. Уже заведённый не трогаем.</summary>
    public static async Task EnsureHouseTypes(EmirDb db)
    {
        if (await db.ReferenceTables.AnyAsync(item => item.Name == Catalog.HouseTypes)) return;
        var source = await db.ReferenceTables.AsNoTracking().FirstOrDefaultAsync(item => item.Name == "Абоненты ЖКХ");
        var names = source is null ? [] : Options(Columns(source), Rows(source), "Тип", null);
        if (names.Count == 0) names = ["Многоквартирный", "Индивидуальный", "Блочной застройки", "Нежилой"];
        var table = new ReferenceTable { Name = Catalog.HouseTypes, ColumnsJson = JsonSerializer.Serialize(new[] { "ID", "Название" }, Plain), RowsJson = "[]", LoadedAtUtc = DateTime.UtcNow };
        SetRows(table, names.Select((item, i) => new List<string> { (i + 1).ToString(), item }).ToList());
        db.ReferenceTables.Add(table);
        await db.SaveChangesAsync();
    }
}
