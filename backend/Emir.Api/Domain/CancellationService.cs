using Emir.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

public record CancelFilter(string? Kind, string? Place, string? Query, IReadOnlyCollection<string> Services);
public record CancelLine(string AbonentId, string Abonent, string Account, string ServiceId, string Service, long Amount);
public record CancelOption(string Id, string Name, long Total);
public record CancelPreview(string Period, string Title, long Total, List<CancelLine> Lines, List<CancelOption> Services, List<string> Places, bool ChargesDone, bool PenaltyDone);

/// <summary>
/// Массовая отмена начислений открытого месяца. Закрытый месяц меняется только через перезакрытие.
/// Показание счётчика остаётся: снимается только сумма, повторное начисление вернёт её.
/// </summary>
public static class CancellationService
{
    private sealed record Item(CancelLine Line, Abonent Abonent, ServiceLine Service, ChargeEntry? Entry, PeriodReading? Reading);

    private static long Applied(ChargeEntry? entry, PeriodReading? reading) =>
        (entry is { AppliedToDebt: true } ? entry.Amount : 0) + (reading?.AppliedAmount ?? 0);

    private static async Task<(AccountingMonth Month, List<Item> All)> Load(EmirDb db)
    {
        var month = await Periods.Work(db);
        var abonents = await db.Abonents.Include(item => item.Services).OrderBy(item => item.Account).ToListAsync();
        var entries = await db.ChargeEntries.Where(entry => entry.Period == month.Period).ToListAsync();
        var readings = await db.PeriodReadings.Where(reading => reading.Period == month.Period).ToListAsync();

        var items = new List<Item>();
        foreach (var abonent in abonents)
        {
            foreach (var service in abonent.Services.OrderBy(line => line.Position))
            {
                var entry = entries.FirstOrDefault(item => item.AbonentId == abonent.Id && item.ServiceId == service.ServiceId);
                var reading = readings.FirstOrDefault(item => item.AbonentId == abonent.Id && item.ServiceId == service.ServiceId);
                var amount = Applied(entry, reading);
                if (entry is null && amount == 0) continue;
                items.Add(new Item(new CancelLine(abonent.Id, abonent.ShortName, abonent.Account, service.ServiceId, service.Name, amount), abonent, service, entry, reading));
            }
        }
        return (month, items);
    }

    private static bool Matches(Item item, CancelFilter filter)
    {
        if (!filter.Services.Contains(item.Service.ServiceId)) return false;
        if (!string.IsNullOrEmpty(filter.Kind) && item.Abonent.Kind != filter.Kind) return false;
        if (!string.IsNullOrEmpty(filter.Place) && item.Abonent.Place != filter.Place) return false;
        if (string.IsNullOrWhiteSpace(filter.Query)) return true;
        var query = filter.Query.Trim();
        return new[] { item.Abonent.Name, item.Abonent.ShortName, item.Abonent.Account, item.Abonent.Street }.Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<CancelPreview> Preview(EmirDb db, CancelFilter filter)
    {
        var (month, items) = await Load(db);
        var lines = items.Where(item => Matches(item, filter)).Select(item => item.Line).ToList();
        var services = items.GroupBy(item => (item.Service.ServiceId, item.Service.Name)).OrderBy(group => group.Key.Name)
            .Select(group => new CancelOption(group.Key.ServiceId, group.Key.Name, group.Sum(item => item.Line.Amount))).ToList();
        var places = await db.Abonents.Where(item => item.Place != "").Select(item => item.Place).Distinct().OrderBy(place => place).ToListAsync();
        var steps = await db.PeriodSteps.ToListAsync();
        return new CancelPreview(month.Period, Periods.Title(month.Period), lines.Sum(line => line.Amount), lines, services, places,
            steps.Any(step => step.Id == "charges" && step.State == "done"), steps.Any(step => step.Id == "penalty" && step.State == "done"));
    }

    /// <summary>expectedTotal и expectedLines: то, что видел бухгалтер. Если данные изменились, отмена не проводится.</summary>
    public static async Task<(string Period, int Lines, long Total)> Apply(EmirDb db, CancelFilter filter, long expectedTotal, int expectedLines, string who, string reason)
    {
        await Periods.EnsureNotReopened(db);
        await ChargingService.EnsureOpen(db);
        var (month, all) = await Load(db);
        var items = all.Where(item => Matches(item, filter)).ToList();
        if (items.Count == 0) throw new ApiError(422, "Под условия не попало ни одного начисления");
        if (items.Sum(item => item.Line.Amount) != expectedTotal || items.Count != expectedLines) throw new ApiError(409, "Данные изменились после просмотра. Посмотрите список ещё раз");

        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            db.ChargeRevisions.Add(new ChargeRevision
            {
                Period = month.Period, AbonentId = item.Abonent.Id, ServiceId = item.Service.ServiceId, OldAmount = item.Line.Amount, NewAmount = 0,
                OldPrice = item.Entry?.Price, NewPrice = null, Who = who, AtUtc = now, Reason = $"Отмена: {reason}",
            });
            ChargingService.ReduceDebt(item.Abonent, item.Service, item.Line.Amount);
            if (item.Entry is not null) db.ChargeEntries.Remove(item.Entry);
            if (item.Reading is not null) item.Reading.AppliedAmount = 0;
            item.Service.ChargeStatus = "Начисление отменено";
        }
        foreach (var group in items.GroupBy(item => item.Abonent.Id))
        {
            var details = string.Join(", ", group.Select(item => $"{item.Service.Name} {item.Line.Amount / 100m:N2} ₽"));
            db.Journal.Add(new JournalRecord { AbonentId = group.Key, AtUtc = now, Action = "Отмена начислений", Who = who, Reason = reason, Details = $"{Periods.Label(month.Period)}: {details}" });
        }

        // Шаги начинаются заново: начисление и пени пересчитают только то, чего теперь не хватает
        Periods.ResetSteps(await db.PeriodSteps.ToListAsync());
        return (month.Period, items.Count, items.Sum(item => item.Line.Amount));
    }
}
