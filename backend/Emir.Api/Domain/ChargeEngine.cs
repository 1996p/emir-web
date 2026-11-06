using System.Globalization;

namespace Emir.Api.Domain;

/// <summary>Данные дома для схем по общедомовому счётчику.</summary>
public record HouseMeterData(decimal Usage, decimal IndividualUsage, decimal TotalArea, decimal IndividualArea, int TotalResidents, int IndividualResidents);

public record ChargeInput(
    string Scheme,
    decimal Price,
    int Month,
    decimal Area = 0,
    int Residents = 0,
    string? ServiceNorm = null,
    string? TariffNorm = null,
    string? ContractNorm = null,
    decimal? HouseHeatNorm = null,
    decimal? MeterUsage = null,
    decimal? YearSharePercent = null,
    bool HeatingSeason = true,
    decimal? HouseLoss = null,
    decimal? HouseArea = null,
    HouseMeterData? HouseMeter = null);

/// <summary>Volume — объём Q, Amount — сумма M в копейках, Error — почему посчитать нельзя.</summary>
public record ChargeResult(decimal Volume, long Amount, string? Error = null)
{
    public static ChargeResult Fail(string error) => new(0, 0, error);
}

/// <summary>Схемы начисления из документации ЕМИР ЖКХ. Сумма = объём × цена, округление до копеек.</summary>
public static class ChargeEngine
{
    public static readonly string[] Schemes =
    [
        "Norm", "AreaKrsk", "HeatHouseNorm", "HeatHouseNormKrsk", "HeatLost", "ContractNorm", "ServiceNorm", "MonthlyQuantityHouse", "NormHouse", "NormTarifContract",
        "MonthlyAmount", "AreaKrskNorm", "NormTarif", "AreaNormTarif", "AreaNormTarifSeasonal", "AreaNormTarifMonthly", "MeterApartment", "MeterHouse", "HouseMeterLeftover", "Area",
    ];

    public static ChargeResult Calculate(ChargeInput input)
    {
        decimal? serviceNorm = NormFor(input.ServiceNorm, input.Month);
        decimal? tariffNorm = NormFor(input.TariffNorm, input.Month);
        decimal? contractNorm = NormFor(input.ContractNorm, input.Month);

        ChargeResult Need(string what) => ChargeResult.Fail($"Нет данных: {what}");
        ChargeResult Result(decimal volume) => new(volume, Money(volume, input.Price));

        switch (input.Scheme)
        {
            case "Norm": return serviceNorm is { } n1 ? Result(n1 * input.Residents) : Need("норма по услуге");
            case "NormTarif": return tariffNorm is { } n2 ? Result(n2 * input.Residents) : Need("норматив по тарифу");
            case "ServiceNorm": return serviceNorm is { } n3 ? Result(n3) : Need("норма по услуге");
            case "AreaKrsk": return Result(contractNorm ?? input.Area);
            case "ContractNorm": return contractNorm is { } n4 ? Result(n4) : Need("норматив по заказу");
            case "MonthlyAmount": return contractNorm is { } n5 ? Result(n5) : Need("норматив по заказу на этот месяц");
            case "AreaKrskNorm": return serviceNorm is { } n6 && contractNorm is { } c6 ? Result(n6 * c6) : Need("норма по услуге и норматив по заказу");
            case "NormTarifContract": return tariffNorm is { } n7 && contractNorm is { } c7 ? Result(c7 * n7) : Need("норматив по тарифу и норматив по заказу");
            case "Area":
            case "HeatHouseNorm": return Result(input.Area);
            case "HeatHouseNormKrsk": return contractNorm is { } n8 ? Result(n8) : Need("норматив по заказу");
            case "HeatLost":
                return contractNorm is { } n9 && input.YearSharePercent is { } share ? Result(n9 * share / 100m) : Need("норматив по заказу и процент месяца");
            // Старая система округляет объём до 4 знаков и только потом умножает на цену: проверено на её начислениях за 2026 год.
            case "NormHouse": return input.HouseHeatNorm is { } h ? Result(Math.Round(input.Area * h, 4, MidpointRounding.AwayFromZero)) : Need("норма теплопотребления дома");
            case "AreaNormTarif":
            case "AreaNormTarifMonthly":
                return tariffNorm is { } n10 ? Result(input.Area * n10) : Need("норматив по тарифу");
            case "AreaNormTarifSeasonal":
                if (!input.HeatingSeason) return new ChargeResult(0, 0);
                return tariffNorm is { } n11 ? Result(input.Area * n11) : Need("норматив по тарифу");
            case "MonthlyQuantityHouse":
                if (input.HouseLoss is not { } loss || input.HouseArea is not { } houseArea || houseArea <= 0) return Need("потери тепла и площадь дома");
                return Result(loss * input.Area / houseArea);
            case "MeterApartment":
                return input.MeterUsage is { } usage ? Result(usage) : ChargeResult.Fail("Нет показаний за месяц");
            case "MeterHouse":
                if (input.HouseMeter is not { } m1) return Need("общедомовой счётчик");
                var restArea = m1.TotalArea - m1.IndividualArea;
                return restArea <= 0 ? Need("площадь дома без индивидуальных счётчиков") : Result(Math.Round((m1.Usage - m1.IndividualUsage) * input.Area / restArea, 4, MidpointRounding.AwayFromZero));
            case "HouseMeterLeftover":
                if (input.HouseMeter is not { } m2) return Need("общедомовой счётчик");
                var restPeople = m2.TotalResidents - m2.IndividualResidents;
                return restPeople <= 0 ? Need("проживающие без индивидуальных счётчиков") : Result((m2.Usage - m2.IndividualUsage) * input.Residents / restPeople);
            default:
                return ChargeResult.Fail($"Неизвестная схема {input.Scheme}");
        }
    }

