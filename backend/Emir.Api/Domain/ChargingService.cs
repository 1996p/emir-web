using Emir.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

public record PlannedCharge(Abonent Abonent, ServiceLine Line, Tariff? Tariff, decimal? Price, ChargeResult Result, bool AlreadyInDebt);
public record ChargeIssue(string Abonent, string Service, string Problem);
/// <summary>Previous — начислено в прошлом месяце; Already — уже проведено за открытый месяц (счётчики, прошлый запуск); Current — что даст расчёт сейчас.</summary>
public record ServicePreview(string Service, int Abonents, long Previous, long Already, long Current);
/// <summary>Already — уже проведено за открытый месяц; Total — что даст расчёт сейчас. Разница Total − Already добавится к долгу при проведении.</summary>
public record ChargePreview(int Abonents, int Lines, long Total, long Already, List<ServicePreview> Services, List<ChargeIssue> Issues);
public record ChargeRunResult(int Abonents, int Lines, long Total, List<ChargeIssue> Issues);
/// <summary>Одна причина, по которой услуги не будут начислены, со счётчиком строк и примерами абонентов.</summary>
public record ReadinessProblem(string Problem, int Lines, int Abonents, List<string> Examples);
/// <summary>Готовность открытого месяца к начислению: что посчитается и что мешает.</summary>
public record MonthReadiness(string Period, bool Reopened, int ReadyLines, int ReadyAbonents, long WouldCharge, int BlockedLines, int BlockedAbonents, List<ReadinessProblem> Problems, int HouseMetersTotal, int HouseMetersWithReading);

/// <summary>Расчёт и проведение начислений за открытый месяц.</summary>
public static class ChargingService
{
    private static decimal Area(this List<AbonentFact> facts, Abonent abonent, string period) => TariffHistory.FactOn(facts, abonent.Id, period)?.Area ?? abonent.Area;
    private static int Residents(this List<AbonentFact> facts, Abonent abonent, string period) => TariffHistory.FactOn(facts, abonent.Id, period)?.Residents ?? abonent.Residents;

    private static string PreviousPeriod(string period) => PenaltyEngine.FirstDay(period).AddMonths(-1).ToString("yyyy-MM");
    private static int MonthOf(string period) => PenaltyEngine.FirstDay(period).Month;

    public static async Task EnsureOpen(EmirDb db)
    {
        var month = await Periods.Work(db);
        if (month.ClosedAtUtc is not null && month.ReopenedAtUtc is null) throw new ApiError(409, "Месяц закрыт. Изменения требуют перезакрытия");
    }

