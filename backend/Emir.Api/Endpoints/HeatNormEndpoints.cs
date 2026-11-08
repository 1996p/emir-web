using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

/// <summary>Category — категория дома; Abonents — сколько абонентов в ней; WithoutNorm — у скольких из них не задана норма;
/// Norm — норма категории из справочника «Категории домов»; Distinct — какие нормы реально стоят у абонентов этой категории;
/// InDirectory — категория есть в справочнике (нет — значение осталось у абонентов от старой системы).</summary>
public record HeatNormRowDto(string Category, int Abonents, int WithoutNorm, decimal? Norm, List<decimal> Distinct, bool InDirectory = true);
public record HeatNormsDto(List<HeatNormRowDto> Categories, int WithoutCategory);
public record ApplyHeatNormRequest(string Category, decimal Norm, string Reason);

public static class HeatNormEndpoints
{
    public static void MapHeatNorms(this RouteGroupBuilder api)
    {
        api.MapGet("/heat-norms", async (EmirDb db) =>
        {
            var abonents = await db.Abonents
                .Where(abonent => abonent.HouseCategory != null && abonent.HouseCategory != "")
                .Select(abonent => new { abonent.HouseCategory, abonent.HouseHeatNorm })
                .ToListAsync();
            var stored = await db.HeatNormCategories.ToDictionaryAsync(item => item.Category, item => item.Norm);
            var withoutCategory = await db.Abonents
                .CountAsync(abonent => abonent.HouseCategory == null || abonent.HouseCategory == "");
            // Список категорий — справочник «Категории домов», в том числе категории без абонентов
            var directory = (await DirectoryData.HouseCategories(db)).ToDictionary(item => item.Name, item => item.Norm);
            var byCategory = abonents.GroupBy(abonent => abonent.HouseCategory!).ToDictionary(group => group.Key, group => group.ToList());

            var rows = directory.Keys.Union(byCategory.Keys)
                .OrderBy(name => name, StringComparer.CurrentCulture)
                .Select(name =>
                {
                    var group = byCategory.GetValueOrDefault(name) ?? [];
                    var norm = directory.GetValueOrDefault(name) ?? (stored.TryGetValue(name, out var saved) ? saved : null);
                    return new HeatNormRowDto(name, group.Count, group.Count(item => item.HouseHeatNorm is null), norm,
                        group.Where(item => item.HouseHeatNorm is { } value && value > 0).Select(item => item.HouseHeatNorm!.Value).Distinct().OrderBy(value => value).ToList(),
                        directory.ContainsKey(name));
                })
                .ToList();
            return new HeatNormsDto(rows, withoutCategory);
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Норма категории проставляется всем абонентам этой категории. Начисления пересчитаются при следующем расчёте.
        api.MapPost("/heat-norms/apply", async (ApplyHeatNormRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Category)) throw new ApiError(422, "Категория не указана");
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите основание");
            if (request.Norm <= 0 || request.Norm != Math.Round(request.Norm, 6) || request.Norm > 10)
                throw new ApiError(422, "Норма должна быть больше нуля и не больше шести знаков после запятой");

            var category = request.Category.Trim();
            await using var transaction = await db.Database.BeginTransactionAsync();
            // Норма живёт в справочнике «Категории домов»: пишем её туда, чтобы справочник и абоненты не расходились
            var table = await db.ReferenceTables.FromSqlInterpolated($"SELECT * FROM \"ReferenceTables\" WHERE \"Name\" = {Catalog.HouseCategories} FOR UPDATE").FirstOrDefaultAsync();
            var columns = table is null ? [] : DirectoryData.Columns(table);
            int nameAt = columns.IndexOf(Catalog.HouseCategoryName), normAt = columns.IndexOf(Catalog.HouseCategoryNorm);
            var rows = table is null ? [] : DirectoryData.Rows(table);
            var own = rows.Where(row => DirectoryData.Cell(row, nameAt) == category).ToList();
            if (table is null || nameAt < 0 || own.Count == 0)
                throw new ApiError(404, $"Категории «{category}» нет в справочнике «{Catalog.HouseCategories}». Заведите её там или смените категорию абонентам.");
            if (normAt >= 0)
            {
                foreach (var row in own) row[normAt] = DirectoryData.FormatNorm(request.Norm);
                DirectoryData.SetRows(table, rows);
            }

            var total = await db.Abonents.CountAsync(abonent => abonent.HouseCategory == category);
            var changed = await DirectoryLinks.ApplyNorm(db, category, request.Norm);

            db.AddJournal(null, "Норма теплопотребления", user.Who(),
                $"Категория «{category}»: норма {request.Norm:0.######} Гкал/м². Изменено абонентов: {changed} из {total}", request.Reason.Trim());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new { Category = category, Norm = request.Norm, Total = total, Changed = changed };
        }).Roles(Roles.Accountant, Roles.Admin);
    }
}