    public static long Money(decimal volume, decimal priceRubles) =>
        (long)Math.Round(volume * priceRubles * 100m, MidpointRounding.AwayFromZero);

    /// <summary>Норма либо числом, либо помесячно: «01=1,5#02=2#12=3». Месяца нет в списке — нормы нет.</summary>
    public static decimal? NormFor(string? norm, int month)
    {
        if (string.IsNullOrWhiteSpace(norm)) return null;
        if (!norm.Contains('=')) return ParseNumber(norm);
        foreach (var part in norm.Split('#', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=');
            if (pair.Length == 2 && int.TryParse(pair[0], out var candidate) && candidate == month) return ParseNumber(pair[1]);
        }
        return null;
    }

    private static decimal? ParseNumber(string value) =>
        decimal.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : null;
}

public record PenaltyTier(int FromDay, decimal Fraction);
public record RefinancingRate(DateOnly From, decimal Rate);
public record PenaltySettings(string StartPeriod, int PayDayNextMonth, PenaltyTier[] Tiers, RefinancingRate[] Rates);
public record OutstandingDebt(string Period, long Amount);

/// <summary>
/// Расчёт пени. Значения настроек взяты из старой системы.
/// Допущения, которые нужно сверить с квитанциями:
/// 1) срок оплаты — указанное число следующего месяца, просрочка считается со следующего дня;
/// 2) ставка берётся на каждый день расчёта;
/// 3) оплаты гасят самые старые начисления.
/// </summary>
public static class PenaltyEngine
{
    // Просрочка считается от 11-го числа следующего месяца (оплата до 10-го включительно): так пени
    // совпадают с журналами старой системы точнее всего, первая пеня за апрель приходится на 11 июня.
    // В её настройках при этом указано 15 дней на оплату.
    public static readonly PenaltySettings Current = new(
        "2017-07", 11,
        [new(0, 0m), new(31, 1m / 300m), new(91, 1m / 130m)],
        new (string Date, decimal Rate)[]
        {
            ("2017-06-19", .09m), ("2017-09-18", .085m), ("2017-10-30", .0825m), ("2017-12-18", .0775m), ("2018-02-12", .075m), ("2018-03-26", .0725m), ("2018-09-14", .075m),
            ("2018-12-17", .0775m), ("2019-07-29", .0725m), ("2019-09-09", .07m), ("2019-10-28", .065m), ("2019-12-16", .0625m), ("2020-02-10", .06m), ("2020-07-27", .0425m),
            ("2021-04-26", .05m), ("2021-06-15", .055m), ("2021-07-26", .065m), ("2021-09-13", .0675m), ("2021-10-25", .075m), ("2021-12-20", .085m), ("2022-02-14", .095m),
            ("2022-02-28", .095m), ("2022-07-25", .08m), ("2022-09-19", .075m), ("2023-07-24", .085m), ("2023-08-15", .095m), ("2023-09-18", .095m), ("2025-01-01", .095m),
        }.Select(item => new RefinancingRate(DateOnly.ParseExact(item.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture), item.Rate)).ToArray());

    public static DateOnly DueDate(string period, int payDay)
    {
        var first = FirstDay(period);
        return first.AddMonths(1).AddDays(payDay - 1);
    }

    public static DateOnly FirstDay(string period) => DateOnly.ParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static decimal RateOn(DateOnly date, PenaltySettings settings) =>
        settings.Rates.Where(rate => rate.From <= date).OrderBy(rate => rate.From).LastOrDefault()?.Rate ?? 0m;

    public static decimal FractionFor(int overdueDays, PenaltySettings settings) =>
        settings.Tiers.Where(tier => tier.FromDay <= overdueDays).OrderBy(tier => tier.FromDay).LastOrDefault()?.Fraction ?? 0m;

    /// <summary>Пеня за расчётный месяц в копейках по долгам, разбитым по месяцам начисления.</summary>
    public static long Calculate(IEnumerable<OutstandingDebt> debts, string period, PenaltySettings settings)
    {
        var first = FirstDay(period);
        var last = first.AddMonths(1).AddDays(-1);
        var total = 0m;
        foreach (var debt in debts.Where(item => item.Amount > 0 && string.CompareOrdinal(item.Period, settings.StartPeriod) >= 0))
        {
            var due = DueDate(debt.Period, settings.PayDayNextMonth);
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                var overdue = day.DayNumber - due.DayNumber;
                if (overdue <= 0) continue;
                total += debt.Amount * FractionFor(overdue, settings) * RateOn(day, settings);
            }
        }
        return (long)Math.Round(total, MidpointRounding.AwayFromZero);
    }

    /// <summary>Распределяет долг по месяцам: оплата гасит старое, значит остаток лежит в самых новых начислениях.</summary>
    public static List<OutstandingDebt> Allocate(long debt, IEnumerable<OutstandingDebt> chargesNewestFirst)
    {
        var result = new List<OutstandingDebt>();
        var rest = debt;
        foreach (var charge in chargesNewestFirst)
        {
            if (rest <= 0) break;
            var part = Math.Min(rest, charge.Amount);
            if (part > 0) result.Add(new OutstandingDebt(charge.Period, part));
            rest -= part;
        }
        return result;
    }
}
