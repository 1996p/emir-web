using System.Globalization;
using Emir.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

public record CheckRow(string? AbonentId, List<string> Cells);

/// <summary>Severity: error нужно исправить, warn нужно посмотреть, info к сведению.</summary>
public record CheckGroup(string Id, string Title, string Description, string Severity, List<string> Columns, List<CheckRow> Rows, string? Action = null, string? ActionTo = null);

public record ChecksResult(string Period, string Title, List<CheckGroup> Groups)
{
    public int Errors => Groups.Where(group => group.Severity == "error").Sum(group => group.Rows.Count);
    public int Warnings => Groups.Where(group => group.Severity == "warn").Sum(group => group.Rows.Count);
}

/// <summary>Сверки только читают данные и ничего не меняют.</summary>
public static class ChecksService
{
    /// <summary>Изменение суммы за месяц, после которого начисление стоит посмотреть. Решение нового ЕМИР, в старом порог неизвестен.</summary>
    public const decimal SharpChange = 0.5m;

    private static string Money(long kopecks) => (kopecks / 100m).ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
    private static string Address(Abonent abonent) => $"{abonent.Street}{(abonent.Flat is null ? "" : $"-{abonent.Flat}")}";

    public static async Task<ChecksResult> Run(EmirDb db, string period)
    {
        var abonents = await db.Abonents.Include(abonent => abonent.Services).OrderBy(abonent => abonent.Account).ToListAsync();
        var plan = await ChargingService.Plan(db, abonents, period);
        var month = await db.AccountingMonths.FirstAsync(item => item.Period == period);
        var chargesDone = month.ClosedAtUtc is not null || await db.PeriodSteps.AnyAsync(step => step.Id == "charges" && step.State == "done");

        return new ChecksResult(period, Periods.Title(period),
        [
            await ChargeDifferences(db, period, chargesDone),
            await LedgerMismatches(db, abonents),
            NotCharged(plan),
            await SharpChanges(db, abonents, period),
            await SuspendedReadings(db, abonents, period),
            await TariffsByPlace(db, abonents, period),
            Duplicates(abonents),
            await ResidentsMismatch(db, abonents),
            CreditAndDebt(abonents),
            await UnresolvedLoads(db),
            await StaleDocuments(db, abonents, period),
            await MissingDocuments(db, abonents, period, month.ClosedAtUtc is not null),
            await Revisions(db, abonents, period),
        ]);
    }

    private static async Task<CheckGroup> ChargeDifferences(EmirDb db, string period, bool chargesDone)
    {
        var changes = await ReclosingService.Differences(db, period, includeMissing: chargesDone);
        return new CheckGroup("differences", "Начисления не совпадают с расчётом",
            "Сумма начисления или долга отличается от того, что даёт расчёт по ценам, тарифам и показаниям этого месяца. В исправном месяце список пуст.",
            "error", ["Абонент", "Услуга", "Начислено, ₽", "По расчёту, ₽", "Разница, ₽"],
            changes.Select(change => new CheckRow(change.AbonentId, [change.Abonent, change.Service, Money(change.OldAmount), Money(change.NewAmount), Money(change.Delta)])).ToList());
    }

    private static async Task<CheckGroup> LedgerMismatches(EmirDb db, List<Abonent> abonents)
    {
        var debts = await db.LedgerEntries.GroupBy(entry => new { entry.AbonentId, entry.ServiceId }).Select(group => new { group.Key.AbonentId, group.Key.ServiceId, Debt = group.Sum(entry => entry.DebtDelta), Credit = group.Sum(entry => entry.CreditDelta) }).ToListAsync();
        var rows = new List<CheckRow>();
        foreach (var abonent in abonents)
        {
            foreach (var line in abonent.Services)
            {
                var ledger = debts.Where(item => item.AbonentId == abonent.Id && item.ServiceId == line.ServiceId).Sum(item => item.Debt);
                if (ledger != line.Debt) rows.Add(new CheckRow(abonent.Id, [abonent.ShortName, line.Name, Money(ledger), Money(line.Debt), Money(line.Debt - ledger)]));
            }
            var credit = debts.Where(item => item.AbonentId == abonent.Id).Sum(item => item.Credit);
            if (credit != abonent.Credit) rows.Add(new CheckRow(abonent.Id, [abonent.ShortName, "Переплата", Money(credit), Money(abonent.Credit), Money(abonent.Credit - credit)]));
        }
        return new CheckGroup("ledger", "Остаток не сходится с журналом денег",
            "Долг или переплата отличаются от суммы всех движений. Значит, данные меняли в обход системы. В исправной базе список пуст. Сверка не зависит от выбранного месяца.",
            "error", ["Абонент", "Строка", "По журналу, ₽", "В остатке, ₽", "Разница, ₽"], rows);
    }

