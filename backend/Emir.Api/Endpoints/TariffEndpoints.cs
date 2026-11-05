using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record TariffPriceDto(string FromPeriod, decimal Price, string Who, string When, string? Reason);
public record TariffDto(string Id, string Name, string Scheme, decimal? CurrentPrice, int Lines, List<TariffPriceDto> Prices);
public record TariffsDto(string OpenPeriod, bool Reopened, List<TariffDto> Tariffs);
public record SetPriceRequest(string FromPeriod, decimal Price, string Reason);
public record CancelPriceRequest(string Reason);
public record ServiceTermDto(string FromPeriod, string? TariffId, string TariffName, string State, string Who, string When, string? Reason);
public record SetTermRequest(string FromPeriod, string? TariffId, string State, string Reason);

public static class TariffEndpoints
{
    private static string When(DateTime atUtc) => atUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    private static async Task<List<(string, string)>> LinesOn(EmirDb db, string tariffId, string period)
    {
        var terms = await db.ServiceTerms.Where(term => string.Compare(term.FromPeriod, period) <= 0).ToListAsync();
        return terms.GroupBy(term => (term.AbonentId, term.ServiceId))
            .Select(group => group.MaxBy(term => term.FromPeriod, StringComparer.Ordinal)!)
            .Where(term => term.TariffId == tariffId)
            .Select(term => (term.AbonentId, term.ServiceId)).ToList();
    }