    /// <summary>Считает начисления месяца по условиям и ценам, действовавшим именно в этом месяце.</summary>
    /// <param name="snapshots">Прошлые начисления месяца: их площадь, жильцы и норматив важнее сегодняшних.</param>
    public static async Task<List<PlannedCharge>> Plan(EmirDb db, List<Abonent> abonents, string period, IReadOnlyCollection<ChargeEntry>? snapshots = null)
    {
        var prices = await db.TariffPrices.ToListAsync();
        var terms = await db.ServiceTerms.ToListAsync();
        var profile = await db.OrganizationProfiles.FirstOrDefaultAsync();
        var heatingSeason = ChargeEngine.InHeatingSeason(MonthOf(period), profile?.HeatSeasonStartMonth ?? 10, profile?.HeatSeasonEndMonth ?? 4);
        var facts = await db.AbonentFacts.ToListAsync();
        var tariffs = await db.Tariffs.ToDictionaryAsync(tariff => tariff.Id);
        var readings = await db.PeriodReadings.Where(reading => reading.Period == period).ToListAsync();
        var houseMeters = await db.HouseMeters.ToDictionaryAsync(meter => meter.Id);
        var houseReadings = houseMeters.Count == 0 ? [] : await db.HouseMeterReadings.ToListAsync();
        var houseLines = houseMeters.Count == 0 ? [] : await db.ServiceLines.Where(item => item.HouseMeterId != null).Select(item => new { item.HouseMeterId, item.AbonentId, item.ServiceId }).ToListAsync();
        var houseData = new Dictionary<string, HouseMeterData?>();
        // Расход месяца в Гкал и площадь всех работающих услуг на счётчике. Без показания начислить нельзя.
        HouseMeterData? HouseDataOf(string meterId)
        {
            if (houseData.TryGetValue(meterId, out var known)) return known;
            HouseMeterData? data = null;
            var current = houseReadings.FirstOrDefault(item => item.HouseMeterId == meterId && item.Period == period);
            var previous = houseReadings.Where(item => item.HouseMeterId == meterId && string.CompareOrdinal(item.Period, period) < 0).OrderByDescending(item => item.Period).FirstOrDefault();
            if (current is not null && previous is not null && houseMeters.TryGetValue(meterId, out var meter))
            {
                var area = houseLines.Where(item => item.HouseMeterId == meterId && TariffHistory.TermOn(terms, item.AbonentId, item.ServiceId, period)?.State == TariffHistory.Working)
                    .Sum(item => TariffHistory.FactOn(facts, item.AbonentId, period)?.Area ?? 0);
                data = new HouseMeterData((current.Value - previous.Value) * meter.UnitFactor, 0, area, 0, 0, 0);
            }
            return houseData[meterId] = data;
        }
        var plan = new List<PlannedCharge>();

        foreach (var abonent in abonents)
        {
            var fact = TariffHistory.FactOn(facts, abonent.Id, period);
            foreach (var line in abonent.Services.Where(item => item.ServiceId != "penalty").OrderBy(item => item.Position))
            {
                // Приостановленные и расторгнутые услуги не начисляются
                var term = TariffHistory.TermOn(terms, line.AbonentId, line.ServiceId, period);
                if (term?.State != TariffHistory.Working) continue;
                if (term.TariffId is null || !tariffs.TryGetValue(term.TariffId, out var tariff))
                {
                    plan.Add(new PlannedCharge(abonent, line, null, null, ChargeResult.Fail("Не выбран тариф"), false));
                    continue;
                }
                if (TariffHistory.PriceOn(prices, tariff.Id, period)?.Price is not { } price)
                {
                    plan.Add(new PlannedCharge(abonent, line, tariff, null, ChargeResult.Fail($"Нет цены тарифа на {period}"), false));
                    continue;
                }

                decimal? usage = null;
                if (tariff.Scheme == "MeterApartment")
                {
                    var reading = readings.FirstOrDefault(item => item.AbonentId == line.AbonentId && item.ServiceId == line.ServiceId);
                    usage = reading is null ? null : Billing.UsageOf(line, reading.StartValue, reading.Value);
                }

                var snapshot = snapshots?.FirstOrDefault(item => item.AbonentId == line.AbonentId && item.ServiceId == line.ServiceId);
                var result = ChargeEngine.Calculate(new ChargeInput(
                    tariff.Scheme, price, MonthOf(period), fact?.Area ?? snapshot?.Area ?? abonent.Area, fact?.Residents ?? snapshot?.Residents ?? abonent.Residents,
                    HouseHeatNorm: abonent.HouseHeatNorm, HouseMeter: tariff.Scheme == "MeterHouse" && line.HouseMeterId is not null ? HouseDataOf(line.HouseMeterId) : null, ServiceNorm: tariff.ServiceNorm, TariffNorm: tariff.TariffNorm, ContractNorm: snapshot is null ? line.ContractNorm : snapshot.ContractNorm, MeterUsage: usage, HeatingSeason: heatingSeason, IsOrg: abonent.Kind == "org"));
                // Начисление по счётчику уже добавлено в долг при вводе показания
                plan.Add(new PlannedCharge(abonent, line, tariff, price, result, tariff.Scheme == "MeterApartment"));
            }
        }
        return plan;
    }

    private static List<ChargeIssue> IssuesOf(List<PlannedCharge> plan) =>
        plan.Where(item => item.Result.Error is not null).Select(item => new ChargeIssue(item.Abonent.ShortName, item.Line.Name, item.Result.Error!)).ToList();