    private static CheckGroup NotCharged(List<PlannedCharge> plan) => new("not-charged", "Услуга работает, но начислить нельзя",
        "Нет тарифа, цены или показания. По этим услугам месяц останется без начисления.",
        "error", ["Абонент", "Услуга", "Причина"],
        plan.Where(item => item.Result.Error is not null).Select(item => new CheckRow(item.Abonent.Id, [item.Abonent.ShortName, item.Line.Name, item.Result.Error!])).ToList());

    private static async Task<CheckGroup> SharpChanges(EmirDb db, List<Abonent> abonents, string period)
    {
        var previousPeriod = PenaltyEngine.FirstDay(period).AddMonths(-1).ToString("yyyy-MM");
        var entries = await db.ChargeEntries.Where(entry => (entry.Period == period || entry.Period == previousPeriod) && entry.ServiceId != "penalty" && entry.Scheme != "Opening").ToListAsync();
        var rows = new List<CheckRow>();
        foreach (var entry in entries.Where(item => item.Period == period))
        {
            var previous = entries.FirstOrDefault(item => item.Period == previousPeriod && item.AbonentId == entry.AbonentId && item.ServiceId == entry.ServiceId);
            if (previous is not { Amount: > 0 }) continue;
            var change = (entry.Amount - previous.Amount) / (decimal)previous.Amount;
            if (Math.Abs(change) <= SharpChange) continue;
            var abonent = abonents.First(item => item.Id == entry.AbonentId);
            rows.Add(new CheckRow(abonent.Id, [abonent.ShortName, abonent.Services.FirstOrDefault(line => line.ServiceId == entry.ServiceId)?.Name ?? entry.ServiceId, Money(previous.Amount), Money(entry.Amount), $"{change * 100:+0;-0} %"]));
        }
        return new CheckGroup("sharp-change", "Резкое изменение суммы",
            $"Начисление изменилось больше чем на {SharpChange * 100:0} % к прошлому месяцу. Это не обязательно ошибка: могла измениться цена или сезон.",
            "warn", ["Абонент", "Услуга", "Прошлый месяц, ₽", "Этот месяц, ₽", "Изменение"], rows);
    }

    private static async Task<CheckGroup> SuspendedReadings(EmirDb db, List<Abonent> abonents, string period)
    {
        var readings = await db.PeriodReadings.Where(reading => reading.Period == period).ToListAsync();
        var terms = await db.ServiceTerms.ToListAsync();
        var rows = new List<CheckRow>();
        foreach (var reading in readings)
        {
            var state = TariffHistory.TermOn(terms, reading.AbonentId, reading.ServiceId, period)?.State;
            if (state == TariffHistory.Working) continue;
            var abonent = abonents.First(item => item.Id == reading.AbonentId);
            rows.Add(new CheckRow(abonent.Id, [abonent.ShortName, abonent.Services.FirstOrDefault(line => line.ServiceId == reading.ServiceId)?.Name ?? reading.ServiceId, state ?? "Нет условий", reading.Value.ToString("0.####", CultureInfo.GetCultureInfo("ru-RU"))]));
        }
        return new CheckGroup("suspended-readings", "Показания по неработающим услугам",
            "Показание сохранено, но начисления по нему нет: услуга приостановлена или расторгнута.",
            "warn", ["Абонент", "Услуга", "Состояние", "Показание"], rows);
    }

