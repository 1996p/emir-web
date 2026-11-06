using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

/// <summary>Category — категория дома; Abonents — сколько абонентов в ней; WithoutNorm — у скольких из них не задана норма;
/// Norm — сохранённая норма категории (если задана); Distinct — какие нормы реально стоят у абонентов этой категории.</summary>
public record HeatNormRowDto(string Category, int Abonents, int WithoutNorm, decimal? Norm, List<decimal> Distinct);
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

            var rows = abonents.GroupBy(abonent => abonent.HouseCategory!)
                .OrderBy(group => group.Key, StringComparer.CurrentCulture)
                .Select(group => new HeatNormRowDto(
                    group.Key,
                    group.Count(),
                    group.Count(item => item.HouseHeatNorm is null),
                    stored.TryGetValue(group.Key, out var norm) ? norm : null,
                    group.Where(item => item.HouseHeatNorm is { } value && value > 0)
                        .Select(item => item.HouseHeatNorm!.Value).Distinct().OrderBy(value => value).ToList()))
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
            var abonents = await db.Abonents.Where(abonent => abonent.HouseCategory == category).ToListAsync();
            if (abonents.Count == 0) throw new ApiError(404, "В этой категории нет абонентов");

            var stored = await db.HeatNormCategories.FirstOrDefaultAsync(item => item.Category == category);
            if (stored is null) db.HeatNormCategories.Add(new HeatNormCategory { Category = category, Norm = request.Norm });
            else stored.Norm = request.Norm;

            var changed = 0;
            foreach (var abonent in abonents)
                if (abonent.HouseHeatNorm != request.Norm) { abonent.HouseHeatNorm = request.Norm; changed++; }

            db.AddJournal(null, "Норма теплопотребления", user.Who(),
                $"Категория «{category}»: норма {request.Norm:0.######} Гкал/м². Изменено абонентов: {changed} из {abonents.Count}", request.Reason.Trim());
            await db.SaveChangesAsync();
            return new { Category = category, Norm = request.Norm, Total = abonents.Count, Changed = changed };
        }).Roles(Roles.Accountant, Roles.Admin);
    }
}
