using Emir.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

/// <summary>
/// Расчётные месяцы. Рабочий месяц: тот, что открыт на исправление, иначе последний.
/// Цепочка: открыт → закрыт → следующий открыт. Закрытый последний месяц можно открыть на исправление.
/// </summary>
public static class Periods
{
    private static readonly string[] Names = ["январь", "февраль", "март", "апрель", "май", "июнь", "июль", "август", "сентябрь", "октябрь", "ноябрь", "декабрь"];

    /// <summary>«ноябрь 2026»</summary>
    public static string Label(string period) => $"{Month(period)} {period[..4]}";

    /// <summary>«ноябрь»</summary>
    public static string Month(string period) => Names[int.Parse(period[5..]) - 1];

    /// <summary>«Ноябрь 2026»</summary>
    public static string Title(string period)
    {
        var label = Label(period);
        return char.ToUpperInvariant(label[0]) + label[1..];
    }

    public static string Next(string period) => PenaltyEngine.FirstDay(period).AddMonths(1).ToString("yyyy-MM");

    /// <summary>Отмечает, какая операция сейчас меняет деньги: см. журнал LedgerEntries.</summary>
    public static async Task BeginOperation(this EmirDb db, string kind, string who, string? reference = null) =>
        db.Operation = new MoneyOperation(kind, who, await WorkPeriod(db), reference);

    public static async Task<AccountingMonth> Latest(EmirDb db) =>
        await db.AccountingMonths.OrderByDescending(month => month.Period).FirstOrDefaultAsync()
            ?? throw new ApiError(409, "Расчётный месяц не задан. Выполните команду start-period");

    public static async Task<AccountingMonth> Work(EmirDb db) =>
        await db.AccountingMonths.FirstOrDefaultAsync(month => month.ReopenedAtUtc != null) ?? await Latest(db);

    public static async Task<string> WorkPeriod(EmirDb db) => (await Work(db)).Period;

    public static string WorkPeriodNow(EmirDb db)
    {
        var months = db.AccountingMonths.AsNoTracking().ToList();
        return (months.FirstOrDefault(month => month.ReopenedAtUtc != null) ?? months.MaxBy(month => month.Period, StringComparer.Ordinal))?.Period
            ?? throw new ApiError(409, "Расчётный месяц не задан. Выполните команду start-period");
    }

    /// <summary>Пока месяц исправляется, нельзя ни считать другой месяц, ни запускать шаги.</summary>
    public static async Task EnsureNotReopened(EmirDb db)
    {
        var month = await db.AccountingMonths.FirstOrDefaultAsync(item => item.ReopenedAtUtc != null);
        if (month is not null) throw new ApiError(409, $"Идёт исправление месяца: {Label(month.Period)}. Сначала завершите перезакрытие");
    }

    public static void ResetSteps(IEnumerable<PeriodStepState> steps)
    {
        foreach (var step in steps)
        {
            (step.State, step.Status) = step.Id switch
            {
                "readings" => ("done", "Выполнено"),
                "charges" => ("current", "Нужно запустить"),
                "checks" => ("next", "После начислений"),
                "penalty" => ("next", "После проверки"),
                "close" => ("next", "После пени"),
                _ => ("next", "После закрытия"),
            };
        }
    }

    public static IEnumerable<PeriodStepState> NewSteps()
    {
        var steps = new[] { "readings", "charges", "checks", "penalty", "close", "docs" }.Select((id, index) => new PeriodStepState { Id = id, Position = index, State = "", Status = "" }).ToList();
        ResetSteps(steps);
        return steps;
    }

    /// <summary>Причина, по которой закрытый месяц нельзя открыть на исправление. Null, если можно.</summary>
    public static async Task<string?> WhyCannotReopen(EmirDb db, AccountingMonth month)
    {
        if (month.ClosedAtUtc is null) return "Месяц ещё не закрыт";
        if (month.ReopenedAtUtc is not null) return "Месяц уже открыт на исправление";
        var latestClosed = await db.AccountingMonths.Where(item => item.ClosedAtUtc != null).OrderByDescending(item => item.Period).FirstAsync();
        if (latestClosed.Period != month.Period) return "Можно исправлять только последний закрытый месяц. Для более ранних используйте корректировку";
        var next = Next(month.Period);
        if (await db.ChargeEntries.AnyAsync(entry => entry.Period == next) || await db.PeriodReadings.AnyAsync(reading => reading.Period == next))
            return $"За {Label(next)} уже есть начисления или показания. Используйте корректировку";
        return null;
    }

    public static async Task<AccountingMonth> OpenNext(EmirDb db)
    {
        await EnsureNotReopened(db);
        var latest = await Latest(db);
        if (latest.ClosedAtUtc is null) throw new ApiError(409, $"Сначала закройте месяц: {Label(latest.Period)}");

        var month = new AccountingMonth { Period = Next(latest.Period) };
        db.AccountingMonths.Add(month);
        ResetSteps(await db.PeriodSteps.ToListAsync());
        await ApplyTerms(db, month.Period);
        foreach (var line in await db.ServiceLines.Where(line => line.ServiceId != "penalty").ToListAsync())
            line.ChargeStatus = line.MeterNumber is null ? "Ждёт начисления" : $"Нет показаний за {Month(month.Period)}";
        foreach (var line in await db.ServiceLines.Where(line => line.ServiceId == "penalty").ToListAsync())
            line.ChargeStatus = "Ждёт начисления";
        return month;
    }

    /// <summary>Показывает в карточке площадь, жильцов и условия услуг указанного месяца.</summary>
    public static async Task ApplyTerms(EmirDb db, string period)
    {
        var facts = await db.AbonentFacts.ToListAsync();
        foreach (var abonent in await db.Abonents.ToListAsync())
        {
            if (TariffHistory.FactOn(facts, abonent.Id, period) is not { } fact) continue;
            abonent.Area = fact.Area;
            abonent.Residents = fact.Residents;
        }

        var terms = await db.ServiceTerms.ToListAsync();
        var tariffs = await db.Tariffs.ToDictionaryAsync(tariff => tariff.Id);
        foreach (var line in await db.ServiceLines.Where(line => line.ServiceId != "penalty").ToListAsync())
        {
            var term = TariffHistory.TermOn(terms, line.AbonentId, line.ServiceId, period);
            TariffHistory.ApplyCurrent(line, term, term?.TariffId is { } id && tariffs.TryGetValue(id, out var tariff) ? tariff : null);
        }
    }
}
