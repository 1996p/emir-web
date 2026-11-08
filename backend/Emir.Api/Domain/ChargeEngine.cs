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
    HouseMeterData? HouseMeter = null,
    // Категория поставщика в старом ЕМИР: юрлица (ЮЛ, Category=1) округляют объём до 3 знаков, физлица (ФЛ, Category=0) до 4
    bool IsOrg = false);

/// <summary>Volume — объём Q, Amount — сумма M в копейках, Error — почему посчитать нельзя.</summary>
public record ChargeResult(decimal Volume, long Amount, string? Error = null)
{
    public static ChargeResult Fail(string error) => new(0, 0, error);
}

/// <summary>Схемы начисления ЕМИР ЖКХ. Сумма = объём × цена, округление до копеек.</summary>
public static class ChargeEngine
{
    public static readonly string[] Schemes =
    [
        "Norm", "AreaKrsk", "HeatHouseNorm", "HeatHouseNormKrsk", "HeatLost", "ContractNorm", "ServiceNorm", "MonthlyQuantity", "MonthlyQuantityHouse", "NormHouse", "NormTarifContract",
        "MonthlyAmount", "AreaKrskNorm", "NormTarif", "AreaNormTarif", "AreaNormTarifSeasonal", "AreaNormTarifMonthly", "MeterApartment", "MeterHouse", "HouseMeterLeftover", "Area",
    ];

    /// <summary>Формулы сверены с кодом старой системы (классы GKH.Contract.Scheme.* и GKH.Krsk.Contract.Scheme.*).</summary>
    public static ChargeResult Calculate(ChargeInput input)
    {
        decimal? serviceNorm = NormFor(input.ServiceNorm, input.Month);
        decimal? tariffNorm = NormFor(input.TariffNorm, input.Month);
        decimal? contractNorm = NormFor(input.ContractNorm, input.Month);
        // Норматив по заказу помесячно, а этого месяца в нём нет: старая система начисляет 0 без ошибки (например, летом)
        bool contractMonthMissing = contractNorm is null && IsMonthly(input.ContractNorm);

        ChargeResult Need(string what) => ChargeResult.Fail($"Нет данных: {what}");
        ChargeResult Result(decimal volume) => new(volume, Money(volume, input.Price));
        // Старый ЕМИР (AbstractScheme.Round): объём округляется ДО умножения на цену — 3 знака для юрлиц, 4 для физлиц
        int scale = input.IsOrg ? 3 : 4;
        decimal Round(decimal volume) => Math.Round(volume, scale, MidpointRounding.AwayFromZero);
        ChargeResult RResult(decimal volume) => Result(Round(volume));
        ChargeResult Zero() => new(0, 0);
        bool noArea = input.Area <= 0;
        const string AreaMissing = "площадь квартиры пустая или нулевая";
        // «Тепло по норме дома»: деньги = площадь × цена, а объём показывается в Гкал = норма дома × площадь
        ChargeResult ByArea(decimal quantity) => new(input.HouseHeatNorm is { } norm ? norm * quantity : quantity, Money(quantity, input.Price));

        switch (input.Scheme)
        {
            case "Norm": return serviceNorm is { } n1 ? RResult(n1 * input.Residents) : Need("норма по услуге");
            case "NormTarif": return tariffNorm is { } n2 ? RResult(n2 * input.Residents) : Need("норматив по тарифу");
            case "ServiceNorm": return serviceNorm is { } n3 ? RResult(n3) : Need("норма по услуге");
            // Норматив в заказе, если он больше нуля, иначе жилая площадь (как схема «Area»)
            case "AreaKrsk":
                if (contractNorm is > 0) return Result(contractNorm.Value);
                return noArea ? Need(AreaMissing) : Result(input.Area);
            case "ContractNorm":
                if (contractMonthMissing) return Zero();
                return contractNorm is { } n4 ? Result(n4) : Need("норматив по заказу");
            // Норматив заказа здесь — СУММА в рублях на месяц, а не объём. Объём = сумма / цена
            case "MonthlyAmount":
                if (contractNorm is not > 0) return Zero();
                var rubles = Round(contractNorm.Value);
                return new(input.Price > 0 ? Round(rubles / input.Price) : 0, (long)Math.Round(rubles * 100m, MidpointRounding.AwayFromZero));
            case "MonthlyQuantity":
                return contractNorm is > 0 ? RResult(contractNorm.Value) : Zero();
            case "AreaKrskNorm":
                return serviceNorm is { } n6 && contractNorm is > 0 ? RResult(n6 * contractNorm.Value) : Need("норма по услуге и норматив по заказу");
            case "NormTarifContract":
                return tariffNorm is { } n7 && contractNorm is > 0 ? RResult(contractNorm.Value * n7) : Need("норматив по тарифу и норматив по заказу");
            case "Area": return noArea ? Need(AreaMissing) : Result(input.Area);
            case "HeatHouseNorm": return ByArea(input.Area);
            // Норматив в заказе, если он больше нуля, иначе как «тепло по норме дома»
            case "HeatHouseNormKrsk": return contractNorm is > 0 ? ByArea(contractNorm.Value) : ByArea(input.Area);
            case "HeatLost":
                return contractNorm is > 0 && input.YearSharePercent is { } share ? RResult(contractNorm.Value * share / 100m) : Need("годовые потери тепла по заказу и процент месяца");
            case "NormHouse":
                if (noArea) return Need(AreaMissing);
                return input.HouseHeatNorm is > 0 ? RResult(input.Area * input.HouseHeatNorm.Value) : Need("норма теплопотребления дома");
            case "AreaNormTarif":
            case "AreaNormTarifMonthly":
                if (noArea) return Need(AreaMissing);
                return tariffNorm is { } n10 ? RResult(input.Area * n10) : Need("норматив по тарифу");
            case "AreaNormTarifSeasonal":
                if (!input.HeatingSeason) return Zero();
                if (noArea) return Need(AreaMissing);
                return tariffNorm is { } n11 ? RResult(input.Area * n11) : Need("норматив по тарифу");
            case "MonthlyQuantityHouse":
                if (input.HouseLoss is not { } loss || input.HouseArea is not { } houseArea || houseArea <= 0) return Need("потери тепла и площадь дома");
                return RResult(loss * input.Area / houseArea);
            case "MeterApartment":
                return input.MeterUsage is { } usage ? RResult(usage) : ChargeResult.Fail("Нет показаний за месяц");
            case "MeterHouse":
                if (input.HouseMeter is not { } m1) return Need("общедомовой счётчик");
                var restArea = m1.TotalArea - m1.IndividualArea;
                return restArea <= 0 ? Need("площадь дома без индивидуальных счётчиков") : RResult((m1.Usage - m1.IndividualUsage) * input.Area / restArea);
            case "HouseMeterLeftover":
                if (input.HouseMeter is not { } m2) return Need("общедомовой счётчик");
                if (m2.IndividualUsage > m2.Usage) return ChargeResult.Fail("Потребление по общедомовому счётчику меньше, чем сумма по квартирным");
                var restPeople = m2.TotalResidents - m2.IndividualResidents;
                return restPeople <= 0 ? Need("проживающие без индивидуальных счётчиков") : RResult((m2.Usage - m2.IndividualUsage) * input.Residents / restPeople);
            default:
                return ChargeResult.Fail($"Неизвестная схема {input.Scheme}");
        }
    }

