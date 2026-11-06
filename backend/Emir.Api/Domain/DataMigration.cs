using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Emir.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

public record MigrationProblem(string File, int Line, string Account, string Message);
public record MigrationReport(bool Applied, List<MigrationProblem> Errors, List<MigrationProblem> Warnings, Dictionary<string, long> Totals)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Перенос из файлов-шаблонов в пустую базу. Шаблоны описаны в docs/migration.md.
/// Пробный прогон выполняет всё то же самое и откатывает транзакцию, поэтому ловит и ошибки самой базы.
/// В отчёт не попадают ФИО и адреса: только файл, строка, лицевой счёт и причина.
/// </summary>
public static partial class DataMigration
{
    private const string Who = "Перенос данных";

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$")]
    private static partial Regex PeriodFormat();

    private sealed record Row(int Line, Dictionary<string, string> Cells)
    {
        public string this[string name] => Cells.GetValueOrDefault(name, "");
    }

    /// <summary>Разбор с кавычками: в названиях организаций бывает разделитель.</summary>
    private static List<string> SplitLine(string line, char separator)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var symbol = line[index];
            if (quoted)
            {
                if (symbol != '"') current.Append(symbol);
                else if (index + 1 < line.Length && line[index + 1] == '"') { current.Append('"'); index++; }
                else quoted = false;
            }
            else if (symbol == '"') quoted = true;
            else if (symbol == separator) { cells.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(symbol);
        }
        cells.Add(current.ToString().Trim());
        return cells;
    }

    private sealed class Context(string folder)
    {
        public List<MigrationProblem> Errors { get; } = [];
        public List<MigrationProblem> Warnings { get; } = [];

        public void Error(string file, int line, string account, string message) => Errors.Add(new MigrationProblem(file, line, account, message));
        public void Warn(string file, int line, string account, string message) => Warnings.Add(new MigrationProblem(file, line, account, message));

        public bool Exists(string file) => File.Exists(Path.Combine(folder, file));

        public List<Row> Read(string file, string[] required, bool optional = false)
        {
            var path = Path.Combine(folder, file);
            if (!File.Exists(path))
            {
                if (!optional) Error(file, 0, "", "Файла нет");
                return [];
            }
            var lines = ReadingImport.Decode(File.ReadAllBytes(path)).Split('\n');
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
            {
                Error(file, 1, "", "Нет строки с названиями колонок");
                return [];
            }
            var header = SplitLine(lines[0].TrimEnd('\r'), ';').Select(name => name.ToLowerInvariant()).ToList();
            foreach (var column in required.Where(column => !header.Contains(column))) Error(file, 1, "", $"Нет колонки {column}");
            if (required.Any(column => !header.Contains(column))) return [];

            var rows = new List<Row>();
            for (var index = 1; index < lines.Length; index++)
            {
                var text = lines[index].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(text)) continue;
                var cells = SplitLine(text, ';');
                if (cells.Count != header.Count)
                {
                    Error(file, index + 1, "", $"Колонок {cells.Count}, ожидалось {header.Count}");
                    continue;
                }
                rows.Add(new Row(index + 1, header.Zip(cells).ToDictionary(pair => pair.First, pair => pair.Second)));
            }
            return rows;
        }

        /// <summary>Рубли в копейки без округления: лишний знак в сумме нужно увидеть, а не потерять.</summary>
        public long? Money(string file, Row row, string column, string account, bool required = true)
        {
            var text = row[column].Replace(" ", "").Replace(" ", "").Replace(',', '.');
            if (text.Length == 0)
            {
                if (required) Error(file, row.Line, account, $"Пустая сумма в колонке {column}");
                return required ? null : 0;
            }
            if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rubles) || rubles * 100 != Math.Truncate(rubles * 100))
            {
                Error(file, row.Line, account, $"Неверная сумма в колонке {column}");
                return null;
            }
            return (long)(rubles * 100);
        }

        public decimal? Number(string file, Row row, string column, string account, bool required = false, int scale = 4, bool signed = false)
        {
            var text = row[column].Replace(" ", "").Replace(',', '.');
            if (text.Length == 0)
            {
                if (required) Error(file, row.Line, account, $"Пустое значение в колонке {column}");
                return null;
            }
            if (decimal.TryParse(text, signed ? NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign : NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value == Math.Round(value, scale)) return value;
            Error(file, row.Line, account, $"Неверное число в колонке {column}");
            return null;
        }

        public DateOnly? Date(string file, Row row, string column, string account)
        {
            var text = row[column];
            if (text.Length == 0) return null;
            if (DateOnly.TryParseExact(text, ["dd.MM.yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
            Error(file, row.Line, account, $"Неверная дата в колонке {column}");
            return null;
        }
    }

    private static string? NormalizeState(string text) => text.Trim().ToLowerInvariant() switch
    {
        "" or "работает" or "активен" or "активна" => TariffHistory.Working,
        "приостановлена" or "приостановлен" => "Приостановлена",
        "расторгнута" or "расторгнут" => "Расторгнута",
        _ => null,
    };

    /// <summary>Заменяет справочники старой системы из references.json. Деньги не трогает, поэтому можно в рабочей базе.</summary>
    public static async Task<int> ImportReferences(EmirDb db, string file)
    {
        using var stream = File.OpenRead(file);
        var tables = await System.Text.Json.JsonSerializer.DeserializeAsync<List<ReferenceFile>>(stream, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        // Русский текст хранится как есть, а не кодами \uXXXX: так база меньше в несколько раз
        var plain = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        db.ReferenceTables.RemoveRange(db.ReferenceTables);
        foreach (var table in tables)
            db.ReferenceTables.Add(new ReferenceTable
            {
                Name = table.Name, ColumnsJson = System.Text.Json.JsonSerializer.Serialize(table.Columns, plain), RowsJson = System.Text.Json.JsonSerializer.Serialize(table.Rows, plain),
                RowCount = table.Rows.Count, Sensitive = table.Sensitive, LoadedAtUtc = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        return tables.Count;
    }

    public record ReferenceFile(string Name, List<string> Columns, List<List<string>> Rows, bool Sensitive);

    public static async Task<MigrationReport> Run(EmirDb db, string folder, string startPeriod, bool apply)
    {
        var ctx = new Context(folder);
        if (!PeriodFormat().IsMatch(startPeriod ?? "")) ctx.Error("", 0, "", "Первый месяц укажите как ГГГГ-ММ");
        if (await db.Abonents.AnyAsync() || await db.AccountingMonths.AnyAsync() || await db.Tariffs.AnyAsync()) ctx.Error("", 0, "", "База не пуста. Перенос выполняется только в пустую базу");
        if (ctx.Errors.Count > 0) return new MigrationReport(false, ctx.Errors, ctx.Warnings, []);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var now = DateTime.UtcNow;
        db.Operation = new MoneyOperation("opening", Who, startPeriod, "Перенос данных");
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        // Тарифы и цены
        var tariffs = new Dictionary<string, Tariff>();
        foreach (var row in ctx.Read("tariffs.csv", ["id", "name", "scheme"]))
        {
            if (row["id"] == "" || row["name"] == "") { ctx.Error("tariffs.csv", row.Line, "", "Пустой код или название тарифа"); continue; }
            if (!ChargeEngine.Schemes.Contains(row["scheme"])) { ctx.Error("tariffs.csv", row.Line, "", $"Неизвестная схема {row["scheme"]}"); continue; }
            if (!tariffs.TryAdd(row["id"], new Tariff { Id = row["id"], Name = row["name"], Scheme = row["scheme"], Unit = row["unit"] == "" ? null : row["unit"], ServiceNorm = row["service_norm"] == "" ? null : row["service_norm"], TariffNorm = row["tariff_norm"] == "" ? null : row["tariff_norm"], PenaltyEnabled = row["penalty_enabled"] is not ("0" or "нет" or "false") }))
                ctx.Error("tariffs.csv", row.Line, "", $"Тариф {row["id"]} указан дважды");
        }
        db.Tariffs.AddRange(tariffs.Values);

        var priceKeys = new HashSet<(string, string)>();
        var pricedTariffs = new HashSet<string>();
        foreach (var row in ctx.Read("tariff_prices.csv", ["tariff_id", "from_period", "price"]))
        {
            if (!tariffs.ContainsKey(row["tariff_id"])) { ctx.Error("tariff_prices.csv", row.Line, "", $"Нет тарифа {row["tariff_id"]}"); continue; }
            if (!PeriodFormat().IsMatch(row["from_period"])) { ctx.Error("tariff_prices.csv", row.Line, "", "Месяц должен быть ГГГГ-ММ"); continue; }
            if (ctx.Number("tariff_prices.csv", row, "price", "", required: true) is not { } price) continue;
            if (!priceKeys.Add((row["tariff_id"], row["from_period"]))) { ctx.Error("tariff_prices.csv", row.Line, "", "Цена с этого месяца указана дважды"); continue; }
            pricedTariffs.Add(row["tariff_id"]);
            db.TariffPrices.Add(new TariffPrice { TariffId = row["tariff_id"], FromPeriod = row["from_period"], Price = price, Who = Who, AtUtc = now });
        }
        foreach (var tariff in tariffs.Keys.Where(id => !pricedTariffs.Contains(id))) ctx.Warn("tariffs.csv", 0, "", $"У тарифа {tariff} нет цены: начислить по нему нельзя");

        // Абоненты
        var abonents = new Dictionary<string, Abonent>();
        foreach (var row in ctx.Read("abonents.csv", ["account", "kind", "name", "street"]))
        {
            var account = row["account"];
            if (account == "") { ctx.Error("abonents.csv", row.Line, "", "Пустой лицевой счёт"); continue; }
            if (abonents.ContainsKey(account)) { ctx.Error("abonents.csv", row.Line, account, "Лицевой счёт указан дважды"); continue; }
            var kind = row["kind"].ToLowerInvariant() switch { "фл" or "person" => "person", "юл" or "org" => "org", _ => "" };
            if (kind == "") { ctx.Error("abonents.csv", row.Line, account, "Тип абонента: ФЛ или ЮЛ"); continue; }
            if (row["name"] == "") { ctx.Error("abonents.csv", row.Line, account, "Пустое имя"); continue; }
            if (row["street"] == "") ctx.Warn("abonents.csv", row.Line, account, "Пустой адрес");
            var area = ctx.Number("abonents.csv", row, "area", account) ?? 0;
            var residents = ctx.Number("abonents.csv", row, "residents", account) ?? 0;
            if (residents != Math.Truncate(residents)) ctx.Error("abonents.csv", row.Line, account, "Число жильцов должно быть целым");
            var houseHeatNorm = ctx.Number("abonents.csv", row, "house_heat_norm", account, scale: 6);
            var credit = ctx.Money("abonents.csv", row, "credit", account, required: false) ?? 0;
            if (credit < 0) ctx.Error("abonents.csv", row.Line, account, "Переплата не может быть отрицательной");
            var consent = row["consent"].ToLowerInvariant() switch { "да" or "1" or "true" => (bool?)true, "нет" or "0" or "false" => false, _ => null };

            var abonent = new Abonent
            {
                Id = Guid.NewGuid().ToString("N"), Kind = kind, Name = row["name"], ShortName = row["short_name"] == "" ? row["name"] : row["short_name"], Place = row["place"], Street = row["street"],
                Flat = row["flat"] == "" ? null : row["flat"], Account = account, Phone = row["phone"] == "" ? null : row["phone"], Inn = row["inn"] == "" ? null : row["inn"], HouseHeatNorm = houseHeatNorm, Area = area, Residents = (int)residents, Credit = credit,
                Note = row["note"] == "" ? null : row["note"], Consent = consent, ConsentAtUtc = consent is null ? null : now, OwnershipDocument = row["ownership_document"] == "" ? null : row["ownership_document"],
            };
            abonents[account] = abonent;
            db.Abonents.Add(abonent);
            db.AbonentFacts.Add(new AbonentFact { AbonentId = abonent.Id, FromPeriod = TariffHistory.Beginning, Area = area, Residents = (int)residents, Who = Who, AtUtc = now, Reason = "Перенос данных" });
        }

        // Общедомовые счётчики и их показания
        var houseMeterIds = new HashSet<string>();
        foreach (var row in ctx.Read("house_meters.csv", ["id", "street"], optional: true))
        {
            if (!houseMeterIds.Add(row["id"])) { ctx.Error("house_meters.csv", row.Line, "", "Счётчик указан дважды"); continue; }
            var factor = ctx.Number("house_meters.csv", row, "unit_factor", "", scale: 8) ?? 1;
            db.HouseMeters.Add(new HouseMeter { Id = row["id"], Place = row["place"], Street = row["street"], Number = row["number"], Unit = row["unit"] == "" ? "Гкал" : row["unit"], UnitFactor = factor });
        }
        foreach (var row in ctx.Read("house_meter_readings.csv", ["meter_id", "period", "value"], optional: true))
        {
            if (!houseMeterIds.Contains(row["meter_id"])) { ctx.Error("house_meter_readings.csv", row.Line, "", "Нет такого общедомового счётчика"); continue; }
            if (!PeriodFormat().IsMatch(row["period"])) { ctx.Error("house_meter_readings.csv", row.Line, "", "Месяц должен быть ГГГГ-ММ"); continue; }
            if (ctx.Number("house_meter_readings.csv", row, "value", "", required: true) is not { } meterValue) continue;
            db.HouseMeterReadings.Add(new HouseMeterReading { HouseMeterId = row["meter_id"], Period = row["period"], Value = meterValue, Who = Who, AtUtc = now });
        }

        // Услуги: остатки, тариф, счётчик
        var lines = new Dictionary<(string Account, string Service), ServiceLine>();
        var positions = new Dictionary<string, int>();
        foreach (var row in ctx.Read("services.csv", ["account", "service_id", "name", "debt"]))
        {
            var account = row["account"];
            if (!abonents.TryGetValue(account, out var abonent)) { ctx.Error("services.csv", row.Line, account, "Нет такого лицевого счёта"); continue; }
            var serviceId = row["service_id"];
            if (serviceId == "" || row["name"] == "") { ctx.Error("services.csv", row.Line, account, "Пустой код или название услуги"); continue; }
            if (lines.ContainsKey((account, serviceId))) { ctx.Error("services.csv", row.Line, account, $"Услуга {serviceId} указана дважды"); continue; }
            if (NormalizeState(row["state"]) is not { } state) { ctx.Error("services.csv", row.Line, account, $"Неизвестное состояние услуги: {row["state"]}"); continue; }
            var tariffId = row["tariff_id"] == "" ? null : row["tariff_id"];
            if (tariffId is not null && !tariffs.ContainsKey(tariffId)) { ctx.Error("services.csv", row.Line, account, $"Нет тарифа {tariffId}"); continue; }
            if (ctx.Money("services.csv", row, "debt", account) is not { } debt) continue;

            // В старой системе баланс услуги может быть с плюсом. У нас это переплата абонента
            if (debt < 0)
            {
                ctx.Warn("services.csv", row.Line, account, "Отрицательный долг перенесён в переплату абонента");
                abonent.Credit -= debt;
                debt = 0;
            }

            var meter = row["meter_number"] == "" ? null : row["meter_number"];
            var last = ctx.Number("services.csv", row, "meter_last_reading", account);
            if (meter is not null && last is null) { ctx.Error("services.csv", row.Line, account, "У счётчика нет последнего показания"); continue; }
            if (tariffId is not null && tariffs[tariffId].Scheme == "MeterApartment" && meter is null && state == TariffHistory.Working)
                ctx.Warn("services.csv", row.Line, account, "Тариф по счётчику, а счётчика нет");

            var houseMeterId = row["house_meter_id"] == "" ? null : row["house_meter_id"];
            if (houseMeterId is not null && !houseMeterIds.Contains(houseMeterId)) ctx.Error("services.csv", row.Line, account, "Нет такого общедомового счётчика");
            var position = positions.GetValueOrDefault(account);
            positions[account] = position + 1;
            var line = new ServiceLine
            {
                HouseMeterId = houseMeterId, AbonentId = abonent.Id, ServiceId = serviceId, Position = position, Name = row["name"], State = state, TariffId = tariffId, TariffName = tariffId is null ? (serviceId == "penalty" ? "По настройке пени" : "Не выбран") : tariffs[tariffId].Name,
                ChargeStatus = "Ждёт начисления", Debt = debt, ContractNorm = row["contract_norm"] == "" ? null : row["contract_norm"], MeterNumber = meter, MeterLastReading = meter is null ? null : last,
                MeterVerifiedUntil = row["meter_verified_until"] == "" ? null : row["meter_verified_until"], MeterAverage = ctx.Number("services.csv", row, "meter_average", account) ?? 5,
            };
            lines[(account, serviceId)] = line;
            db.ServiceLines.Add(line);
            if (serviceId != "penalty") db.ServiceTerms.Add(new ServiceTerm { AbonentId = abonent.Id, ServiceId = serviceId, FromPeriod = TariffHistory.Beginning, TariffId = tariffId, State = state, Who = Who, AtUtc = now, Reason = "Перенос данных" });
        }
        foreach (var abonent in abonents.Where(pair => !positions.ContainsKey(pair.Key))) ctx.Warn("abonents.csv", 0, abonent.Key, "У абонента нет услуг");

        // История начислений нужна пени: без неё просрочку считать не от чего
        var chargedByLine = new Dictionary<(string, string), long>();
        var chargeKeys = new HashSet<(string, string, string)>();
        foreach (var row in ctx.Read("charges.csv", ["account", "service_id", "period", "amount"], optional: true))
        {
            var account = row["account"];
            if (!lines.TryGetValue((account, row["service_id"]), out var line)) { ctx.Error("charges.csv", row.Line, account, $"Нет услуги {row["service_id"]}"); continue; }
            if (!PeriodFormat().IsMatch(row["period"])) { ctx.Error("charges.csv", row.Line, account, "Месяц должен быть ГГГГ-ММ"); continue; }
            if (string.CompareOrdinal(row["period"], startPeriod) >= 0) { ctx.Error("charges.csv", row.Line, account, "Начисление за первый месяц или позже: история должна быть раньше него"); continue; }
            if (!chargeKeys.Add((account, row["service_id"], row["period"]))) { ctx.Error("charges.csv", row.Line, account, "Начисление за этот месяц указано дважды"); continue; }
            if (ctx.Money("charges.csv", row, "amount", account) is not { } amount) continue;
            db.ChargeEntries.Add(new ChargeEntry { AbonentId = line.AbonentId, ServiceId = line.ServiceId, Period = row["period"], Scheme = "Opening", Amount = amount, Volume = ctx.Number("charges.csv", row, "volume", account, signed: true) ?? 0, Price = ctx.Number("charges.csv", row, "price", account), AppliedToDebt = true, AtUtc = now });
            chargedByLine[(account, row["service_id"])] = chargedByLine.GetValueOrDefault((account, row["service_id"])) + amount;
        }
        var withoutHistory = lines.Where(pair => pair.Key.Service != "penalty" && pair.Value.Debt > 0 && chargedByLine.GetValueOrDefault(pair.Key) < pair.Value.Debt).ToList();
        foreach (var pair in withoutHistory) ctx.Warn("charges.csv", 0, pair.Key.Account, $"Долг услуги {pair.Key.Service} больше перенесённых начислений: пени на остаток считаться не будут");

        // Платежи для истории: на остатки не влияют, они уже учтены в долге
        var paymentCount = 0;
        foreach (var row in ctx.Read("payments.csv", ["account", "date", "source", "amount"], optional: true))
        {
            var account = row["account"];
            if (!abonents.TryGetValue(account, out var abonent)) { ctx.Error("payments.csv", row.Line, account, "Нет такого лицевого счёта"); continue; }
            var date = ctx.Date("payments.csv", row, "date", account);
            if (date is null) { if (row["date"] == "") ctx.Error("payments.csv", row.Line, account, "Пустая дата"); continue; }
            if (ctx.Money("payments.csv", row, "amount", account) is not { } amount) continue;
            db.Payments.Add(new Payment { Id = Guid.NewGuid().ToString("N"), AbonentId = abonent.Id, AtUtc = date.Value.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc), Source = row["source"], Cashier = row["cashier"] == "" ? Who : row["cashier"], Amount = amount, Status = "Перенесён" });
            paymentCount++;
        }

        foreach (var row in ctx.Read("residents.csv", ["account", "name"], optional: true))
        {
            if (!abonents.TryGetValue(row["account"], out var abonent)) { ctx.Error("residents.csv", row.Line, row["account"], "Нет такого лицевого счёта"); continue; }
            if (row["name"] == "") { ctx.Error("residents.csv", row.Line, row["account"], "Пустое ФИО"); continue; }
            db.Residents.Add(new Resident { AbonentId = abonent.Id, Name = row["name"], FromDate = ctx.Date("residents.csv", row, "from_date", row["account"]), ToDate = ctx.Date("residents.csv", row, "to_date", row["account"]) });
        }

        // Льготники: категории создаются по названиям из файла
        var beneficiaryUuids = new HashSet<string>();
        var benefitRows = ctx.Read("beneficiaries.csv", ["account", "full_name", "category"], optional: true);
        var categories = benefitRows.Select(row => row["category"]).Where(name => name != "").Distinct().ToDictionary(name => name, name => new BenefitCategory { Name = name });
        db.BenefitCategories.AddRange(categories.Values);
        if (categories.Count > 0)
        {
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync();
        }
        foreach (var row in benefitRows)
        {
            if (row["uuid"] != "" && !beneficiaryUuids.Add(row["uuid"])) { ctx.Error("beneficiaries.csv", row.Line, row["account"], "UUID льготника указан дважды"); continue; }
            if (!abonents.TryGetValue(row["account"], out var abonent)) { ctx.Error("beneficiaries.csv", row.Line, row["account"], "Нет такого лицевого счёта"); continue; }
            if (row["full_name"] == "" || row["category"] == "") { ctx.Error("beneficiaries.csv", row.Line, row["account"], "Пустое ФИО или категория"); continue; }
            var beneficiary = new Beneficiary { AbonentId = abonent.Id, CategoryId = categories[row["category"]].Id, FullName = row["full_name"], FromDate = ctx.Date("beneficiaries.csv", row, "from_date", row["account"]), ToDate = ctx.Date("beneficiaries.csv", row, "to_date", row["account"]), Who = Who, AtUtc = now };
            if (row["uuid"] != "") beneficiary.Uuid = row["uuid"];
            db.Beneficiaries.Add(beneficiary);
        }

        db.AccountingMonths.Add(new AccountingMonth { Period = startPeriod! });
        db.PeriodSteps.AddRange(Periods.NewSteps());

        var totals = new Dictionary<string, long>
        {
            ["abonents"] = abonents.Count, ["services"] = lines.Count, ["total_debt"] = lines.Values.Sum(line => line.Debt), ["total_credit"] = abonents.Values.Sum(item => item.Credit),
            ["tariffs"] = tariffs.Count, ["charges"] = chargeKeys.Count, ["payments"] = paymentCount,
        };

        // Главная проверка переноса: итоги должны совпасть с тем, что показывает старая система
        if (ctx.Exists("control.csv"))
        {
            foreach (var row in ctx.Read("control.csv", ["metric", "value"]))
            {
                if (!totals.TryGetValue(row["metric"], out var actual)) { ctx.Error("control.csv", row.Line, "", $"Неизвестный показатель {row["metric"]}"); continue; }
                var money = row["metric"] is "total_debt" or "total_credit";
                long? expected = money ? ctx.Money("control.csv", row, "value", "") : long.TryParse(row["value"], out var count) ? count : null;
                if (expected is null) { if (!money) ctx.Error("control.csv", row.Line, "", "Неверное значение"); continue; }
                if (expected != actual) ctx.Error("control.csv", row.Line, "", $"{row["metric"]}: в старой системе {(money ? expected / 100m : expected)}, в файлах {(money ? actual / 100m : actual)}");
            }
        }
        else
        {
            ctx.Warn("control.csv", 0, "", "Файла итогов нет: совпадение со старой системой не проверено");
        }

        if (ctx.Errors.Count > 0) return new MigrationReport(false, ctx.Errors, ctx.Warnings, totals);

        db.ChangeTracker.DetectChanges();
        db.Journal.Add(new JournalRecord { AtUtc = now, Action = "Перенос данных", Who = Who, Details = $"Абонентов {abonents.Count}, услуг {lines.Count}, долг {totals["total_debt"] / 100m:N2} ₽, переплата {totals["total_credit"] / 100m:N2} ₽, первый месяц {startPeriod}" });
        await db.SaveChangesAsync();

        // Журнал денег обязан сойтись с остатками ещё до фиксации
        var ledgerDebt = await db.LedgerEntries.SumAsync(entry => entry.DebtDelta);
        var ledgerCredit = await db.LedgerEntries.SumAsync(entry => entry.CreditDelta);
        if (ledgerDebt != totals["total_debt"] || ledgerCredit != totals["total_credit"]) ctx.Error("", 0, "", "Журнал денег не сошёлся с остатками");
        if (ctx.Errors.Count > 0) return new MigrationReport(false, ctx.Errors, ctx.Warnings, totals);

        if (apply) await transaction.CommitAsync();
        return new MigrationReport(apply, ctx.Errors, ctx.Warnings, totals);
    }

    public static string Format(MigrationReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(report.Applied ? "ДАННЫЕ ЗАГРУЖЕНЫ" : report.Ok ? "ПРОБНЫЙ ПРОГОН: ошибок нет, в базу ничего не записано" : "ЕСТЬ ОШИБКИ: в базу ничего не записано");
        foreach (var (name, value) in report.Totals) text.AppendLine($"  {name}: {(name is "total_debt" or "total_credit" ? (value / 100m).ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) : value.ToString())}");
        text.AppendLine($"Ошибок: {report.Errors.Count}, предупреждений: {report.Warnings.Count}");
        foreach (var item in report.Errors) text.AppendLine($"ОШИБКА {item.File}:{item.Line} ЛС {item.Account}: {item.Message}");
        foreach (var item in report.Warnings) text.AppendLine($"ВНИМАНИЕ {item.File}:{item.Line} ЛС {item.Account}: {item.Message}");
        return text.ToString();
    }
}
