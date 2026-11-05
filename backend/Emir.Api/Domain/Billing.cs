using Emir.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

/// <summary>Ошибка, понятная пользователю. Текст возвращается клиенту.</summary>
public class ApiError(int status, string detail) : Exception(detail)
{
    public int Status { get; } = status;
}

public record Allocation(string ServiceId, long Amount);

/// <summary>Денежные правила. Все суммы в копейках.</summary>
public static class Billing
{
    /// <summary>Цена по счётчику на открытый месяц. Нет цены, если услуга не работает или тариф не по счётчику.</summary>
    private static decimal? MeterPrice(EmirDb db, ServiceLine line, string period)
    {
        var term = db.ServiceTerms.Where(item => item.AbonentId == line.AbonentId && item.ServiceId == line.ServiceId && string.Compare(item.FromPeriod, period) <= 0)
            .OrderByDescending(item => item.FromPeriod).FirstOrDefault();
        if (term is not { State: TariffHistory.Working, TariffId: not null }) return null;
        if (!db.Tariffs.Any(tariff => tariff.Id == term.TariffId && tariff.Scheme == "MeterApartment")) return null;
        return db.TariffPrices.Where(price => price.TariffId == term.TariffId && string.Compare(price.FromPeriod, period) <= 0)
            .OrderByDescending(price => price.FromPeriod).Select(price => (decimal?)price.Price).FirstOrDefault();
    }

    public static long ChargeFor(decimal? price, decimal fromValue, decimal toValue) =>
        price is { } value ? (long)Math.Round((toValue - fromValue) * value * 100m, MidpointRounding.AwayFromZero) : 0;

    /// <summary>
    /// Учитывает показание за открытый период. Повторный ввод пересчитывает только разницу,
    /// поэтому показание нельзя начислить дважды.
    /// </summary>
    public static void ApplyReading(EmirDb db, Abonent abonent, ServiceLine line, decimal value)
    {
        if (line.MeterLastReading is not { } last) throw new ApiError(422, $"У услуги «{line.Name}» нет счётчика");
        if (value < 0) throw new ApiError(422, "Показание не может быть отрицательным");
        var period = Periods.WorkPeriodNow(db);
        var month = db.AccountingMonths.AsNoTracking().First(item => item.Period == period);
        if (month.ClosedAtUtc is not null && month.ReopenedAtUtc is null) throw new ApiError(409, "Месяц закрыт. Изменения требуют перезакрытия");

        var reading = db.PeriodReadings.Local.FirstOrDefault(item => item.AbonentId == line.AbonentId && item.ServiceId == line.ServiceId && item.Period == period)
            ?? db.PeriodReadings.FirstOrDefault(item => item.AbonentId == line.AbonentId && item.ServiceId == line.ServiceId && item.Period == period);
        var start = reading?.StartValue ?? last;
        if (value < start) throw new ApiError(422, $"Показание {value:0.####} меньше предыдущего {start:0.####}. Для исправления используйте корректировку");

        // Всегда от начала месяца: повторный ввод добавит в долг только разницу, а округление не накопится
        var charge = ChargeFor(MeterPrice(db, line, period), start, value);
        if (reading is null)
        {
            reading = new PeriodReading { AbonentId = line.AbonentId, ServiceId = line.ServiceId, Period = period, StartValue = start, Value = value };
            db.PeriodReadings.Add(reading);
        }
        else
        {
            reading.Value = value;
        }

        line.Debt += charge - reading.AppliedAmount;
        reading.AppliedAmount = charge;
        if (line.Debt < 0)
        {
            abonent.Credit -= line.Debt;
            line.Debt = 0;
        }
        line.MeterLastReading = value;
        line.ChargeStatus = $"За {Periods.Month(period)} выполнено";
    }

    /// <summary>Гасит долг по услугам. Излишек уходит в переплату.</summary>
    public static long ApplyAllocations(Abonent abonent, IReadOnlyCollection<Allocation> allocations)
    {
        if (allocations.Count == 0) throw new ApiError(422, "Выберите услугу для оплаты");
        if (allocations.Select(item => item.ServiceId).Distinct().Count() != allocations.Count) throw new ApiError(422, "Услуга указана дважды");

        long total = 0;
        foreach (var allocation in allocations)
        {
            if (allocation.Amount <= 0) throw new ApiError(422, "Сумма должна быть больше нуля");
            var line = abonent.Services.FirstOrDefault(item => item.ServiceId == allocation.ServiceId)
                ?? throw new ApiError(422, $"У абонента нет услуги {allocation.ServiceId}");
            var applied = Math.Min(line.Debt, allocation.Amount);
            line.Debt -= applied;
            abonent.Credit += allocation.Amount - applied;
            total += allocation.Amount;
        }
        return total;
    }

    /// <summary>Снимает деньги: сначала с переплаты, остаток становится долгом по первой услуге.</summary>
    public static void TakeFromAbonent(Abonent abonent, long amount)
    {
        var fromCredit = Math.Min(abonent.Credit, amount);
        abonent.Credit -= fromCredit;
        var rest = amount - fromCredit;
        if (rest == 0) return;
        var first = abonent.Services.OrderBy(line => line.Position).FirstOrDefault()
            ?? throw new ApiError(422, "У абонента нет услуг");
        first.Debt += rest;
    }

    /// <summary>Возвращает деньги: гасит долги по порядку услуг, остаток уходит в переплату.</summary>
    public static void GiveToAbonent(Abonent abonent, long amount)
    {
        var rest = amount;
        foreach (var line in abonent.Services.OrderBy(item => item.Position))
        {
            var applied = Math.Min(line.Debt, rest);
            line.Debt -= applied;
            rest -= applied;
        }
        abonent.Credit += rest;
    }
}
