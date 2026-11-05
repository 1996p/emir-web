using Emir.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

public record RecloseChange(string AbonentId, string Abonent, string ServiceId, string Service, long OldAmount, long NewAmount, decimal? OldPrice, decimal? NewPrice)
{
    public long Delta => NewAmount - OldAmount;
}

public record ReclosePreview(string Period, string Title, long Delta, List<RecloseChange> Changes, List<ChargeIssue> Issues, List<ChargeIssue> Blocking);

/// <summary>
/// Пересчитывает открытый на исправление месяц и проводит разницу.
/// Площадь, жильцы и норматив берутся из первоначального начисления: они не должны менять закрытый месяц задним числом.
/// Пени не пересчитываются: история долгов по дням не хранится.
/// </summary>
public static class ReclosingService
{
    private sealed record Item(RecloseChange Change, Abonent Abonent, ServiceLine Line, PlannedCharge? Planned, ChargeEntry? Entry, PeriodReading? Reading);

    private static async Task<(AccountingMonth Month, List<Item> Items, List<ChargeIssue> Issues, List<ChargeIssue> Blocking)> Compute(EmirDb db)
    {
        var month = await db.AccountingMonths.FirstOrDefaultAsync(item => item.ReopenedAtUtc != null) ?? throw new ApiError(409, "Нет месяца, открытого на исправление");
        var (items, issues, blocking) = await ComputeFor(db, month.Period);
        return (month, items, issues, blocking);
    }

    /// <summary>Что не совпадает между начислениями месяца и расчётом. includeMissing: считать ошибкой услугу без начисления.</summary>
    public static async Task<List<RecloseChange>> Differences(EmirDb db, string period, bool includeMissing)
    {
        var (items, _, _) = await ComputeFor(db, period);
        return items.Where(item => includeMissing || item.Entry is not null).Select(item => item.Change).ToList();
    }

    private static async Task<(List<Item> Items, List<ChargeIssue> Issues, List<ChargeIssue> Blocking)> ComputeFor(EmirDb db, string period)
    {
        var abonents = await db.Abonents.Include(abonent => abonent.Services).ToListAsync();
        var entries = await db.ChargeEntries.Where(entry => entry.Period == period && entry.ServiceId != "penalty").ToListAsync();
        var readings = await db.PeriodReadings.Where(reading => reading.Period == period).ToListAsync();
        var plan = await ChargingService.Plan(db, abonents, period, entries);

        var items = new List<Item>();
        var issues = new List<ChargeIssue>();
        var blocking = new List<ChargeIssue>();
        var seen = new HashSet<(string, string)>();

        foreach (var planned in plan)
        {
            var (abonent, line) = (planned.Abonent, planned.Line);
            seen.Add((abonent.Id, line.ServiceId));
            var entry = entries.FirstOrDefault(item => item.AbonentId == abonent.Id && item.ServiceId == line.ServiceId);
            var reading = readings.FirstOrDefault(item => item.AbonentId == abonent.Id && item.ServiceId == line.ServiceId);
            var old = Applied(entry, reading);

            if (planned.Result.Error is { } error)
            {
                var issue = new ChargeIssue(abonent.ShortName, line.Name, error);
                issues.Add(issue);
                // Иначе ошибка в данных молча сняла бы уже начисленную сумму
                if (old != 0) blocking.Add(issue);
                continue;
            }

            var amount = planned.Result.Amount;
            // По счётчику долг уже изменён при вводе показания, но запись начисления ещё нужно привести к новой сумме
            var unchanged = entry is null ? amount == 0 : amount == old && entry.Amount == amount && entry.Price == planned.Price;
            if (unchanged) continue;
            items.Add(new Item(new RecloseChange(abonent.Id, abonent.ShortName, line.ServiceId, line.Name, old, amount, entry?.Price, planned.Price), abonent, line, planned, entry, reading));
        }

        // Услуга в этом месяце больше не действует: начисление снимается
        foreach (var abonent in abonents)
        {
            foreach (var line in abonent.Services.Where(item => item.ServiceId != "penalty" && !seen.Contains((abonent.Id, item.ServiceId))))
            {
                var entry = entries.FirstOrDefault(item => item.AbonentId == abonent.Id && item.ServiceId == line.ServiceId);
                var reading = readings.FirstOrDefault(item => item.AbonentId == abonent.Id && item.ServiceId == line.ServiceId);
                var old = Applied(entry, reading);
                if (entry is null && old == 0) continue;
                items.Add(new Item(new RecloseChange(abonent.Id, abonent.ShortName, line.ServiceId, line.Name, old, 0, entry?.Price, null), abonent, line, null, entry, reading));
            }
        }
        return (items, issues, blocking);
    }

