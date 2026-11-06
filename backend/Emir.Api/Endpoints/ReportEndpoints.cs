using System.Globalization;
using System.Text;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record ReportColumnDto(string Title, string Type, bool NoTotal = false);
public record ReportRowDto(string Name, List<long> Values);
public record ReportResultDto(bool Available, string? Message, List<ReportColumnDto> Columns, List<ReportRowDto> Rows, List<long?> Totals);

public static class ReportEndpoints
{
    private const string NotConnected = "Этот отчёт ещё не сделан. Данные для него перенесены, отчёт будет добавлен.";

    public static void MapReports(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/reports").RequireAuthorization();

        group.MapGet("/catalog", () => Catalog.Reports);

        group.MapGet("/run", async (string reportId, string period, string cut, string who, DateOnly? from, DateOnly? to, EmirDb db) =>
            await Build(db, reportId, period, cut, who, from, to));

        group.MapGet("/export.csv", async (string reportId, string period, string cut, string who, DateOnly? from, DateOnly? to, EmirDb db) =>
        {
            var result = await Build(db, reportId, period, cut, who, from, to);
            if (!result.Available) throw new ApiError(422, result.Message ?? NotConnected);
            var text = new StringBuilder();
            text.AppendLine(string.Join(';', new[] { cut }.Concat(result.Columns.Select(column => column.Title)).Select(Cell)));
            foreach (var row in result.Rows)
                text.AppendLine(string.Join(';', new[] { Cell(row.Name) }.Concat(row.Values.Select((value, index) => Number(value, result.Columns[index])))));
            var bom = Encoding.UTF8.GetPreamble();
            return Results.File(bom.Concat(Encoding.UTF8.GetBytes(text.ToString())).ToArray(), "text/csv; charset=utf-8", $"{reportId}.csv");
        });
    }

    /// <summary>Значение не должно выполниться как формула, когда файл откроют в Excel.</summary>
    public static string Cell(string value)
    {
        var safe = value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }

