using System.Text.Json;
using System.Text.RegularExpressions;
using Emir.Api.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Emir.Api.Domain;

/// <summary>Паспорт физлица. Все поля необязательны, но если паспорт указан, нужны серия и номер.</summary>
public record PassportData(string? Series, string? Number, DateOnly? IssuedAt, string? DepartmentCode);

/// <summary>Услуги, подключаемые при регистрации. Heat: norm | none. Water: meter | norm | none.</summary>
public record RegistrationServices(string? Heat, string? Water, string? MeterNumber, decimal? MeterReading);

/// <summary>Проверка и подготовка данных, которые вводят при регистрации абонента.</summary>
public static partial class Registration
{
    public const string HeatId = "heat", WaterId = "water";
    private const string PassportPurpose = "Emir.Abonent.Passport";

    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$")]
    private static partial Regex EmailPattern();

    public const string PhoneHint = "Телефон: 10 цифр с кодом города или оператора, например +7 (913) 123 45 67";

    /// <summary>Российский номер в едином виде «+7 (913) 123 45 67», как его показывает маска ввода. Принимает 8…, +7…, скобки, пробелы и дефисы.
    /// Нужны 10 цифр без кода страны, код начинается с 3, 4, 8 или 9; номер из одинаковых цифр не настоящий.</summary>
    public static string? Phone(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (Regex.IsMatch(text, @"[^\d\s()+\-.]")) throw new ApiError(422, PhoneHint);
        var digits = Regex.Replace(text, @"\D", "");
        if (digits.Length == 11 && digits[0] is '7' or '8') digits = digits[1..];
        else if (text.StartsWith('+')) throw new ApiError(422, PhoneHint);
        if (digits.Length != 10 || digits[0] is not ('3' or '4' or '8' or '9')) throw new ApiError(422, PhoneHint);
        if (digits.Distinct().Count() == 1) throw new ApiError(422, "Телефон похож на ненастоящий: все цифры одинаковые");
        return $"+7 ({digits[..3]}) {digits[3..6]} {digits[6..8]} {digits[8..]}";
    }

    public static string? Email(string? value)
    {
        var email = value?.Trim();
        if (string.IsNullOrEmpty(email)) return null;
        if (email.Length > 200 || !EmailPattern().IsMatch(email)) throw new ApiError(422, "E-mail указан неверно");
        return email;
    }

    public static DateOnly? BirthDate(DateOnly? value)
    {
        if (value is not { } date) return null;
        if (date < new DateOnly(1900, 1, 1) || date > DateOnly.FromDateTime(DateTime.Today)) throw new ApiError(422, "Дата рождения указана неверно");
        return date;
    }

    public static string? Kpp(string? value)
    {
        var kpp = value?.Trim();
        if (string.IsNullOrEmpty(kpp)) return null;
        if (!Regex.IsMatch(kpp, @"^\d{9}$")) throw new ApiError(422, "КПП — 9 цифр");
        return kpp;
    }

    /// <summary>Паспорт без пробелов в серии и номере. Нет ни одного поля — паспорта нет.</summary>
    public static PassportData? Passport(PassportData? value)
    {
        if (value is null) return null;
        string Digits(string? text) => Regex.Replace(text ?? "", @"\s+", "");
        var series = Digits(value.Series);
        var number = Digits(value.Number);
        var code = value.DepartmentCode?.Trim() ?? "";
        if (series == "" && number == "" && code == "" && value.IssuedAt is null) return null;
        if (!Regex.IsMatch(series, @"^\d{4}$")) throw new ApiError(422, "Серия паспорта — 4 цифры");
        if (!Regex.IsMatch(number, @"^\d{6}$")) throw new ApiError(422, "Номер паспорта — 6 цифр");
        if (code != "" && !Regex.IsMatch(code, @"^\d{3}-?\d{3}$")) throw new ApiError(422, "Код подразделения — 6 цифр, например 540-001");
        if (value.IssuedAt is { } issued && (issued < new DateOnly(1950, 1, 1) || issued > DateOnly.FromDateTime(DateTime.Today))) throw new ApiError(422, "Дата выдачи паспорта указана неверно");
        if (code.Length == 6) code = $"{code[..3]}-{code[3..]}";
        return new PassportData(series, number, value.IssuedAt, code == "" ? null : code);
    }

    public static string Protect(IDataProtectionProvider provider, PassportData passport) =>
        provider.CreateProtector(PassportPurpose).Protect(JsonSerializer.Serialize(passport));

    public static PassportData? Unprotect(IDataProtectionProvider provider, string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return null;
        try { return JsonSerializer.Deserialize<PassportData>(provider.CreateProtector(PassportPurpose).Unprotect(protectedText)); }
        catch (System.Security.Cryptography.CryptographicException) { throw new ApiError(500, "Паспорт не расшифровывается: ключи шифрования не те, что при записи"); }
    }

    public static string Describe(PassportData passport) =>
        $"{passport.Series![..2]} {passport.Series[2..]} № {passport.Number}" +
        (passport.IssuedAt is { } issued ? $", выдан {issued:dd.MM.yyyy}" : "") +
        (passport.DepartmentCode is { } code ? $", код подразделения {code}" : "");

    /// <summary>Строки услуг нового абонента и их описание для журнала.</summary>
    public static (List<ServiceLine> Lines, List<string> Notes) Services(string abonentId, RegistrationServices? request)
    {
        var heat = request?.Heat ?? "norm";
        var water = request?.Water ?? "none";
        if (heat is not ("norm" or "none")) throw new ApiError(422, "Теплоснабжение: по нормативу или не подключать");
        if (water is not ("meter" or "norm" or "none")) throw new ApiError(422, "Холодная вода: по счётчику, по нормативу или не подключать");
        var lines = new List<ServiceLine>();
        var notes = new List<string>();
        if (heat == "norm")
        {
            lines.Add(new ServiceLine { AbonentId = abonentId, ServiceId = HeatId, Position = lines.Count, Name = "Теплоснабжение", TariffName = "По нормативу", ChargeStatus = "Ждёт начисления" });
            notes.Add("Теплоснабжение по нормативу");
        }
        if (water == "meter")
        {
            var number = request?.MeterNumber?.Trim();
            if (string.IsNullOrEmpty(number) || number.Length > 100) throw new ApiError(422, "Укажите номер счётчика воды");
            var reading = request?.MeterReading ?? 0;
            if (reading < 0 || reading > 100_000_000 || reading != Math.Round(reading, 3)) throw new ApiError(422, "Начальное показание счётчика указано неверно");
            lines.Add(new ServiceLine { AbonentId = abonentId, ServiceId = WaterId, Position = lines.Count, Name = "Холодная вода", TariffName = "По счётчику", ChargeStatus = "Ждёт показаний", MeterNumber = number, MeterLastReading = reading });
            notes.Add($"Холодная вода по счётчику {number}, начальное показание {reading:0.###}");
        }
        else if (water == "norm")
        {
            lines.Add(new ServiceLine { AbonentId = abonentId, ServiceId = WaterId, Position = lines.Count, Name = "Холодная вода", TariffName = "По нормативу", ChargeStatus = "Ждёт начисления" });
            notes.Add("Холодная вода по нормативу");
        }
        return (lines, notes);
    }
}
