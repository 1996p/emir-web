using System.Text.Json;
using System.Text.RegularExpressions;
using Emir.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

public record DocumentParty(string Name, string? Inn, string? Kpp, string Address);
public record DocumentSeller(string Name, string Inn, string? Kpp, string Address, string? BankName, string? Bik, string? CorrAccount, string? Account, string? Director, string? ChiefAccountant);
public record DocumentLine(string Service, string Unit, string UnitCode, decimal? Volume, decimal? Price, long Amount, long Vat, long Debt);

/// <summary>Снимок на момент выпуска: документ не меняется, когда потом меняются цены, адрес или реквизиты.</summary>
public record DocumentData(
    string Title, string Number, int Revision, string Date, string RevisionDate, string PeriodTitle, string Contract,
    DocumentSeller Seller, DocumentParty Buyer, decimal? VatRate, List<DocumentLine> Lines,
    long Total, long VatTotal, long Debt, long Credit, long ToPay, string TotalWords, string ToPayWords, string? ActNumber = null);

public record GenerateResult(int Created, int Existing, int Abonents);

public static partial class DocumentService
{
    public const string Receipt = "receipt", Bill = "bill", Act = "act", Facture = "facture";

    public static string KindName(string kind) => kind switch
    {
        Receipt => "Квитанция", Bill => "Счёт", Act => "Акт", Facture => "Счёт-фактура", _ => kind,
    };

    private static readonly Dictionary<string, string> UnitCodes = new() { ["м³"] = "113", ["м²"] = "055", ["Гкал"] = "233", ["кВт·ч"] = "245" };

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Digits();

    public static void Validate(OrganizationProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ApiError(422, "Укажите название организации");
        if (string.IsNullOrWhiteSpace(profile.Address)) throw new ApiError(422, "Укажите адрес организации");
        CheckDigits(profile.Inn, [10, 12], "ИНН", required: true);
        CheckDigits(profile.Kpp, [9], "КПП");
        CheckDigits(profile.Bik, [9], "БИК", required: true);
        CheckDigits(profile.CorrAccount, [20], "Корреспондентский счёт");
        CheckDigits(profile.Account, [20], "Расчётный счёт", required: true);
        if (string.IsNullOrWhiteSpace(profile.BankName)) throw new ApiError(422, "Укажите банк");
        if (profile.VatRate is < 0 or > 100) throw new ApiError(422, "Ставка НДС должна быть от 0 до 100");
    }