    private static long Applied(ChargeEntry? entry, PeriodReading? reading) =>
        (entry is { AppliedToDebt: true } ? entry.Amount : 0) + (reading?.AppliedAmount ?? 0);

    public static async Task<ReclosePreview> Preview(EmirDb db)
    {
        var (month, items, issues, blocking) = await Compute(db);
        return new ReclosePreview(month.Period, Periods.Title(month.Period), items.Sum(item => item.Change.Delta), items.Select(item => item.Change).ToList(), issues, blocking);
    }

    /// <summary>expectedDelta: сумма, которую видел бухгалтер. Если данные успели измениться, проводить нельзя.</summary>
    public static async Task<ReclosePreview> Apply(EmirDb db, long expectedDelta, string who, string reason)
    {
        var (month, items, issues, blocking) = await Compute(db);
        if (blocking.Count > 0) throw new ApiError(409, $"Исправьте ошибки в расчёте: {blocking.Count}");
        var delta = items.Sum(item => item.Change.Delta);
        if (delta != expectedDelta) throw new ApiError(409, "Данные изменились после просмотра. Посмотрите разницу ещё раз");

        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            var change = item.Change;
            db.ChargeRevisions.Add(new ChargeRevision
            {
                Period = month.Period, AbonentId = change.AbonentId, ServiceId = change.ServiceId, OldAmount = change.OldAmount, NewAmount = change.NewAmount,
                OldPrice = change.OldPrice, NewPrice = change.NewPrice, Who = who, AtUtc = now, Reason = reason,
            });

            if (change.Delta > 0) item.Line.Debt += change.Delta;
            else ChargingService.ReduceDebt(item.Abonent, item.Line, -change.Delta);

            if (item.Planned is null)
            {
                if (item.Entry is not null) db.ChargeEntries.Remove(item.Entry);
                if (item.Reading is not null) item.Reading.AppliedAmount = 0;
            }
            else
            {
                var entry = item.Entry;
                if (entry is null)
                {
                    entry = new ChargeEntry { AbonentId = change.AbonentId, ServiceId = change.ServiceId, Period = month.Period, Scheme = "", Area = item.Abonent.Area, Residents = item.Abonent.Residents, ContractNorm = item.Line.ContractNorm };
                    db.ChargeEntries.Add(entry);
                }
                entry.Scheme = item.Planned.Tariff!.Scheme;
                entry.TariffId = item.Planned.Tariff.Id;
                entry.Price = item.Planned.Price;
                entry.Volume = item.Planned.Result.Volume;
                entry.Amount = change.NewAmount;
                entry.AppliedToDebt = !item.Planned.AlreadyInDebt;
                entry.AtUtc = now;
                if (item.Reading is not null) item.Reading.AppliedAmount = item.Planned.AlreadyInDebt ? change.NewAmount : 0;
            }
            db.Journal.Add(new JournalRecord
            {
                AbonentId = change.AbonentId, AtUtc = now, Action = "Перезакрытие месяца", Who = who, Reason = reason,
                Details = $"{Periods.Label(month.Period)}, {change.Service}: было {Money(change.OldAmount)} ₽, стало {Money(change.NewAmount)} ₽",
            });
        }

        // Суммы изменились: выпущенные документы этих абонентов больше не верны
        var changed = items.Where(item => item.Change.Delta != 0 || item.Change.OldPrice != item.Change.NewPrice).Select(item => item.Change.AbonentId).Distinct().ToList();
        var stale = await db.BillingDocuments.Where(document => document.Period == month.Period && document.Status == "active" && changed.Contains(document.AbonentId)).ToListAsync();
        foreach (var document in stale) document.Stale = true;

        month.ReopenedAtUtc = null;
        month.ReopenedBy = null;
        month.ReopenReason = null;
        month.ReclosedAtUtc = now;
        month.ReclosedBy = who;
        month.ReclosedCount++;
        // В строках услуг снова условия последнего месяца
        await Periods.ApplyTerms(db, (await Periods.Latest(db)).Period);
        return new ReclosePreview(month.Period, Periods.Title(month.Period), delta, items.Select(item => item.Change).ToList(), issues, []);
    }

    private static string Money(long kopecks) => (kopecks / 100m).ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
}
