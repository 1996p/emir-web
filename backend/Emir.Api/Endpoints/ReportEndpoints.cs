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
    private const string NotConnected = "Этот отчёт появится после переноса данных из старой системы.";

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
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    private static async Task<ReportResultDto> Build(EmirDb db, string reportId, string period, string cut, string who, DateOnly? from, DateOnly? to)
    {
        var definition = Catalog.Reports.FirstOrDefault(item => item.Id == reportId) ?? throw new ApiError(404, "Отчёт не найден");
        if (definition.Kind == "debt") return await BuildDebt(db, cut, who);
        if (definition.Kind == "cash") return await BuildCash(db, cut, who, period, from, to);
        if (definition.Kind == "ledger") return await BuildTurnover(db, cut, who, period, from, to);
        return Unavailable();
    }

    private static ReportResultDto Unavailable() => new(false, NotConnected, [], [], []);

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
                rows = abonents.Where(abonent => abonent.Services.Any(line => line.Debt > 0)).GroupBy(abonent => abonent.Street).OrderBy(house => house.Key)
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
        var payments = (await db.Payments.Where(payment => payment.Status == "Проведена" && payment.AtUtc >= start && payment.AtUtc < end).ToListAsync())
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