    private static string Number(long value, ReportColumnDto column) => column.Type switch
    {
        "money" => (value / 100m).ToString("0.00", CultureInfo.GetCultureInfo("ru-RU")),
        "percent" => (value / 10m).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")),
        "volume" => (value / 1000m).ToString("0.000", CultureInfo.GetCultureInfo("ru-RU")),
        "norm" => (value / 1000000m).ToString("0.00000", CultureInfo.GetCultureInfo("ru-RU")),
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    private static async Task<ReportResultDto> Build(EmirDb db, string reportId, string period, string cut, string who, DateOnly? from, DateOnly? to)
    {
        var definition = Catalog.Reports.FirstOrDefault(item => item.Id == reportId) ?? throw new ApiError(404, "Отчёт не найден");
        if (definition.Kind == "debt") return await BuildDebt(db, cut, who);
        if (definition.Kind == "cash") return await BuildCash(db, cut, who, period, from, to);
        if (definition.Kind == "ledger") return await BuildTurnover(db, cut, who, period, from, to);
        if (definition.Kind == "sum") return await BuildSum(db, definition.Id, cut, who, period, from, to);
        if (definition.Kind == "use") return await BuildUse(db, definition.Id, cut, who, period, from, to);
        return Unavailable();
    }

    private static ReportResultDto Unavailable() => new(false, NotConnected, [], [], []);
    private static ReportResultDto NoData(string what) => new(false, $"Для этого разреза нет данных: {what} не переносились из старой системы.", [], [], []);

    /// <summary>Входящий остаток − начислено + оплачено + корректировки = исходящий. Остаток: минус это долг.</summary>
    private static async Task<ReportResultDto> BuildTurnover(EmirDb db, string cut, string who, string period, DateOnly? from, DateOnly? to)
    {
        var (start, end) = Range(period, from, to);
        var abonents = (await db.Abonents.Include(abonent => abonent.Services).ToListAsync()).Where(abonent => MatchesWho(abonent.Kind, who)).ToDictionary(abonent => abonent.Id);
        var entries = (await db.LedgerEntries.Where(entry => entry.AtUtc < end).ToListAsync()).Where(entry => abonents.ContainsKey(entry.AbonentId)).ToList();
        var names = abonents.Values.SelectMany(abonent => abonent.Services).GroupBy(line => (line.AbonentId, line.ServiceId)).ToDictionary(group => group.Key, group => group.First().Name);

        string? Key(LedgerEntry entry) => cut switch
        {
            "Абоненты" => abonents[entry.AbonentId].ShortName,
            "Услуги" => entry.ServiceId is null ? "Переплата" : names.GetValueOrDefault((entry.AbonentId, entry.ServiceId), entry.ServiceId),
            _ => null,
        };
        if (cut is not ("Абоненты" or "Услуги")) return Unavailable();

        static long Effect(IEnumerable<LedgerEntry> items) => items.Sum(entry => entry.CreditDelta - entry.DebtDelta);
        // Входящие данные относятся к началу учёта, в какой бы день их ни занесли
        static bool Before(LedgerEntry entry, DateTime start) => entry.AtUtc < start || entry.Kind == "opening";

        var rows = entries.GroupBy(entry => Key(entry)!).OrderBy(group => group.Key).Select(group =>
        {
            var inRange = group.Where(entry => !Before(entry, start)).ToList();
            var opening = Effect(group.Where(entry => Before(entry, start)));
            var charged = -Effect(inRange.Where(entry => entry.Kind is "charge" or "reading" or "penalty" or "reclose" or "cancel"));
            var paid = Effect(inRange.Where(entry => entry.Kind is "payment" or "exchange" or "refund"));
            var corrected = Effect(inRange.Where(entry => entry.Kind == "correction"));
            return new ReportRowDto(group.Key, [opening, charged, paid, corrected, opening - charged + paid + corrected]);
        }).ToList();

        // В выбранном диапазоне абонента ещё не было: строка нулевая, чтобы список не зависел от дат
        if (cut == "Абоненты")
            rows = abonents.Values.Select(abonent => abonent.ShortName).Distinct().OrderBy(name => name)
                .Select(name => rows.FirstOrDefault(row => row.Name == name) ?? new ReportRowDto(name, [0, 0, 0, 0, 0])).ToList();

        return Finish([new("Входящий остаток, ₽", "money"), new("Начислено, ₽", "money"), new("Оплачено, ₽", "money"), new("Корректировки, ₽", "money"), new("Исходящий остаток, ₽", "money")], rows);
    }

    private static async Task<ReportResultDto> BuildDebt(EmirDb db, string cut, string who)
    {
        var abonents = (await db.Abonents.Include(abonent => abonent.Services).ToListAsync()).Where(abonent => MatchesWho(abonent.Kind, who)).ToList();
        static long Debt(IEnumerable<ServiceLine> lines) => lines.Where(line => line.ServiceId != "penalty").Sum(line => line.Debt);
        static long Fee(IEnumerable<ServiceLine> lines) => lines.Where(line => line.ServiceId == "penalty").Sum(line => line.Debt);

        List<ReportRowDto> rows;
        List<ReportColumnDto> columns;
        var money = new ReportColumnDto[] { new("Долг, ₽", "money"), new("Пени, ₽", "money"), new("Итого, ₽", "money") };
        switch (cut)
        {
            case "Абоненты":
                columns = [new("Услуг с долгом", "count", true), .. money];
                rows = abonents.Where(abonent => abonent.Services.Any(line => line.Debt > 0)).OrderBy(abonent => abonent.Name)
                    .Select(abonent => new ReportRowDto(abonent.ShortName, [abonent.Services.Count(line => line.Debt > 0), Debt(abonent.Services), Fee(abonent.Services), Debt(abonent.Services) + Fee(abonent.Services)])).ToList();
                break;
            case "Дома":
                columns = [new("Абонентов", "count"), .. money];
                rows = abonents.Where(abonent => abonent.Services.Any(line => line.Debt > 0)).GroupBy(HouseOf).OrderBy(house => house.Key)
                    .Select(house =>
                    {
                        var lines = house.SelectMany(abonent => abonent.Services).ToList();
                        return new ReportRowDto(house.Key, [house.Count(), Debt(lines), Fee(lines), Debt(lines) + Fee(lines)]);
                    }).ToList();
                break;
            case "Услуги":
                columns = [new("Абонентов", "count"), .. money];
                rows = abonents.SelectMany(abonent => abonent.Services).Where(line => line.Debt > 0).GroupBy(line => line.Name).OrderBy(service => service.Key)
                    .Select(service => new ReportRowDto(service.Key, [service.Count(), Debt(service), Fee(service), service.Sum(line => line.Debt)])).ToList();
                break;
            default:
                return Unavailable();
        }
        return Finish(columns, rows);
    }

    private static async Task<ReportResultDto> BuildCash(EmirDb db, string cut, string who, string period, DateOnly? from, DateOnly? to)
    {
        var (start, end) = Range(period, from, to);
        var persons = await db.Abonents.Select(abonent => new { abonent.Id, abonent.Kind }).ToDictionaryAsync(abonent => abonent.Id, abonent => abonent.Kind);
        var payments = (await db.Payments.Where(payment => (payment.Status == "Проведена" || payment.Status == "Перенесён") && payment.AtUtc >= start && payment.AtUtc < end).ToListAsync())
            .Where(payment => persons.TryGetValue(payment.AbonentId, out var kind) && MatchesWho(kind, who)).ToList();

        Func<Payment, string>? key = cut switch
        {
            "Дни" => payment => payment.AtUtc.ToLocalTime().ToString("yyyy-MM-dd"),
            "Кассиры" => payment => payment.Cashier,
            "Офисы" or "Провайдеры" => payment => payment.Source,
            _ => null,
        };
        if (key is null) return Unavailable();

        List<ReportColumnDto> columns = [new("Платежей", "count"), new("Наличные, ₽", "money"), new("Безнал, ₽", "money"), new("Итого, ₽", "money")];
        var rows = payments.GroupBy(key).OrderBy(group => group.Key).Select(group =>
        {
            var noncash = group.Where(payment => payment.Source == "Банковские платежи").Sum(payment => payment.Amount);
            var total = group.Sum(payment => payment.Amount);
            return new ReportRowDto(group.Key, [group.Count(), total - noncash, noncash, total]);
        }).ToList();
        return Finish(columns, rows);
    }

    /// <summary>Месяцы начисления, которые пересекаются с выбранным периодом отчёта.</summary>
    private static HashSet<string> Months(DateTime start, DateTime end)
    {
        var months = new HashSet<string>();
        var first = new DateTime(start.ToLocalTime().Year, start.ToLocalTime().Month, 1);
        for (var month = first; month < end.ToLocalTime(); month = month.AddMonths(1)) months.Add(month.ToString("yyyy-MM"));
        return months;
    }

    /// <summary>Отчёты по начислениям и состоянию счетов. Суммы в копейках, объём в тысячных долях.</summary>
    private static async Task<ReportResultDto> BuildSum(EmirDb db, string reportId, string cut, string who, string period, DateOnly? from, DateOnly? to)
    {
        var (start, end) = Range(period, from, to);
        var months = Months(start, end);
        var abonents = (await db.Abonents.Include(abonent => abonent.Services).ToListAsync()).Where(abonent => MatchesWho(abonent.Kind, who)).ToDictionary(abonent => abonent.Id);
        var lines = abonents.Values.SelectMany(abonent => abonent.Services).ToDictionary(line => (line.AbonentId, line.ServiceId));
        string Label(Abonent abonent) => $"{abonent.Account} · {abonent.ShortName}";
        string ServiceName(string abonentId, string serviceId) => serviceId == "penalty" ? "Пени" : lines.TryGetValue((abonentId, serviceId), out var line) ? line.Name : serviceId;
        string House(Abonent abonent) => HouseOf(abonent);

        async Task<List<ChargeEntry>> Charges()
        {
            var list = await db.ChargeEntries.Where(entry => months.Contains(entry.Period)).ToListAsync();
            return list.Where(entry => abonents.ContainsKey(entry.AbonentId)).ToList();
        }
        async Task<List<Payment>> Payments()
        {
            var list = await db.Payments.Where(payment => (payment.Status == "Проведена" || payment.Status == "Перенесён") && payment.AtUtc >= start && payment.AtUtc < end).ToListAsync();
            return list.Where(payment => abonents.ContainsKey(payment.AbonentId)).ToList();
        }
        static long Volume(IEnumerable<ChargeEntry> entries) => (long)Math.Round(entries.Sum(entry => entry.Volume) * 1000m);

        switch (reportId)
        {
            case "charges":
            {
                var charges = await Charges();
                Func<ChargeEntry, string>? key = cut switch
                {
                    "Услуги" => entry => ServiceName(entry.AbonentId, entry.ServiceId),
                    "Абоненты" => entry => Label(abonents[entry.AbonentId]),
                    _ => null,
                };
                if (key is null) return Unavailable();
                var rows = charges.GroupBy(key).OrderBy(group => group.Key)
                    .Select(group => new ReportRowDto(group.Key, [group.Select(entry => entry.AbonentId).Distinct().Count(), Volume(group), group.Sum(entry => entry.Amount)])).ToList();
                return Finish([new("Абонентов", "count", true), new("Объём", "volume"), new("Начислено, ₽", "money")], rows);
            }
            case "tariffs":
            {
                var charges = (await Charges()).Where(entry => entry.ServiceId != "penalty").ToList();
                var tariffs = await db.Tariffs.ToDictionaryAsync(tariff => tariff.Id, tariff => tariff.Name);
                string TariffName(ChargeEntry entry)
                {
                    var id = entry.TariffId ?? (lines.TryGetValue((entry.AbonentId, entry.ServiceId), out var line) ? line.TariffId : null);
                    return id is not null && tariffs.TryGetValue(id, out var name) ? name : "Без тарифа";
                }
                var rows = charges.GroupBy(TariffName).OrderBy(group => group.Key)
                    .Select(group => new ReportRowDto(group.Key, [group.Select(entry => entry.AbonentId).Distinct().Count(), Volume(group), group.Sum(entry => entry.Amount)])).ToList();
                return Finish([new("Абонентов", "count", true), new("Объём", "volume"), new("Начислено, ₽", "money")], rows);
            }
            case "balance":
            {
                // Состояние на сейчас: долг по услугам и переплата абонента
                Func<ServiceLine, string>? key = cut switch
                {
                    "Услуги" => line => line.ServiceId == "penalty" ? "Пени" : line.Name,
                    "Сельсоветы" => line => abonents[line.AbonentId].Place == "" ? "Без населённого пункта" : abonents[line.AbonentId].Place,
                    _ => null,
                };
                if (key is null) return Unavailable();
                var rows = lines.Values.GroupBy(key).OrderBy(group => group.Key).Select(group =>
                {
                    var debt = group.Where(line => line.Debt > 0).Sum(line => line.Debt);
                    var credit = -group.Where(line => line.Debt < 0).Sum(line => line.Debt);
                    return new ReportRowDto(group.Key, [group.Select(line => line.AbonentId).Distinct().Count(), debt, credit, debt - credit]);
                }).ToList();
                if (cut == "Сельсоветы")
                {
                    // Переплата лежит у абонента, а не у услуги: добавляется по населённому пункту
                    foreach (var place in abonents.Values.Where(abonent => abonent.Credit > 0).GroupBy(abonent => abonent.Place == "" ? "Без населённого пункта" : abonent.Place))
                    {
                        var index = rows.FindIndex(row => row.Name == place.Key);
                        var credit = place.Sum(abonent => abonent.Credit);
                        if (index < 0) rows.Add(new ReportRowDto(place.Key, [place.Count(), 0, credit, -credit]));
                        else rows[index] = rows[index] with { Values = [rows[index].Values[0], rows[index].Values[1], rows[index].Values[2] + credit, rows[index].Values[3] - credit] };
                    }
                }
                else
                {
                    var credit = abonents.Values.Sum(abonent => abonent.Credit);
                    if (credit > 0) rows.Add(new ReportRowDto("Переплата абонентов", [abonents.Values.Count(abonent => abonent.Credit > 0), 0, credit, -credit]));
                }
                return Finish([new("Абонентов", "count", true), new("Долг, ₽", "money"), new("Переплата, ₽", "money"), new("Итого к оплате, ₽", "money")], rows.OrderBy(row => row.Name).ToList());
            }
            case "monthly":
            {
                var charges = await Charges();
                var payments = await Payments();
                Func<ChargeEntry, string>? chargeKey = cut switch
                {
                    "Услуги" => entry => ServiceName(entry.AbonentId, entry.ServiceId),
                    "Месяцы" => entry => entry.Period,
                    _ => null,
                };
                if (chargeKey is null) return Unavailable();
                var names = charges.Select(chargeKey).ToHashSet();
                if (cut == "Месяцы") foreach (var payment in payments) names.Add(payment.AtUtc.ToLocalTime().ToString("yyyy-MM"));
                var rows = names.OrderBy(name => name).Select(name =>
                {
                    var charged = charges.Where(entry => chargeKey(entry) == name).Sum(entry => entry.Amount);
                    var paid = cut == "Месяцы" ? payments.Where(payment => payment.AtUtc.ToLocalTime().ToString("yyyy-MM") == name).Sum(payment => payment.Amount) : 0;
                    return new ReportRowDto(name, [charged, paid]);
                }).ToList();
                if (cut == "Услуги") rows.Add(new ReportRowDto("Оплаты (без разбивки по услугам)", [0, payments.Sum(payment => payment.Amount)]));
                return Finish([new("Начислено, ₽", "money"), new("Оплачено, ₽", "money")], rows);
            }
            case "print-charges":
            {
                var charges = await Charges();
                Func<ChargeEntry, string>? key = cut switch
                {
                    "Абоненты" => entry => Label(abonents[entry.AbonentId]),
                    "Дома" => entry => House(abonents[entry.AbonentId]),
                    _ => null,
                };
                if (key is null) return Unavailable();
                var rows = charges.GroupBy(key).OrderBy(group => group.Key).Select(group =>
                {
                    var penalty = group.Where(entry => entry.ServiceId == "penalty").Sum(entry => entry.Amount);
                    var total = group.Sum(entry => entry.Amount);
                    return new ReportRowDto(group.Key, [total - penalty, penalty, total]);
                }).ToList();
                return Finish([new("Начислено за услуги, ₽", "money"), new("Пени, ₽", "money"), new("Всего, ₽", "money")], rows);
            }
            case "print-state":
            {
                Func<Abonent, string>? key = cut switch
                {
                    "Абоненты" => Label,
                    "Дома" => House,
                    _ => null,
                };
                if (key is null) return Unavailable();
                var rows = abonents.Values.GroupBy(key).OrderBy(group => group.Key).Select(group =>
                {
                    var debt = group.SelectMany(abonent => abonent.Services).Where(line => line.Debt > 0).Sum(line => line.Debt);
                    var credit = group.Sum(abonent => abonent.Credit) - group.SelectMany(abonent => abonent.Services).Where(line => line.Debt < 0).Sum(line => line.Debt);
                    // К оплате считается по каждому абоненту: переплата одного не гасит долг соседа
                    var due = group.Sum(abonent => Math.Max(0, abonent.Services.Sum(line => line.Debt) - abonent.Credit));
                    return new ReportRowDto(group.Key, [debt, credit, due]);
                }).ToList();
                return Finish([new("Долг, ₽", "money"), new("Переплата, ₽", "money"), new("К оплате, ₽", "money")], rows);
            }
            default:
                return Unavailable();
        }
    }

/// <summary>Отчёты по потреблению, домам и жильцам. Объём и площадь в тысячных долях, норма в миллионных.</summary>
    private static async Task<ReportResultDto> BuildUse(EmirDb db, string reportId, string cut, string who, string period, DateOnly? from, DateOnly? to)
    {
        var (start, end) = Range(period, from, to);
        var months = Months(start, end);
        var abonents = (await db.Abonents.Include(abonent => abonent.Services).ToListAsync()).Where(abonent => MatchesWho(abonent.Kind, who)).ToDictionary(abonent => abonent.Id);
        var lines = abonents.Values.SelectMany(abonent => abonent.Services).ToDictionary(line => (line.AbonentId, line.ServiceId));
        string Label(Abonent abonent) => $"{abonent.Account} · {abonent.ShortName}";
        string Place(Abonent abonent) => abonent.Place == "" ? "Без населённого пункта" : abonent.Place;
        static long Thousandths(decimal value) => (long)Math.Round(value * 1000m);
        bool IsHeat(ChargeEntry entry) => entry.ServiceId != "penalty" && lines.TryGetValue((entry.AbonentId, entry.ServiceId), out var line) && !line.Name.Contains("одоотвед", StringComparison.OrdinalIgnoreCase);
        bool IsWater(ChargeEntry entry) => lines.TryGetValue((entry.AbonentId, entry.ServiceId), out var line) && line.Name.Contains("одоотвед", StringComparison.OrdinalIgnoreCase);
        async Task<List<ChargeEntry>> Charges() => (await db.ChargeEntries.Where(entry => months.Contains(entry.Period) && entry.ServiceId != "penalty").ToListAsync()).Where(entry => abonents.ContainsKey(entry.AbonentId)).ToList();
        ReportResultDto Consumption(IEnumerable<ChargeEntry> charges, Func<ChargeEntry, string> key, bool withArea) =>
            Finish(withArea
                ? [new("Абонентов", "count", true), new("Площадь, м²", "volume", true), new("Объём", "volume"), new("Начислено, ₽", "money")]
                : [new("Абонентов", "count", true), new("Объём", "volume"), new("Начислено, ₽", "money")],
                charges.GroupBy(key).OrderBy(group => group.Key).Select(group =>
                {
                    var ids = group.Select(entry => entry.AbonentId).Distinct().ToList();
                    List<long> values = [ids.Count];
                    if (withArea) values.Add(Thousandths(ids.Sum(id => abonents[id].Area)));
                    values.Add(Thousandths(group.Sum(entry => entry.Volume)));
                    values.Add(group.Sum(entry => entry.Amount));
                    return new ReportRowDto(group.Key, values);
                }).ToList());

        switch (reportId)
        {
            case "volume":
            {
                var charges = await Charges();
                return cut switch
                {
                    "Услуги" => Consumption(charges, entry => lines[(entry.AbonentId, entry.ServiceId)].Name, false),
                    "Абоненты" => Consumption(charges, entry => Label(abonents[entry.AbonentId]), false),
                    "Дома" => Consumption(charges, entry => HouseOf(abonents[entry.AbonentId]), true),
                    "Населённые пункты" => Consumption(charges, entry => Place(abonents[entry.AbonentId]), true),
                    "Юрлица" => Consumption(charges.Where(entry => abonents[entry.AbonentId].Kind == "org"), entry => Label(abonents[entry.AbonentId]), false),
                    "Сельсоветы" => NoData("сельсоветы"),
                    _ => Unavailable(),
                };
            }
            case "heat":
            {
                var charges = (await Charges()).Where(IsHeat).ToList();
                return cut switch
                {
                    "Дома" => Consumption(charges, entry => HouseOf(abonents[entry.AbonentId]), true),
                    "Квартиры" => Consumption(charges, entry => Label(abonents[entry.AbonentId]), true),
                    "Тепловые камеры" => NoData("тепловые камеры"),
                    "Котельные" => NoData("котельные"),
                    _ => Unavailable(),
                };
            }
            case "water":
            {
                var charges = (await Charges()).Where(IsWater).ToList();
                return cut switch
                {
                    "Улицы" => Consumption(charges, entry => HouseOf(abonents[entry.AbonentId]), false),
                    "Скважины" => NoData("скважины"),
                    _ => Unavailable(),
                };
            }
            case "houses":
            {
                if (cut == "Дома")
                {
                    var rows = abonents.Values.GroupBy(HouseOf).OrderBy(group => group.Key).Select(group => new ReportRowDto(group.Key,
                        [group.Count(), group.Sum(abonent => abonent.Residents), Thousandths(group.Sum(abonent => abonent.Area)), group.SelectMany(abonent => abonent.Services).Where(line => line.Debt > 0).Sum(line => line.Debt)])).ToList();
                    return Finish([new("Абонентов", "count"), new("Жильцов", "count"), new("Площадь, м²", "volume"), new("Долг, ₽", "money")], rows);
                }
                if (cut == "Тарифы")
                {
                    // У абонента может быть несколько тарифов: долг берётся только по услугам этого тарифа,
                    // а число абонентов, жильцов и площадь по тарифам не суммируются в общий итог
                    var rows = lines.Values.Where(line => line.State == "Работает" && line.ServiceId != "penalty").GroupBy(line => line.TariffName).OrderBy(group => group.Key).Select(group =>
                    {
                        var owners = group.Select(line => abonents[line.AbonentId]).DistinctBy(abonent => abonent.Id).ToList();
                        return new ReportRowDto(group.Key, [owners.Count, owners.Sum(abonent => abonent.Residents), Thousandths(owners.Sum(abonent => abonent.Area)), group.Where(line => line.Debt > 0).Sum(line => line.Debt)]);
                    }).ToList();
                    return Finish([new("Абонентов", "count", true), new("Жильцов", "count", true), new("Площадь, м²", "volume", true), new("Долг по услугам тарифа, ₽", "money")], rows);
                }
                return Unavailable();
            }
            case "residents":
            {
                if (cut == "Сельсоветы") return NoData("сельсоветы");
                if (cut != "Дома") return Unavailable();
                var first = DateOnly.FromDateTime(start.ToLocalTime());
                var last = DateOnly.FromDateTime(end.ToLocalTime()).AddDays(-1);
                var residents = (await db.Residents.ToListAsync()).Where(item => abonents.ContainsKey(item.AbonentId)).ToList();
                var rows = residents.GroupBy(item => HouseOf(abonents[item.AbonentId])).OrderBy(group => group.Key).Select(group => new ReportRowDto(group.Key,
                [
                    group.Count(item => (item.FromDate is null || item.FromDate <= last) && (item.ToDate is null || item.ToDate >= last)),
                    group.Count(item => item.FromDate is { } date && date >= first && date <= last),
                    group.Count(item => item.ToDate is { } date && date >= first && date <= last),
                ])).ToList();
                return Finish([new("Проживает на конец периода", "count"), new("Въехало", "count"), new("Выбыло", "count")], rows);
            }
            case "norms":
            {
                List<ReportColumnDto> columns = [new("Абонентов", "count"), new("Жильцов", "count"), new("Площадь, м²", "volume"), new("Норма дома, Гкал/м²", "norm", true)];
                Func<Abonent, IEnumerable<string>>? keys = cut switch
                {
                    "Услуги" => abonent => abonent.Services.Where(line => line.State == "Работает" && line.ServiceId != "penalty").Select(line => line.Name).Distinct(),
                    "Дома" => abonent => [HouseOf(abonent)],
                    _ => null,
                };
                if (keys is null) return Unavailable();
                var rows = abonents.Values.SelectMany(abonent => keys(abonent).Select(key => (key, abonent))).GroupBy(pair => pair.key, pair => pair.abonent).OrderBy(group => group.Key).Select(group =>
                {
                    var norms = group.Where(abonent => abonent.HouseHeatNorm is not null).Select(abonent => abonent.HouseHeatNorm!.Value).Distinct().ToList();
                    return new ReportRowDto(group.Key, [group.Count(), group.Sum(abonent => abonent.Residents), Thousandths(group.Sum(abonent => abonent.Area)), norms.Count == 1 ? (long)Math.Round(norms[0] * 1000000m) : 0]);
                }).ToList();
                return Finish(columns, rows);
            }
            case "categories":
            {
                // Названия категорий не переносились: категория показана своей нормой теплопотребления
                var rows = abonents.Values.GroupBy(abonent => abonent.HouseHeatNorm is { } norm ? $"Норма {norm.ToString("0.00000", CultureInfo.GetCultureInfo("ru-RU"))} Гкал/м²" : "Без категории").OrderBy(group => group.Key).Select(group =>
                    new ReportRowDto(group.Key, [group.Select(HouseOf).Distinct().Count(), group.Count(), Thousandths(group.Sum(abonent => abonent.Area))])).ToList();
                return Finish([new("Домов", "count"), new("Абонентов", "count"), new("Площадь, м²", "volume")], rows);
            }
            default:
                return Unavailable();
        }
    }

    /// <summary>Дом без квартиры: в адресе после дома через дефис идёт номер квартиры.</summary>
    private static string HouseOf(Abonent abonent)
    {
        var street = System.Text.RegularExpressions.Regex.Replace(abonent.Street, @"-[^\s-]*$", "");
        return abonent.Place == "" ? street : $"{abonent.Place}, {street}";
    }

    private static ReportResultDto Finish(List<ReportColumnDto> columns, List<ReportRowDto> rows)
    {
        var totals = columns.Select((column, index) => column.NoTotal ? (long?)null : rows.Sum(row => row.Values[index])).ToList();
        return new ReportResultDto(true, null, columns, rows, totals);
    }

    private static bool MatchesWho(string kind, string who) => who switch
    {
        "Физлица" => kind == "person",
        "Юрлица" => kind == "org",
        _ => true,
    };

    private static (DateTime Start, DateTime End) Range(string period, DateOnly? from, DateOnly? to)
    {
        var today = DateTime.Today;
        var month = new DateTime(today.Year, today.Month, 1);
        return period switch
        {
            "Прошлый" => (month.AddMonths(-1).ToUniversalTime(), month.ToUniversalTime()),
            "Квартал" => (month.AddMonths(-2).ToUniversalTime(), month.AddMonths(1).ToUniversalTime()),
            "Свои даты" => from is { } start && to is { } end
                ? (start.ToDateTime(TimeOnly.MinValue).ToUniversalTime(), end.AddDays(1).ToDateTime(TimeOnly.MinValue).ToUniversalTime())
                : throw new ApiError(422, "Укажите даты"),
            _ => (month.ToUniversalTime(), month.AddMonths(1).ToUniversalTime()),
        };
    }
}