    private static bool IsMonthly(string? norm) => norm?.Contains('=') == true;

    public static long Money(decimal volume, decimal priceRubles) =>
        (long)Math.Round(volume * priceRubles * 100m, MidpointRounding.AwayFromZero);

    /// <summary>Месяц входит в отопительный сезон [start..end] включительно. Сезон может переходить через год (10..4 = окт–апр).</summary>
    public static bool InHeatingSeason(int month, int start, int end) =>
        start <= end ? month >= start && month <= end : month >= start || month <= end;

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

    /// <summary>Пеня за расчётный месяц в копейках по долгам, разбитым по месяцам начисления.
    /// Как в старой системе (Penaltie.Control.AbonPay): пеня начисляется за каждый день отдельно и сразу округляется до копейки,
    /// дневная сумма меньше копейки не начисляется. Долги одного месяца начисления складываются до расчёта.</summary>
    public static long Calculate(IEnumerable<OutstandingDebt> debts, string period, PenaltySettings settings)
    {
        var first = FirstDay(period);
        var last = first.AddMonths(1).AddDays(-1);
        long total = 0;
        var byMonth = debts.Where(item => item.Amount > 0 && string.CompareOrdinal(item.Period, settings.StartPeriod) >= 0)
            .GroupBy(item => item.Period).Select(group => new OutstandingDebt(group.Key, group.Sum(item => item.Amount)));
        foreach (var debt in byMonth)
        {
            var due = DueDate(debt.Period, settings.PayDayNextMonth);
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                var overdue = day.DayNumber - due.DayNumber;
                if (overdue <= 0) continue;
                total += DailyKopecks(debt.Amount, FractionFor(overdue, settings), RateOn(day, settings));
            }
        }
        return total;
    }

    /// <summary>Пеня за один день: долг × доля ставки × ставка, до копейки. Меньше копейки — 0.</summary>
    public static long DailyKopecks(long debt, decimal fraction, decimal rate) =>
        (long)Math.Round(debt * fraction * rate, MidpointRounding.AwayFromZero);

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
