using System.Text.RegularExpressions;
using Emir.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

/// <summary>
/// История цен тарифов и условий услуги. Запись действует с указанного месяца до следующей записи.
/// Расчёт помесячный, поэтому смена внутри месяца не делится на части.
/// </summary>
public static partial class TariffHistory
{
    /// <summary>Записи, действовавшие до начала истории.</summary>
    public const string Beginning = "2000-01";

    public const string Working = "Работает";
    public static readonly string[] States = [Working, "Приостановлена", "Расторгнута"];

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$")]
    private static partial Regex PeriodFormat();

    public static TariffPrice? PriceOn(IEnumerable<TariffPrice> prices, string tariffId, string period) =>
        prices.Where(price => price.TariffId == tariffId && string.CompareOrdinal(price.FromPeriod, period) <= 0)
            .MaxBy(price => price.FromPeriod, StringComparer.Ordinal);

    public static ServiceTerm? TermOn(IEnumerable<ServiceTerm> terms, string abonentId, string serviceId, string period) =>
        terms.Where(term => term.AbonentId == abonentId && term.ServiceId == serviceId && string.CompareOrdinal(term.FromPeriod, period) <= 0)
            .MaxBy(term => term.FromPeriod, StringComparer.Ordinal);

    public static AbonentFact? FactOn(IEnumerable<AbonentFact> facts, string abonentId, string period) =>
        facts.Where(fact => fact.AbonentId == abonentId && string.CompareOrdinal(fact.FromPeriod, period) <= 0)
            .MaxBy(fact => fact.FromPeriod, StringComparer.Ordinal);

    /// <summary>Проверяет месяц: прошлые не меняются, в открытом менять можно только до первых начислений.</summary>
    public static async Task EnsureCanChange(EmirDb db, string fromPeriod, IReadOnlyCollection<(string AbonentId, string ServiceId)> lines)
    {
        if (fromPeriod is null || !PeriodFormat().IsMatch(fromPeriod)) throw new ApiError(422, "Укажите месяц в формате ГГГГ-ММ");
        var work = await Periods.Work(db);
        if (string.CompareOrdinal(fromPeriod, work.Period) < 0) throw new ApiError(409, "Прошлый месяц закрыт для изменений. Выберите открытый или будущий месяц");
        if (fromPeriod != work.Period) return;
        // Месяц открыт на исправление: разницу покажет и проведёт перезакрытие
        if (work.ReopenedAtUtc is not null) return;

        await ChargingService.EnsureOpen(db);
        var charged = await db.ChargeEntries.Where(entry => entry.Period == fromPeriod && entry.ServiceId != "penalty").Select(entry => new { entry.AbonentId, entry.ServiceId }).ToListAsync();
        var read = await db.PeriodReadings.Where(reading => reading.Period == fromPeriod).Select(reading => new { reading.AbonentId, reading.ServiceId }).ToListAsync();
        var used = charged.Select(item => (item.AbonentId, item.ServiceId)).Concat(read.Select(item => (item.AbonentId, item.ServiceId))).ToHashSet();
        if (lines.Any(used.Contains)) throw new ApiError(409, "За этот месяц уже есть начисления или показания. Укажите следующий месяц");
    }

    /// <summary>Переносит в строку услуги условия открытого месяца: карточка абонента показывает их без расчёта.</summary>
    public static void ApplyCurrent(ServiceLine line, ServiceTerm? term, Tariff? tariff)
    {
        if (term is null) return;
        line.State = term.State;
        line.TariffId = term.TariffId;
        line.TariffName = tariff?.Name ?? "Не выбран";
    }
}