    public static async Task<ChargePreview> Preview(EmirDb db)
    {
        var abonents = await db.Abonents.Include(abonent => abonent.Services).ToListAsync();
        var open = await Periods.WorkPeriod(db);
        var plan = await Plan(db, abonents, open);
        var previousPeriod = PreviousPeriod(open);
        var previous = await db.ChargeEntries.Where(entry => entry.Period == previousPeriod).ToListAsync();
        // Уже проведённые за открытый месяц начисления (счётчики ставятся в долг при вводе показания, либо расчёт уже запускали)
        var already = await db.ChargeEntries.Where(entry => entry.Period == open && entry.ServiceId != "penalty").ToListAsync();
        var valid = plan.Where(item => item.Result.Error is null).ToList();

        var services = valid.GroupBy(item => item.Line.Name).OrderBy(group => group.Key).Select(group =>
        {
            var ids = group.Select(item => item.Line.ServiceId).ToHashSet();
            return new ServicePreview(group.Key, group.Select(item => item.Abonent.Id).Distinct().Count(),
                previous.Where(entry => ids.Contains(entry.ServiceId)).Sum(entry => entry.Amount),
                already.Where(entry => ids.Contains(entry.ServiceId)).Sum(entry => entry.Amount),
                group.Sum(item => item.Result.Amount));
        }).ToList();

        return new ChargePreview(valid.Select(item => item.Abonent.Id).Distinct().Count(), valid.Count, valid.Sum(item => item.Result.Amount), already.Sum(entry => entry.Amount), services, IssuesOf(plan));
    }

    /// <summary>Что мешает закрыть открытый месяц: причины сгруппированы, с примерами и прогрессом по общедомовым счётчикам.</summary>
    public static async Task<MonthReadiness> Readiness(EmirDb db)
    {
        var abonents = await db.Abonents.Include(abonent => abonent.Services).ToListAsync();
        var work = await Periods.Work(db);
        var open = work.Period;
        var plan = await Plan(db, abonents, open);
        var valid = plan.Where(item => item.Result.Error is null).ToList();
        var blocked = plan.Where(item => item.Result.Error is not null).ToList();

        var problems = blocked.GroupBy(item => item.Result.Error!).OrderByDescending(group => group.Count()).Select(group => new ReadinessProblem(
            group.Key, group.Count(), group.Select(item => item.Abonent.Id).Distinct().Count(),
            group.Select(item => item.Abonent.ShortName).Distinct().Take(5).ToList())).ToList();

        var houseMetersTotal = await db.HouseMeters.CountAsync();
        var withReading = await db.HouseMeterReadings.Where(reading => reading.Period == open).Select(reading => reading.HouseMeterId).Distinct().CountAsync();

        return new MonthReadiness(open, work.ReopenedAtUtc is not null,
            valid.Count, valid.Select(item => item.Abonent.Id).Distinct().Count(), valid.Sum(item => item.Result.Amount),
            blocked.Count, blocked.Select(item => item.Abonent.Id).Distinct().Count(), problems, houseMetersTotal, withReading);
    }

    /// <summary>Повторный запуск сначала отменяет начисления месяца, поэтому сумма не удваивается.</summary>
    public static async Task<ChargeRunResult> Run(EmirDb db, string? onlyAbonentId = null)
    {
        await Periods.EnsureNotReopened(db);
        await EnsureOpen(db);
        var period = await Periods.WorkPeriod(db);
        var query = db.Abonents.Include(abonent => abonent.Services).AsQueryable();
        if (onlyAbonentId is not null) query = query.Where(abonent => abonent.Id == onlyAbonentId);
        var abonents = await query.ToListAsync();
        var plan = await Plan(db, abonents, period);
        var ids = abonents.Select(abonent => abonent.Id).ToHashSet();

        var facts = await db.AbonentFacts.ToListAsync();
        var readings = await db.PeriodReadings.Where(row => row.Period == period).ToListAsync();
        var old = await db.ChargeEntries.Where(entry => entry.Period == period && entry.ServiceId != "penalty" && ids.Contains(entry.AbonentId)).ToListAsync();
        foreach (var entry in old.Where(item => item.AppliedToDebt))
        {
            var abonent = abonents.First(item => item.Id == entry.AbonentId);
            var line = abonent.Services.FirstOrDefault(item => item.ServiceId == entry.ServiceId);
            if (line is not null) ReduceDebt(abonent, line, entry.Amount);
        }
        db.ChargeEntries.RemoveRange(old);

        foreach (var item in plan.Where(item => item.Result.Error is null))
        {
            db.ChargeEntries.Add(new ChargeEntry
            {
                AbonentId = item.Abonent.Id, ServiceId = item.Line.ServiceId, Period = period, Scheme = item.Tariff!.Scheme,
                Volume = item.Result.Volume, Amount = item.Result.Amount, TariffId = item.Tariff.Id, Price = item.Price, Area = facts.Area(item.Abonent, period), Residents = facts.Residents(item.Abonent, period), ContractNorm = item.Line.ContractNorm, AppliedToDebt = !item.AlreadyInDebt, AtUtc = DateTime.UtcNow,
            });
            if (!item.AlreadyInDebt)
            {
                item.Line.Debt += item.Result.Amount;
            }
            else if (readings.FirstOrDefault(row => row.AbonentId == item.Abonent.Id && row.ServiceId == item.Line.ServiceId) is { } reading)
            {
                // Обычно показание уже в долге и разницы нет. После отмены начисления сумма возвращается здесь
                var missing = item.Result.Amount - reading.AppliedAmount;
                if (missing >= 0) item.Line.Debt += missing;
                else ReduceDebt(item.Abonent, item.Line, -missing);
                reading.AppliedAmount = item.Result.Amount;
            }
            item.Line.ChargeStatus = $"За {Periods.Month(period)} выполнено";
        }
        foreach (var item in plan.Where(item => item.Result.Error is not null)) item.Line.ChargeStatus = item.Result.Error!;

        var valid = plan.Where(item => item.Result.Error is null).ToList();
        return new ChargeRunResult(valid.Select(item => item.Abonent.Id).Distinct().Count(), valid.Count, valid.Sum(item => item.Result.Amount), IssuesOf(plan));
    }