    private static async Task<CheckGroup> TariffsByPlace(EmirDb db, List<Abonent> abonents, string period)
    {
        var terms = await db.ServiceTerms.ToListAsync();
        var tariffs = await db.Tariffs.ToDictionaryAsync(tariff => tariff.Id);
        var used = abonents.Where(abonent => abonent.Place != "").SelectMany(abonent => abonent.Services.Where(line => line.ServiceId != "penalty").Select(line =>
            (Abonent: abonent, Line: line, Term: TariffHistory.TermOn(terms, abonent.Id, line.ServiceId, period))))
            .Where(item => item.Term is { State: TariffHistory.Working, TariffId: not null }).ToList();

        var rows = new List<CheckRow>();
        foreach (var group in used.GroupBy(item => (item.Abonent.Place, item.Line.Name)))
        {
            var byTariff = group.GroupBy(item => item.Term!.TariffId!).OrderByDescending(item => item.Count()).ToList();
            if (byTariff.Count < 2) continue;
            var usual = tariffs[byTariff[0].Key].Name;
            foreach (var item in byTariff.Skip(1).SelectMany(other => other))
                rows.Add(new CheckRow(item.Abonent.Id, [group.Key.Place, item.Abonent.ShortName, group.Key.Name, tariffs[item.Term!.TariffId!].Name, usual]));
        }
        return new CheckGroup("tariff-place", "Другой тариф в населённом пункте",
            "У абонента не тот тариф, что у большинства в его населённом пункте. Таблицы «тариф и сельсовет» пока нет, поэтому сравнение идёт с соседями.",
            "warn", ["Населённый пункт", "Абонент", "Услуга", "Тариф абонента", "У большинства"], rows);
    }

    private static CheckGroup Duplicates(List<Abonent> abonents)
    {
        var rows = new List<CheckRow>();
        var people = abonents.Where(abonent => abonent.Kind == "person").ToList();
        foreach (var group in people.GroupBy(abonent => abonent.Name.Trim().ToLowerInvariant()).Where(group => group.Count() > 1))
            foreach (var abonent in group)
                rows.Add(new CheckRow(abonent.Id, ["Одинаковое ФИО", abonent.Name, abonent.Account, Address(abonent)]));
        foreach (var group in people.Where(abonent => abonent.Street != "").GroupBy(abonent => (abonent.Place, abonent.Street, abonent.Flat)).Where(group => group.Count() > 1))
            foreach (var abonent in group)
                rows.Add(new CheckRow(abonent.Id, ["Одинаковый адрес", abonent.Name, abonent.Account, Address(abonent)]));
        return new CheckGroup("duplicates", "Возможные дубли лицевых счетов",
            "Физлица с одинаковым ФИО или адресом. Это могут быть разные люди или совместные собственники, решение за вами.",
            "warn", ["Совпадение", "Абонент", "ЛС", "Адрес"], rows);
    }

