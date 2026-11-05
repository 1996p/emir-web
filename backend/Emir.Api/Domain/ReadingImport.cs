using System.Globalization;
using System.Text;
using Emir.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

/// <summary>Номера колонок считаются с единицы. Счётчик и дата необязательны: 0 значит, что колонки нет.</summary>
public record ReadingColumns(int Account, int Meter, int Value, int Date, char Separator);

/// <summary>State: ok, high, same, not-found, no-meter, many-meters, unknown-meter, lower, bad-value, bad-date, other-month, duplicate.</summary>
public record ReadingImportLine(int Line, string Account, string Meter, string Value, decimal? Previous, decimal? Usage, long Charge, string State);
public record ReadingImportResult(bool Applied, string Title, int Applicable, int High, long Charge, int Saved, List<ReadingImportLine> Lines);

/// <summary>
/// Загрузка показаний из файла. Сначала проверка, запись только после подтверждения,
/// и правила те же, что при ручном вводе, потому что проводит их тот же Billing.ApplyReading.
/// </summary>
public static class ReadingImport
{
    public const int MaxLines = 50_000;

    /// <summary>Расход больше стольких средних считается подозрительным, как на экране ручного ввода.</summary>
    public const decimal HighFactor = 3;

    static ReadingImport() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Выгрузки из старых программ часто в Windows-1251, а номера счётчиков содержат кириллицу.</summary>
    public static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static string Cell(string[] parts, int column) => column >= 1 && column <= parts.Length ? parts[column - 1].Trim().Trim('"').Trim() : "";

    private static decimal? ParseValue(string text)
    {
        var normalized = text.Replace(" ", "").Replace(' ', ' ').Replace(" ", "").Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static DateOnly? ParseDate(string text)
    {
        // Время после даты встречается в выгрузках и на месяц показания не влияет
        var date = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return DateOnly.TryParseExact(date, ["dd.MM.yyyy", "yyyy-MM-dd", "dd.MM.yy", "d.M.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    public static async Task<ReadingImportResult> Run(EmirDb db, string content, ReadingColumns columns, bool apply, bool includeHigh, int? expected, string who, string fileName)
    {
        if (columns.Account < 1 || columns.Value < 1 || columns.Meter < 0 || columns.Date < 0) throw new ApiError(422, "Укажите номера колонок лицевого счёта и показания");
        var used = new[] { columns.Account, columns.Value, columns.Meter, columns.Date }.Where(column => column > 0).ToList();
        if (used.Distinct().Count() != used.Count) throw new ApiError(422, "Одна колонка выбрана для двух полей");

        var month = await Periods.Work(db);
        if (apply) await ChargingService.EnsureOpen(db);
        var firstDay = PenaltyEngine.FirstDay(month.Period);
        var lastDay = firstDay.AddMonths(1).AddDays(-1);

        var abonents = await db.Abonents.Include(item => item.Services).ToDictionaryAsync(item => item.Account);
        var readings = await db.PeriodReadings.Where(item => item.Period == month.Period).ToListAsync();
        var terms = await db.ServiceTerms.ToListAsync();
        var prices = await db.TariffPrices.ToListAsync();
        var meterTariffs = await db.Tariffs.Where(item => item.Scheme == "MeterApartment").Select(item => item.Id).ToListAsync();

        decimal? PriceOf(ServiceLine line)
        {
            var term = TariffHistory.TermOn(terms, line.AbonentId, line.ServiceId, month.Period);
            if (term is not { State: TariffHistory.Working, TariffId: not null } || !meterTariffs.Contains(term.TariffId)) return null;
            return TariffHistory.PriceOn(prices, term.TariffId, month.Period)?.Price;
        }

        var rows = content.Split('\n');
        if (rows.Length > MaxLines) throw new ApiError(422, $"В файле больше {MaxLines} строк. Разбейте его на части");

        var lines = new List<ReadingImportLine>();
        var targets = new List<(Abonent Abonent, ServiceLine Line, decimal Value, string State)>();
        var seen = new HashSet<(string, string)>();

        for (var index = 0; index < rows.Length; index++)
        {
            var raw = rows[index].Trim();
            if (raw.Length == 0) continue;
            var parts = raw.Split(columns.Separator);
            var account = Cell(parts, columns.Account);
            var meter = Cell(parts, columns.Meter);
            var text = Cell(parts, columns.Value);
            var parsed = ParseValue(text);

            // Первая строка с нечисловым показанием считается заголовком
            if (parsed is null && lines.Count == 0 && index == 0) continue;

            void Add(string state, decimal? previous = null, decimal? usage = null, long charge = 0) => lines.Add(new ReadingImportLine(index + 1, account, meter, text, previous, usage, charge, state));

            if (parsed is not { } value || value < 0) { Add("bad-value"); continue; }
            if (columns.Date > 0)
            {
                if (ParseDate(Cell(parts, columns.Date)) is not { } date) { Add("bad-date"); continue; }
                if (date < firstDay || date > lastDay) { Add("other-month"); continue; }
            }
            if (!abonents.TryGetValue(account, out var abonent)) { Add("not-found"); continue; }

            var meters = abonent.Services.Where(line => line.MeterNumber is not null).ToList();
            if (meters.Count == 0) { Add("no-meter"); continue; }
            ServiceLine? target;
            if (meter != "")
            {
                target = meters.FirstOrDefault(line => string.Equals(line.MeterNumber, meter, StringComparison.OrdinalIgnoreCase));
                if (target is null) { Add("unknown-meter"); continue; }
            }
            else
            {
                if (meters.Count > 1) { Add("many-meters"); continue; }
                target = meters[0];
            }
            if (!seen.Add((abonent.Id, target.ServiceId))) { Add("duplicate"); continue; }

            var existing = readings.FirstOrDefault(item => item.AbonentId == abonent.Id && item.ServiceId == target.ServiceId);
            var start = existing?.StartValue ?? target.MeterLastReading ?? 0;
            var usage = value - start;
            if (usage < 0) { Add("lower", start, usage); continue; }
            if (existing?.Value == value) { Add("same", start, usage); continue; }

            var charge = Billing.ChargeFor(PriceOf(target), start, value) - (existing?.AppliedAmount ?? 0);
            var state = usage > target.MeterAverage * HighFactor ? "high" : "ok";
            Add(state, start, usage, charge);
            targets.Add((abonent, target, value, state));
        }
        if (lines.Count == 0) throw new ApiError(422, "В файле нет строк с показаниями. Проверьте разделитель и номера колонок");

        var chosen = targets.Where(item => item.State == "ok" || includeHigh).ToList();
        var chosenLines = lines.Where(line => line.State == "ok" || (includeHigh && line.State == "high")).ToList();
        var saved = 0;
        if (apply)
        {
            if (expected != chosen.Count) throw new ApiError(409, "Данные изменились после проверки. Проверьте файл ещё раз");
            if (chosen.Count == 0) throw new ApiError(422, "В файле нет показаний для записи");
            db.Operation = new MoneyOperation("reading", who, month.Period, fileName);
            foreach (var item in chosen)
            {
                Billing.ApplyReading(db, item.Abonent, item.Line, item.Value);
                db.Journal.Add(new JournalRecord { AbonentId = item.Abonent.Id, AtUtc = DateTime.UtcNow, Action = "Показание счётчика", Who = who, Details = $"{item.Line.MeterNumber}: {item.Value:0.####}, файл {fileName}" });
                saved++;
            }
        }
        return new ReadingImportResult(apply, Periods.Title(month.Period), chosen.Count, targets.Count(item => item.State == "high"), chosenLines.Sum(line => line.Charge), saved, lines);
    }
}