    private static void CheckDigits(string? value, int[] lengths, string name, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required) throw new ApiError(422, $"Укажите: {name}");
            return;
        }
        if (!Digits().IsMatch(value) || !lengths.Contains(value.Length)) throw new ApiError(422, $"{name}: нужно цифр: {string.Join(" или ", lengths)}");
    }

    /// <summary>Документы выпускаются только по закрытому месяцу, иначе суммы ещё могут измениться.</summary>
    private static async Task<(AccountingMonth Month, OrganizationProfile Profile)> Ready(EmirDb db, string period)
    {
        var month = await db.AccountingMonths.FirstOrDefaultAsync(item => item.Period == period) ?? throw new ApiError(404, "Такого месяца нет");
        if (month.ClosedAtUtc is null) throw new ApiError(409, "Документы формируются после закрытия месяца");
        if (month.ReopenedAtUtc is not null) throw new ApiError(409, "Месяц исправляется. Сначала завершите перезакрытие");
        var profile = await db.OrganizationProfiles.FirstOrDefaultAsync() ?? throw new ApiError(409, "Заполните реквизиты организации");
        Validate(profile);
        return (month, profile);
    }

    private static string[] KindsFor(Abonent abonent) => abonent.Kind == "org" ? [Bill, Act, Facture] : [Receipt];

    public static async Task<GenerateResult> Generate(EmirDb db, string period, string who)
    {
        var (_, profile) = await Ready(db, period);
        var abonents = await db.Abonents.Include(abonent => abonent.Services).OrderBy(abonent => abonent.Account).ToListAsync();
        var entries = await db.ChargeEntries.Where(entry => entry.Period == period && entry.Scheme != "Opening" && entry.Amount > 0).ToListAsync();
        var existing = await db.BillingDocuments.Where(document => document.Period == period && document.Status == "active").ToListAsync();
        var tariffs = await db.Tariffs.ToDictionaryAsync(tariff => tariff.Id);
        var now = DateTime.UtcNow;
        int created = 0, skipped = 0, people = 0;

        foreach (var abonent in abonents)
        {
            var own = entries.Where(entry => entry.AbonentId == abonent.Id).ToList();
            if (own.Count == 0) continue;
            var any = false;
            // Счёт-фактура ссылается на акт, а номера у них свои: акт выпускается раньше
            var actNumber = existing.FirstOrDefault(document => document.AbonentId == abonent.Id && document.Kind == Act)?.Number;
            foreach (var kind in KindsFor(abonent))
            {
                if (existing.Any(document => document.AbonentId == abonent.Id && document.Kind == kind))
                {
                    skipped++;
                    continue;
                }
                if (!HasLines(kind, own)) continue;
                var number = await NextNumber(db, kind, abonent, period);
                if (kind == Act) actNumber = number;
                var data = Build(kind, abonent, own, tariffs, profile, period, number, 1, now, actNumber);
                if (data is null) continue;
                db.BillingDocuments.Add(new BillingDocument { Kind = kind, Period = period, Year = int.Parse(period[..4]), AbonentId = abonent.Id, Number = data.Number, Revision = 1, Status = "active", Total = kind == Receipt ? data.ToPay : data.Total, Data = JsonSerializer.Serialize(data, Json), Who = who, AtUtc = now });
                created++;
                any = true;
            }
            if (any) people++;
        }
        return new GenerateResult(created, skipped, people);
    }

    /// <summary>Перевыпуск: номер тот же, растёт номер исправления, прежний документ остаётся в истории.</summary>
    public static async Task<BillingDocument> Reissue(EmirDb db, int id, string who, string reason)
    {
        var old = await db.BillingDocuments.FirstOrDefaultAsync(document => document.Id == id) ?? throw new ApiError(404, "Документ не найден");
        if (old.Status != "active") throw new ApiError(409, "Этот документ уже заменён");
        var (_, profile) = await Ready(db, old.Period);
        var abonent = await db.GetAbonentForDocuments(old.AbonentId);
        var entries = await db.ChargeEntries.Where(entry => entry.Period == old.Period && entry.AbonentId == old.AbonentId && entry.Scheme != "Opening" && entry.Amount > 0).ToListAsync();
        var tariffs = await db.Tariffs.ToDictionaryAsync(tariff => tariff.Id);
        var now = DateTime.UtcNow;

        var revision = old.Revision + 1;
        var actNumber = await db.BillingDocuments.Where(document => document.AbonentId == old.AbonentId && document.Period == old.Period && document.Kind == Act && document.Status == "active").Select(document => document.Number).FirstOrDefaultAsync();
        var data = Build(old.Kind, abonent, entries, tariffs, profile, old.Period, old.Number, revision, now, actNumber) ?? throw new ApiError(409, "За этот месяц начислений больше нет. Документ выпускать не на что");
        old.Status = "replaced";
        old.Stale = false;
        // Сначала снять отметку active: уникальный индекс не допускает два действующих документа
        await db.SaveChangesAsync();
        var document = new BillingDocument { Kind = old.Kind, Period = old.Period, Year = old.Year, AbonentId = old.AbonentId, Number = old.Number, Revision = revision, Status = "active", Total = old.Kind == Receipt ? data.ToPay : data.Total, Data = JsonSerializer.Serialize(data, Json), Who = who, AtUtc = now, Reason = reason };
        db.BillingDocuments.Add(document);
        return document;
    }

    private static Task<Abonent> GetAbonentForDocuments(this EmirDb db, string id) =>
        db.Abonents.Include(abonent => abonent.Services).FirstAsync(abonent => abonent.Id == id);

    private static async Task<string> NextNumber(EmirDb db, string kind, Abonent abonent, string period)
    {
        if (kind == Receipt) return $"{abonent.Account}-{period}";
        var year = int.Parse(period[..4]);
        var counter = db.DocumentCounters.Local.FirstOrDefault(item => item.Year == year && item.Kind == kind)
            ?? await db.DocumentCounters.FirstOrDefaultAsync(item => item.Year == year && item.Kind == kind);
        if (counter is null)
        {
            counter = new DocumentCounter { Year = year, Kind = kind, Last = 0 };
            db.DocumentCounters.Add(counter);
        }
        counter.Last++;
        return counter.Last.ToString();
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static bool HasLines(string kind, List<ChargeEntry> entries) => kind == Receipt || entries.Any(entry => entry.ServiceId != "penalty");

    private static DocumentData? Build(string kind, Abonent abonent, List<ChargeEntry> entries, Dictionary<string, Tariff> tariffs, OrganizationProfile profile, string period, string number, int revision, DateTime now, string? actNumber)
    {
        var services = abonent.Services.ToDictionary(line => line.ServiceId);
        // Пени не услуга: в счёт, акт и счёт-фактуру не входят, в квитанцию входят отдельной строкой
        var lines = entries.Where(entry => kind == Receipt || entry.ServiceId != "penalty")
            .OrderBy(entry => entry.ServiceId == "penalty").ThenBy(entry => services.GetValueOrDefault(entry.ServiceId)?.Position ?? 0)
            .Select(entry =>
            {
                var unit = entry.TariffId is { } id && tariffs.TryGetValue(id, out var tariff) ? tariff.Unit ?? "усл. ед." : "усл. ед.";
                var vat = entry.ServiceId == "penalty" || profile.VatRate is not { } rate ? 0 : (long)Math.Round(entry.Amount * rate / (100 + rate), MidpointRounding.AwayFromZero);
                return new DocumentLine(services.GetValueOrDefault(entry.ServiceId)?.Name ?? entry.ServiceId, entry.ServiceId == "penalty" ? "—" : unit, UnitCodes.GetValueOrDefault(unit, "876"),
                    entry.ServiceId == "penalty" ? null : entry.Volume, entry.Price, entry.Amount, vat, services.GetValueOrDefault(entry.ServiceId)?.Debt ?? 0);
            }).ToList();
        if (lines.Count == 0) return null;

        // В квитанции видны все долги, иначе колонка долга не сойдётся с итогом: начисления в этом месяце могло не быть
        if (kind == Receipt)
        {
            var extra = abonent.Services.Where(line => line.Debt > 0 && entries.All(entry => entry.ServiceId != line.ServiceId))
                .OrderBy(line => line.ServiceId == "penalty").ThenBy(line => line.Position)
                .Select(line => new DocumentLine(line.Name, "—", "876", null, null, 0, 0, line.Debt));
            lines.AddRange(extra);
        }

        var total = lines.Sum(line => line.Amount);
        var debt = abonent.Services.Sum(line => line.Debt);
        var toPay = Math.Max(0, debt - abonent.Credit);
        var lastDay = PenaltyEngine.FirstDay(period).AddMonths(1).AddDays(-1);
        var address = string.Join(", ", new[] { abonent.Place, abonent.Street + (abonent.Flat is null ? "" : $", кв. {abonent.Flat}") }.Where(item => !string.IsNullOrWhiteSpace(item)));

        return new DocumentData(KindName(kind), number, revision, lastDay.ToString("dd.MM.yyyy"), revision > 1 ? now.ToLocalTime().ToString("dd.MM.yyyy") : "", Periods.Title(period), abonent.Account,
            new DocumentSeller(profile.Name, profile.Inn, profile.Kpp, profile.Address, profile.BankName, profile.Bik, profile.CorrAccount, profile.Account, profile.Director, profile.ChiefAccountant),
            new DocumentParty(abonent.Name, abonent.Inn, null, address), profile.VatRate, lines,
            total, lines.Sum(line => line.Vat), debt, abonent.Credit, toPay, MoneyWords.Rubles(total), MoneyWords.Rubles(toPay), actNumber);
    }
}