    private static async Task<CheckGroup> ResidentsMismatch(EmirDb db, List<Abonent> abonents)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var residents = await db.Residents.ToListAsync();
        var rows = abonents.Where(abonent => residents.Any(item => item.AbonentId == abonent.Id))
            .Select(abonent => (Abonent: abonent, Listed: residents.Count(item => item.AbonentId == abonent.Id && (item.ToDate is null || item.ToDate > today))))
            .Where(item => item.Listed != item.Abonent.Residents)
            .Select(item => new CheckRow(item.Abonent.Id, [item.Abonent.ShortName, item.Abonent.Account, item.Listed.ToString(), item.Abonent.Residents.ToString()])).ToList();
        return new CheckGroup("residents", "Жильцы: список не совпадает с расчётом",
            "Начисление идёт по числу жильцов из параметров, а в списке другое количество. Абоненты без списка здесь не показаны. Сверка не зависит от выбранного месяца.",
            "warn", ["Абонент", "ЛС", "В списке", "В расчёте"], rows);
    }

    private static CheckGroup CreditAndDebt(List<Abonent> abonents) => new("credit-debt", "Переплата и долг одновременно",
        "Переплату можно зачесть в долг корректировкой. Сама система её не зачитывает.",
        "info", ["Абонент", "ЛС", "Переплата, ₽", "Долг, ₽"],
        abonents.Where(abonent => abonent.Credit > 0 && abonent.Services.Sum(line => line.Debt) > 0)
            .Select(abonent => new CheckRow(abonent.Id, [abonent.ShortName, abonent.Account, Money(abonent.Credit), Money(abonent.Services.Sum(line => line.Debt))])).ToList());

    private static async Task<CheckGroup> UnresolvedLoads(EmirDb db)
    {
        var batches = await db.ExchangeBatches.Include(batch => batch.Lines).Where(batch => !batch.Posted && batch.IsPayments).OrderBy(batch => batch.AtUtc).ToListAsync();
        var states = new Dictionary<string, string> { ["not-found"] = "Счёт не найден", ["duplicate"] = "Дубль" };
        return new CheckGroup("unresolved-loads", "Непроведённые строки реестров",
            "Строки загруженных реестров платежей без решения. Деньги по ним не зачислены. Сверка не зависит от выбранного месяца.",
            "warn", ["Реестр", "Строка", "Лицевой счёт", "Сумма, ₽", "Проблема"],
            batches.SelectMany(batch => batch.Lines.Where(line => line.State != "ok").OrderBy(line => line.Line)
                .Select(line => new CheckRow(null, [batch.FileName, line.Line.ToString(), line.Key, Money(line.Amount), states.GetValueOrDefault(line.State, line.State)]))).ToList(),
            "В обмен", "/exchange");
    }

    private static async Task<CheckGroup> StaleDocuments(EmirDb db, List<Abonent> abonents, string period)
    {
        var documents = await db.BillingDocuments.Where(document => document.Period == period && document.Status == "active" && document.Stale).OrderBy(document => document.Id).ToListAsync();
        return new CheckGroup("stale-docs", "Документы нужно перевыпустить",
            "После выпуска месяц перезакрыли, и суммы в этих документах больше не верны.",
            "error", ["Абонент", "Документ", "Номер", "Сумма в документе, ₽"],
            documents.Select(document => new CheckRow(document.AbonentId, [abonents.FirstOrDefault(item => item.Id == document.AbonentId)?.ShortName ?? document.AbonentId, DocumentService.KindName(document.Kind), document.Number, Money(document.Total)])).ToList(),
            "К документам", "/documents");
    }

    private static async Task<CheckGroup> MissingDocuments(EmirDb db, List<Abonent> abonents, string period, bool closed)
    {
        var rows = new List<CheckRow>();
        if (closed)
        {
            var charged = await db.ChargeEntries.Where(entry => entry.Period == period && entry.Scheme != "Opening" && entry.ServiceId != "penalty" && entry.Amount > 0).Select(entry => entry.AbonentId).Distinct().ToListAsync();
            var issued = await db.BillingDocuments.Where(document => document.Period == period && document.Status == "active").Select(document => document.AbonentId).Distinct().ToListAsync();
            rows = abonents.Where(abonent => charged.Contains(abonent.Id) && !issued.Contains(abonent.Id)).Select(abonent => new CheckRow(abonent.Id, [abonent.ShortName, abonent.Account, abonent.Kind == "org" ? "Юрлицо" : "Физлицо"])).ToList();
        }
        return new CheckGroup("no-docs", "Закрытый месяц без документов",
            "Начисление есть, а документов по нему нет. Для незакрытого месяца список пуст.",
            "warn", ["Абонент", "ЛС", "Тип"], rows, "К документам", "/documents");
    }

    private static async Task<CheckGroup> Revisions(EmirDb db, List<Abonent> abonents, string period)
    {
        var revisions = await db.ChargeRevisions.Where(revision => revision.Period == period).OrderByDescending(revision => revision.AtUtc).ToListAsync();
        return new CheckGroup("revisions", "Отмены и перезакрытия",
            "Все изменения уже рассчитанных начислений этого месяца: кто, когда и почему.",
            "info", ["Когда", "Абонент", "Услуга", "Было, ₽", "Стало, ₽", "Кто", "Основание"],
            revisions.Select(revision =>
            {
                var abonent = abonents.FirstOrDefault(item => item.Id == revision.AbonentId);
                return new CheckRow(revision.AbonentId, [revision.AtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), abonent?.ShortName ?? revision.AbonentId, abonent?.Services.FirstOrDefault(line => line.ServiceId == revision.ServiceId)?.Name ?? revision.ServiceId, Money(revision.OldAmount), Money(revision.NewAmount), revision.Who, revision.Reason]);
            }).ToList());
    }
}