    public static void MapTariffs(this RouteGroupBuilder api)
    {
        api.MapGet("/tariffs", async (EmirDb db) =>
        {
            var tariffs = await db.Tariffs.OrderBy(tariff => tariff.Name).ToListAsync();
            var prices = await db.TariffPrices.ToListAsync();
            var lines = await db.ServiceLines.Where(line => line.TariffId != null).GroupBy(line => line.TariffId!).Select(group => new { Id = group.Key, Count = group.Count() }).ToListAsync();
            var work = await Periods.Work(db);
            var open = work.Period;
            return new TariffsDto(open, work.ReopenedAtUtc is not null, tariffs.Select(tariff => new TariffDto(
                tariff.Id, tariff.Name, tariff.Scheme, TariffHistory.PriceOn(prices, tariff.Id, open)?.Price, lines.FirstOrDefault(item => item.Id == tariff.Id)?.Count ?? 0,
                prices.Where(price => price.TariffId == tariff.Id).OrderByDescending(price => price.FromPeriod, StringComparer.Ordinal)
                    .Select(price => new TariffPriceDto(price.FromPeriod, price.Price, price.Who, When(price.AtUtc), price.Reason)).ToList())).ToList());
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        api.MapPost("/tariffs/{id}/prices", async (string id, SetPriceRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите основание: например, номер приказа");
            if (request.Price <= 0 || request.Price != Math.Round(request.Price, 4) || request.Price > 1_000_000) throw new ApiError(422, "Цена должна быть больше нуля и иметь не больше четырёх знаков после запятой");

            await using var transaction = await db.BeginMoneyTransaction();
            var tariff = await db.Tariffs.FirstOrDefaultAsync(item => item.Id == id) ?? throw new ApiError(404, "Тариф не найден");
            await TariffHistory.EnsureCanChange(db, request.FromPeriod, await LinesOn(db, id, request.FromPeriod));

            var existing = await db.TariffPrices.FirstOrDefaultAsync(price => price.TariffId == id && price.FromPeriod == request.FromPeriod);
            var was = existing is null ? "" : $"было {existing.Price:0.####}, ";
            if (existing is null)
            {
                existing = new TariffPrice { TariffId = id, FromPeriod = request.FromPeriod, Price = request.Price, Who = user.Who(), AtUtc = DateTime.UtcNow, Reason = request.Reason.Trim() };
                db.TariffPrices.Add(existing);
            }
            else
            {
                existing.Price = request.Price;
                existing.Who = user.Who();
                existing.AtUtc = DateTime.UtcNow;
                existing.Reason = request.Reason.Trim();
            }
            db.AddJournal(null, "Цена тарифа", user.Who(), $"{tariff.Name}: с {request.FromPeriod} {was}цена {request.Price:0.####} ₽", request.Reason.Trim());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        }).Roles(Roles.Accountant, Roles.Admin);

        api.MapPost("/tariffs/{id}/prices/{fromPeriod}/cancel", async (string id, string fromPeriod, CancelPriceRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите причину");
            await using var transaction = await db.BeginMoneyTransaction();
            var tariff = await db.Tariffs.FirstOrDefaultAsync(item => item.Id == id) ?? throw new ApiError(404, "Тариф не найден");
            var price = await db.TariffPrices.FirstOrDefaultAsync(item => item.TariffId == id && item.FromPeriod == fromPeriod) ?? throw new ApiError(404, "Такой цены нет");
            await TariffHistory.EnsureCanChange(db, fromPeriod, await LinesOn(db, id, fromPeriod));
            // Без более ранней цены услуги этого тарифа перестанут считаться
            if (!await db.TariffPrices.AnyAsync(item => item.TariffId == id && string.Compare(item.FromPeriod, fromPeriod) < 0)) throw new ApiError(409, "Это первая цена тарифа, её нельзя убрать");
            db.TariffPrices.Remove(price);
            db.AddJournal(null, "Отмена цены тарифа", user.Who(), $"{tariff.Name}: с {fromPeriod} цена {price.Price:0.####} ₽", request.Reason.Trim());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        }).Roles(Roles.Accountant, Roles.Admin);

        api.MapGet("/abonents/{id}/services/{serviceId}/terms", async (string id, string serviceId, EmirDb db) =>
        {
            var terms = await db.ServiceTerms.Where(term => term.AbonentId == id && term.ServiceId == serviceId).ToListAsync();
            var tariffs = await db.Tariffs.ToDictionaryAsync(tariff => tariff.Id);
            return terms.OrderByDescending(term => term.FromPeriod, StringComparer.Ordinal).Select(term => new ServiceTermDto(
                term.FromPeriod, term.TariffId, term.TariffId is not null && tariffs.TryGetValue(term.TariffId, out var tariff) ? tariff.Name : "Не выбран", term.State, term.Who, When(term.AtUtc), term.Reason));
        }).RequireAuthorization();

        api.MapPost("/abonents/{id}/services/{serviceId}/terms", async (string id, string serviceId, SetTermRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите причину");
            if (!TariffHistory.States.Contains(request.State)) throw new ApiError(422, "Неизвестное состояние услуги");

            await using var transaction = await db.BeginMoneyTransaction();
            var abonent = await db.GetAbonent(id);
            var line = abonent.Services.FirstOrDefault(item => item.ServiceId == serviceId) ?? throw new ApiError(404, "Услуга не найдена");
            if (serviceId == "penalty") throw new ApiError(422, "Пени считаются по своей настройке");
            var tariff = request.TariffId is null ? null : await db.Tariffs.FirstOrDefaultAsync(item => item.Id == request.TariffId) ?? throw new ApiError(422, "Тариф не найден");
            await TariffHistory.EnsureCanChange(db, request.FromPeriod, [(id, serviceId)]);

            var term = await db.ServiceTerms.FirstOrDefaultAsync(item => item.AbonentId == id && item.ServiceId == serviceId && item.FromPeriod == request.FromPeriod);
            if (term is null)
            {
                term = new ServiceTerm { AbonentId = id, ServiceId = serviceId, FromPeriod = request.FromPeriod, State = request.State, Who = user.Who() };
                db.ServiceTerms.Add(term);
            }
            term.TariffId = request.TariffId;
            term.State = request.State;
            term.Who = user.Who();
            term.AtUtc = DateTime.UtcNow;
            term.Reason = request.Reason.Trim();

            if (request.FromPeriod == await Periods.WorkPeriod(db))
            {
                TariffHistory.ApplyCurrent(line, term, tariff);
                line.ChargeStatus = "Ждёт начисления";
            }
            db.AddJournal(id, "Условия услуги", user.Who(), $"{line.Name}: с {request.FromPeriod} {tariff?.Name ?? "без тарифа"}, {request.State}", request.Reason.Trim());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);
    }
}