    /// <summary>Начисляет пеню по долгам услуг. Повторный запуск заменяет пеню этого месяца.</summary>
    public static async Task<ChargeRunResult> RunPenalty(EmirDb db)
    {
        await Periods.EnsureNotReopened(db);
        await EnsureOpen(db);
        var period = await Periods.WorkPeriod(db);
        var abonents = await db.Abonents.Include(abonent => abonent.Services).ToListAsync();
        var entries = await db.ChargeEntries.ToListAsync();
        var tariffs = await db.Tariffs.ToDictionaryAsync(tariff => tariff.Id);
        var count = 0;
        long total = 0;

        foreach (var abonent in abonents)
        {
            var penalty = abonent.Services.FirstOrDefault(line => line.ServiceId == "penalty");
            var oldEntry = entries.FirstOrDefault(entry => entry.AbonentId == abonent.Id && entry.ServiceId == "penalty" && entry.Period == period);
            if (oldEntry is not null)
            {
                if (penalty is not null) ReduceDebt(abonent, penalty, oldEntry.Amount);
                db.ChargeEntries.Remove(oldEntry);
            }

            var debts = new List<OutstandingDebt>();
            foreach (var line in abonent.Services.Where(item => item.ServiceId != "penalty" && item.Debt > 0))
            {
                if (line.TariffId is not null && tariffs.TryGetValue(line.TariffId, out var tariff) && !tariff.PenaltyEnabled) continue;
                var charges = entries.Where(entry => entry.AbonentId == abonent.Id && entry.ServiceId == line.ServiceId)
                    .OrderByDescending(entry => entry.Period).Select(entry => new OutstandingDebt(entry.Period, entry.Amount));
                debts.AddRange(PenaltyEngine.Allocate(line.Debt, charges));
            }

            var amount = PenaltyEngine.Calculate(debts, period, PenaltyEngine.Current);
            if (amount <= 0) continue;
            if (penalty is null)
            {
                penalty = new ServiceLine { AbonentId = abonent.Id, ServiceId = "penalty", Position = abonent.Services.Count, Name = "Пени", TariffName = "По настройке пени", ChargeStatus = $"За {Periods.Month(period)} выполнено" };
                db.ServiceLines.Add(penalty);
            }
            penalty.Debt += amount;
            penalty.ChargeStatus = $"За {Periods.Month(period)} выполнено";
            db.ChargeEntries.Add(new ChargeEntry { AbonentId = abonent.Id, ServiceId = "penalty", Period = period, Scheme = "Penalty", Volume = 0, Amount = amount, AppliedToDebt = true, AtUtc = DateTime.UtcNow });
            count++;
            total += amount;
        }
        return new ChargeRunResult(count, count, total, []);
    }

    public static void ReduceDebt(Abonent abonent, ServiceLine line, long amount)
    {
        line.Debt -= amount;
        if (line.Debt >= 0) return;
        // Часть начисления уже оплачена: она становится переплатой
        abonent.Credit -= line.Debt;
        line.Debt = 0;
    }
}
