using System.Security.Claims;
using Emir.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

/// <summary>Правка записи справочника-источника расходится по тем, кто на неё ссылается (Catalog.References и категория дома абонента).</summary>
public static class DirectoryLinks
{
    /// <summary>old — запись до правки, updated — после (null — удалена), rows — все записи источника уже после правки.
    /// Переименование значения переносится в ссылающиеся справочники; удалить или «отвязать» используемое значение нельзя.
    /// Возвращает, что ещё изменилось, для журнала.</summary>
    public static async Task<string> Propagate(EmirDb db, ClaimsPrincipal user, string source, List<string> columns, List<List<string>> rows, List<string> old, List<string>? updated)
    {
        var notes = new List<string>();
        foreach (var reference in Catalog.References.Where(item => item.Source == source))
        {
            var labelAt = columns.IndexOf(reference.Label);
            var oldLabel = DirectoryData.Cell(old, labelAt);
            if (labelAt < 0 || oldLabel == "") continue;
            var from = (reference.Where ?? []).Select(pair => (pair.From, pair.To, at: columns.IndexOf(pair.From))).ToList();
            var sameFilters = updated is not null && from.All(pair => DirectoryData.Cell(old, pair.at) == DirectoryData.Cell(updated, pair.at));
            var newLabel = updated is null ? "" : DirectoryData.Cell(updated, labelAt);
            if (sameFilters && newLabel == oldLabel) continue;
            // Если такое же значение даёт другая запись, ссылки не осиротеют
            if (rows.Any(row => DirectoryData.Cell(row, labelAt) == oldLabel && from.All(pair => DirectoryData.Cell(row, pair.at) == DirectoryData.Cell(old, pair.at)))) continue;

            var target = await db.ReferenceTables.FromSqlInterpolated($"SELECT * FROM \"ReferenceTables\" WHERE \"Name\" = {reference.Table} FOR UPDATE").FirstOrDefaultAsync();
            if (target is null) continue;
            var targetColumns = DirectoryData.Columns(target);
            var cells = targetColumns.Select((column, i) => (column, i)).Where(item => item.column == reference.Column).Select(item => item.i).ToList();
            var to = from.Select(pair => (pair.at, toAt: targetColumns.IndexOf(pair.To))).ToList();
            if (cells.Count == 0 || to.Any(pair => pair.toAt < 0)) continue;
            var targetRows = DirectoryData.Rows(target);
            bool Uses(List<string> row) => cells.Any(i => DirectoryData.Cell(row, i) == oldLabel) && to.All(pair => DirectoryData.Cell(row, pair.toAt) == DirectoryData.Cell(old, pair.at));
            var using_ = targetRows.Where(Uses).ToList();
            if (using_.Count == 0) continue;

            if (!sameFilters || newLabel == "")
                throw new ApiError(409, $"«{oldLabel}» используется в справочнике «{reference.Table}»: {Records(using_.Count)}. Сначала поменяйте там значение.");
            foreach (var row in using_)
                foreach (var i in cells)
                    if (DirectoryData.Cell(row, i) == oldLabel) row[i] = newLabel;
            DirectoryData.SetRows(target, targetRows);
            notes.Add($"в «{reference.Table}» переименовано: {Records(using_.Count)}");
        }

        if (source == Catalog.HouseCategories) notes.AddRange(await HouseCategoryChanged(db, user, columns, rows, old, updated));
        // Город, улица и дом абонента — значения из справочников адресов
        if (await Addresses.Propagate(db, source, columns, rows, old, updated) is { } addressNote) notes.Add(addressNote);
        return string.Join("; ", notes);
    }

    /// <summary>Категория дома абонента — значение из «Категорий домов», а норма категории — его норма теплопотребления (схема NormHouse).</summary>
    private static async Task<List<string>> HouseCategoryChanged(EmirDb db, ClaimsPrincipal user, List<string> columns, List<List<string>> rows, List<string> old, List<string>? updated)
    {
        var notes = new List<string>();
        int nameAt = columns.IndexOf(Catalog.HouseCategoryName), normAt = columns.IndexOf(Catalog.HouseCategoryNorm);
        var oldName = DirectoryData.Cell(old, nameAt);
        if (nameAt < 0 || oldName == "") return notes;
        var newName = updated is null ? "" : DirectoryData.Cell(updated, nameAt);
        var stillThere = rows.Any(row => DirectoryData.Cell(row, nameAt) == oldName);
        var abonents = await db.Abonents.Where(abonent => abonent.HouseCategory == oldName).ToListAsync();

        if (newName != oldName && !stillThere && abonents.Count > 0)
        {
            if (newName == "") throw new ApiError(409, $"Категория «{oldName}» стоит у {abonents.Count} абонентов. Сначала смените им категорию.");
            foreach (var abonent in abonents) abonent.HouseCategory = newName;
            var stored = await db.HeatNormCategories.FirstOrDefaultAsync(item => item.Category == oldName);
            if (stored is not null && !await db.HeatNormCategories.AnyAsync(item => item.Category == newName))
            {
                db.HeatNormCategories.Remove(stored);
                db.HeatNormCategories.Add(new HeatNormCategory { Category = newName, Norm = stored.Norm });
            }
            notes.Add($"категория переименована у {abonents.Count} абонентов");
        }

        if (updated is null || newName == "" || normAt < 0) return notes;
        var oldNorm = DirectoryData.ParseNorm(DirectoryData.Cell(old, normAt));
        var newNorm = DirectoryData.ParseNorm(DirectoryData.Cell(updated, normAt));
        if (newNorm == oldNorm || newNorm is null) return notes;
        // Норма меняет начисления, поэтому её правит тот же, кто применяет нормы в «Нормах теплопотребления»
        if (!user.IsInRole(Roles.Accountant) && !user.IsInRole(Roles.Admin)) throw new ApiError(403, "Норму теплопотребления меняет бухгалтер или администратор");
        if (newNorm > 10 || newNorm != Math.Round(newNorm.Value, 6)) throw new ApiError(422, "Норма должна быть больше нуля и не больше шести знаков после запятой");
        var changed = await ApplyNorm(db, newName, newNorm.Value);
        notes.Add($"норма {DirectoryData.FormatNorm(newNorm.Value)} проставлена {changed} абонентам");
        return notes;
    }

    /// <summary>Проставляет норму всем абонентам категории и запоминает её за категорией. Возвращает, у скольких абонентов норма изменилась.</summary>
    public static async Task<int> ApplyNorm(EmirDb db, string category, decimal norm)
    {
        var stored = await db.HeatNormCategories.FirstOrDefaultAsync(item => item.Category == category);
        if (stored is null) db.HeatNormCategories.Add(new HeatNormCategory { Category = category, Norm = norm });
        else stored.Norm = norm;
        var changed = 0;
        // Абоненты, уже загруженные в контекст (например, только что переименованные), берутся из него же
        foreach (var abonent in await db.Abonents.Where(abonent => abonent.HouseCategory == category).ToListAsync())
            if (abonent.HouseHeatNorm != norm) { abonent.HouseHeatNorm = norm; changed++; }
        foreach (var abonent in db.Abonents.Local.Where(abonent => abonent.HouseCategory == category && abonent.HouseHeatNorm != norm))
        { abonent.HouseHeatNorm = norm; changed++; }
        return changed;
    }

    private static string Records(int count) => count % 10 == 1 && count % 100 != 11 ? $"{count} запись"
        : count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? $"{count} записи" : $"{count} записей";
}