/// <summary>Сумма прописью для счёта и акта.</summary>
public static class MoneyWords
{
    private static readonly string[] Hundreds = ["", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот"];
    private static readonly string[] Tens = ["", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто"];
    private static readonly string[] Teens = ["десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать"];
    private static readonly string[] OnesMale = ["", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять"];
    private static readonly string[] OnesFemale = ["", "одна", "две", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять"];

    private static string Form(long value, string one, string few, string many)
    {
        var tail = value % 100;
        if (tail is >= 11 and <= 14) return many;
        return (tail % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    private static IEnumerable<string> Triple(int value, bool female)
    {
        yield return Hundreds[value / 100];
        var rest = value % 100;
        if (rest is >= 10 and <= 19)
        {
            yield return Teens[rest - 10];
            yield break;
        }
        yield return Tens[rest / 10];
        yield return (female ? OnesFemale : OnesMale)[rest % 10];
    }

    public static string Rubles(long kopecks)
    {
        var rubles = Math.Abs(kopecks) / 100;
        var words = new List<string>();
        if (rubles == 0) words.Add("ноль");
        var groups = new (long Divider, bool Female, string One, string Few, string Many)[]
        {
            (1_000_000_000, false, "миллиард", "миллиарда", "миллиардов"),
            (1_000_000, false, "миллион", "миллиона", "миллионов"),
            (1_000, true, "тысяча", "тысячи", "тысяч"),
        };
        var rest = rubles;
        foreach (var group in groups)
        {
            var part = (int)(rest / group.Divider);
            rest %= group.Divider;
            if (part == 0) continue;
            words.AddRange(Triple(part, group.Female));
            words.Add(Form(part, group.One, group.Few, group.Many));
        }
        words.AddRange(Triple((int)rest, false));
        var text = string.Join(' ', words.Where(word => word != ""));
        var cents = Math.Abs(kopecks) % 100;
        var result = $"{text} {Form(rubles, "рубль", "рубля", "рублей")} {cents:00} {Form(cents, "копейка", "копейки", "копеек")}";
        return char.ToUpperInvariant(result[0]) + result[1..];
    }
}
