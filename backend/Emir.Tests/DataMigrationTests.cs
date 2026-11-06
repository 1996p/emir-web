using System.Text;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Emir.Tests;

/// <summary>Отдельная пустая база на каждый тест: начальные данные здесь не нужны и мешали бы.</summary>
public sealed class DataMigrationTests : IDisposable
{
    private static readonly string Server = Environment.GetEnvironmentVariable("EMIR_TEST_POSTGRES") ?? "Host=localhost;Port=5433;Username=emir;Password=emir-dev-local";
    private readonly string _connection = $"{Server};Database=emir_test_{Guid.NewGuid():N};Maximum Pool Size=10";
    private readonly string _folder = Directory.CreateTempSubdirectory("emir-migration").FullName;

    public DataMigrationTests()
    {
        using var db = Db();
        db.Database.Migrate();
        Write("tariffs.csv", "id;name;scheme;unit\nheat;Тепло по площади;HeatHouseNorm;м²\nwater;Вода по счётчику;MeterApartment;м³\n");
        Write("tariff_prices.csv", "tariff_id;from_period;price\nheat;2000-01;35,43\nheat;2026-07;40\nwater;2000-01;52\n");
        Write("abonents.csv", "account;kind;name;short_name;place;street;flat;phone;inn;area;residents;credit;note;consent;ownership_document\n100;ФЛ;Иванов Иван;Иванов И.;с. Тест;ул. Первая, 1;2;+7;;50,5;3;0,00;Заметка;да;Выписка\n200;ЮЛ;\"ООО \"\"Точка; Запятая\"\"\";;с. Тест;ул. Вторая, 2;;;5400000000;100;0;15,00;;;\n");
        Write("services.csv", "account;service_id;name;state;tariff_id;debt;contract_norm;meter_number;meter_last_reading;meter_verified_until;meter_average\n100;heat;Теплоснабжение;Работает;heat;1 000,50;;;;;\n100;water;Холодная вода;Приостановлен;water;200,00;;СЧ-1;412,5;05.2029;4\n100;penalty;Пени;;;10,00;;;;;\n200;heat;Теплоснабжение;;heat;0;;;;;\n");
        Write("charges.csv", "account;service_id;period;amount;volume;price\n100;heat;2026-09;1000,50;50,5;40\n100;water;2026-09;200;;\n");
    }

    public void Dispose()
    {
        using var db = Db();
        NpgsqlConnection.ClearAllPools();
        db.Database.EnsureDeleted();
        Directory.Delete(_folder, true);
    }

    private EmirDb Db() => new(new DbContextOptionsBuilder<EmirDb>().UseNpgsql(_connection).Options);

    private void Write(string file, string text) => File.WriteAllText(Path.Combine(_folder, file), text, new UTF8Encoding(true));

    private async Task<MigrationReport> Run(bool apply = true, string period = "2026-11")
    {
        await using var db = Db();
        return await DataMigration.Run(db, _folder, period, apply);
    }

    [Fact]
    public async Task Apply_LoadsEverything_AndLedgerMatchesBalances()
    {
        var report = await Run();
        Assert.True(report.Ok, DataMigration.Format(report));
        Assert.True(report.Applied);
        Assert.Equal(100050 + 20000 + 1000, report.Totals["total_debt"]);
        Assert.Equal(1500, report.Totals["total_credit"]);

        await using var db = Db();
        var person = await db.Abonents.Include(item => item.Services).FirstAsync(item => item.Account == "100");
        Assert.Equal(50.5m, person.Area);
        Assert.Equal(3, person.Residents);
        Assert.True(person.Consent);
        var org = await db.Abonents.FirstAsync(item => item.Account == "200");
        Assert.Equal("ООО \"Точка; Запятая\"", org.Name);
        Assert.Equal(org.Name, org.ShortName);
        var water = person.Services.First(line => line.ServiceId == "water");
        Assert.Equal("Приостановлена", water.State);
        Assert.Equal(412.5m, water.MeterLastReading);
        Assert.Equal(4m, water.MeterAverage);

        Assert.Equal(report.Totals["total_debt"], await db.LedgerEntries.SumAsync(item => item.DebtDelta));
        Assert.Equal(report.Totals["total_credit"], await db.LedgerEntries.SumAsync(item => item.CreditDelta));
        Assert.All(await db.LedgerEntries.ToListAsync(), item => Assert.Equal("opening", item.Kind));
        Assert.Equal("2026-11", (await db.AccountingMonths.SingleAsync()).Period);
        Assert.Equal(2, await db.AbonentFacts.CountAsync());
        Assert.Equal(3, await db.ServiceTerms.CountAsync());
        Assert.Equal(6, await db.PeriodSteps.CountAsync());
    }

    [Fact]
    public async Task DryRun_ValidatesFully_ButWritesNothing()
    {
        var report = await Run(apply: false);
        Assert.True(report.Ok, DataMigration.Format(report));
        Assert.False(report.Applied);
        await using var db = Db();
        Assert.False(await db.Abonents.AnyAsync());
        Assert.False(await db.LedgerEntries.AnyAsync());
        Assert.False(await db.Tariffs.AnyAsync());
    }

    [Fact]
    public async Task AfterMigration_ChargeEngineUsesPriceHistory_AndChargesWork()
    {
        Assert.True((await Run()).Ok);
        await using var db = Db();
        var abonents = await db.Abonents.Include(item => item.Services).Where(item => item.Account == "100").ToListAsync();
        var plan = await ChargingService.Plan(db, abonents, "2026-11");
        // 50,5 м² × 40 ₽, приостановленная вода и пени в расчёт не идут
        var heat = Assert.Single(plan);
        Assert.Equal(202000, heat.Result.Amount);
        var old = await ChargingService.Plan(db, abonents, "2026-06");
        Assert.Equal(178922, Assert.Single(old).Result.Amount);
    }

    [Fact]
    public async Task NormHouseTariff_UsesHouseHeatNormFromFile()
    {
        Write("tariffs.csv", "id;name;scheme;unit\nheat;Тепло по норме дома;NormHouse;Гкал\n");
        Write("tariff_prices.csv", "tariff_id;from_period;price\nheat;2000-01;3128,09\n");
        Write("abonents.csv", "account;kind;name;street;area;house_heat_norm\n100;ФЛ;Иванов;ул. 1;50;0,01725\n");
        Write("services.csv", "account;service_id;name;debt;tariff_id\n100;heat;Тепло;0;heat\n");
        Write("charges.csv", "account;service_id;period;amount\n");
        var report = await Run();
        Assert.True(report.Ok, DataMigration.Format(report));
        await using var db = Db();
        var abonents = await db.Abonents.Include(item => item.Services).ToListAsync();
        var heat = Assert.Single(await ChargingService.Plan(db, abonents, "2026-11"));
        // 50 м² × 0,01725 Гкал × 3128,09 ₽ = 2697,98 ₽, норма хранится без округления
        Assert.Equal(269798, heat.Result.Amount);
    }

    [Fact]
    public async Task MeterHouseTariff_SplitsUsageByArea_WithUnitConversion()
    {
        Write("tariffs.csv", "id;name;scheme;unit\nheat;Общедомовой счётчик;MeterHouse;Гкал\n");
        Write("tariff_prices.csv", "tariff_id;from_period;price\nheat;2000-01;1000\n");
        Write("abonents.csv", "account;kind;name;street;area\n100;ФЛ;Первый;ул. 1;30\n200;ФЛ;Второй;ул. 1;70\n");
        Write("services.csv", "account;service_id;name;debt;tariff_id;house_meter_id\n100;heat;Тепло;0;heat;m1\n200;heat;Тепло;0;heat;m1\n");
        Write("charges.csv", "account;service_id;period;amount\n");
        Write("house_meters.csv", "id;place;street;number;unit;unit_factor\nm1;с. Тест;ул. 1;77;ГДж;0,25\n");
        Write("house_meter_readings.csv", "meter_id;period;value\nm1;2026-10;100\nm1;2026-11;140\n");
        var report = await Run();
        Assert.True(report.Ok, DataMigration.Format(report));
        await using var db = Db();
        var abonents = await db.Abonents.Include(item => item.Services).OrderBy(item => item.Account).ToListAsync();
        var plan = await ChargingService.Plan(db, abonents, "2026-11");
        // Расход 40 ГДж × 0,25 = 10 Гкал: 30% и 70% при цене 1000 ₽
        Assert.Equal(300000, plan[0].Result.Amount);
        Assert.Equal(700000, plan[1].Result.Amount);
        Assert.Contains("Нет данных", (await ChargingService.Plan(db, abonents, "2026-12"))[0].Result.Error);
    }

    [Fact]
    public async Task NotEmptyBase_IsRefused()
    {
        Assert.True((await Run()).Ok);
        var again = await Run();
        Assert.False(again.Ok);
        Assert.Contains(again.Errors, item => item.Message.Contains("не пуста"));
    }

    [Fact]
    public async Task AnyError_WritesNothing_AndAllErrorsAreCollected()
    {
        Write("abonents.csv", "account;kind;name;street;area\n100;ФЛ;Иванов;ул. 1;плохо\n100;ФЛ;Дубль;ул. 2;1\n300;ХЗ;Третий;ул. 3;1\n400;ФЛ;;ул. 4;1\n");
        Write("services.csv", "account;service_id;name;debt;state;tariff_id\n999;heat;Тепло;1;;\n100;heat;Тепло;1,234;;\n100;x;Икс;1;странное;\n100;y;Игрек;1;;нет_такого\n");
        var report = await Run();
        Assert.False(report.Ok);
        var messages = report.Errors.Select(item => item.Message).ToList();
        foreach (var expected in new[] { "Неверное число", "указан дважды", "ФЛ или ЮЛ", "Пустое имя", "Нет такого лицевого счёта", "Неверная сумма", "Неизвестное состояние", "Нет тарифа" })
            Assert.Contains(messages, message => message.Contains(expected));
        Assert.Contains(report.Errors, item => item is { File: "abonents.csv", Line: 3 });
        await using var db = Db();
        Assert.False(await db.Abonents.AnyAsync());
    }

    [Fact]
    public async Task MissingRequiredFileOrColumn_IsError()
    {
        File.Delete(Path.Combine(_folder, "tariffs.csv"));
        Write("abonents.csv", "account;kind\n100;ФЛ\n");
        var report = await Run();
        Assert.Contains(report.Errors, item => item is { File: "tariffs.csv", Message: "Файла нет" });
        Assert.Contains(report.Errors, item => item.File == "abonents.csv" && item.Message.Contains("Нет колонки name"));
    }

    [Fact]
    public async Task ControlTotals_Match_Pass()
    {
        Write("control.csv", "metric;value\nabonents;2\nservices;4\ntotal_debt;1 210,50\ntotal_credit;15,00\n");
        Assert.True((await Run()).Ok);
    }

    [Fact]
    public async Task ControlTotals_Differ_BlockLoad()
    {
        Write("control.csv", "metric;value\ntotal_debt;1 210,51\n");
        var report = await Run();
        Assert.False(report.Ok);
        Assert.Contains(report.Errors, item => item.Message.Contains("total_debt"));
        await using var db = Db();
        Assert.False(await db.Abonents.AnyAsync());
    }

    [Fact]
    public async Task NoControlFile_IsWarned()
    {
        var report = await Run();
        Assert.Contains(report.Warnings, item => item.File == "control.csv");
    }

    [Fact]
    public async Task NegativeDebt_BecomesCredit_NetBalanceKept()
    {
        Write("services.csv", "account;service_id;name;debt\n100;heat;Тепло;-50,00\n");
        Write("charges.csv", "account;service_id;period;amount\n");
        var report = await Run();
        Assert.True(report.Ok, DataMigration.Format(report));
        Assert.Contains(report.Warnings, item => item.Message.Contains("переплату"));
        Assert.Equal(0, report.Totals["total_debt"]);
        Assert.Equal(1500 + 5000, report.Totals["total_credit"]);
    }

    [Fact]
    public async Task DebtWithoutChargeHistory_IsWarned_BecausePenaltyDependsOnIt()
    {
        Write("charges.csv", "account;service_id;period;amount\n100;heat;2026-09;100\n");
        var report = await Run();
        Assert.True(report.Ok);
        Assert.Contains(report.Warnings, item => item.Account == "100" && item.Message.Contains("пени"));
    }

    [Fact]
    public async Task ChargeInStartMonth_IsRefused()
    {
        Write("charges.csv", "account;service_id;period;amount\n100;heat;2026-11;100\n");
        Assert.Contains((await Run()).Errors, item => item.Message.Contains("раньше"));
    }

    [Fact]
    public async Task OptionalFiles_LoadPaymentsResidentsBeneficiaries_WithoutChangingBalances()
    {
        Write("payments.csv", "account;date;source;cashier;amount\n100;15.10.2026;Касса;Кассир;500,00\n");
        Write("residents.csv", "account;name;from_date;to_date\n100;Жилец Один;01.01.2020;\n");
        Write("beneficiaries.csv", "account;full_name;category;from_date;to_date;uuid\n100;Жилец Один;Ветеран;01.01.2020;;abc-1\n200;Другой;Ветеран;;;\n");
        var report = await Run();
        Assert.True(report.Ok, DataMigration.Format(report));
        Assert.Equal(100050 + 20000 + 1000, report.Totals["total_debt"]);

        await using var db = Db();
        var payment = await db.Payments.SingleAsync();
        Assert.Equal(50000, payment.Amount);
        Assert.Equal("Перенесён", payment.Status);
        Assert.Equal(1, await db.BenefitCategories.CountAsync());
        Assert.Equal(2, await db.Beneficiaries.CountAsync());
        Assert.Contains(await db.Beneficiaries.ToListAsync(), item => item.Uuid == "abc-1");
        Assert.Equal(new DateOnly(2020, 1, 1), (await db.Residents.SingleAsync()).FromDate);
    }

    [Fact]
    public async Task DuplicateBeneficiaryUuid_IsAnError_NotACrash()
    {
        Write("beneficiaries.csv", "account;full_name;category;from_date;to_date;uuid\n100;Один;Ветеран;;;abc-1\n200;Другой;Ветеран;;;abc-1\n");
        var report = await Run();
        Assert.Contains(report.Errors, item => item is { File: "beneficiaries.csv", Line: 3 } && item.Message.Contains("UUID"));
    }

    [Fact]
    public async Task ImportReferences_ReplacesTables_AndKeepsMoneyUntouched()
    {
        Assert.True((await Run()).Ok);
        Write("references.json", "[{\"name\":\"Котельные\",\"columns\":[\"ID\",\"Название\"],\"rows\":[[\"1\",\"Центральная\"],[\"2\",\"Северная\"]],\"sensitive\":false}]");
        await using (var db = Db()) Assert.Equal(1, await DataMigration.ImportReferences(db, Path.Combine(_folder, "references.json")));
        await using (var db = Db()) Assert.Equal(1, await DataMigration.ImportReferences(db, Path.Combine(_folder, "references.json")));
        await using var check = Db();
        var table = await check.ReferenceTables.SingleAsync();
        Assert.Equal(2, table.RowCount);
        Assert.Contains("Северная", table.RowsJson);
        Assert.Equal(100050 + 20000 + 1000, await check.ServiceLines.SumAsync(line => line.Debt));
    }

    [Fact]
    public async Task Windows1251Files_AreRead()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllText(Path.Combine(_folder, "tariffs.csv"), "id;name;scheme;unit\nheat;Тепло по площади;HeatHouseNorm;м²\nwater;Вода;MeterApartment;м³\n", Encoding.GetEncoding(1251));
        Assert.True((await Run()).Ok);
        await using var db = Db();
        Assert.Contains(await db.Tariffs.ToListAsync(), item => item.Name == "Тепло по площади");
    }

    [Fact]
    public async Task Report_ContainsNoPersonalData()
    {
        Write("abonents.csv", "account;kind;name;street;area\n100;ФЛ;Иванов Секретный;ул. Секретная, 1;плохо\n");
        var text = DataMigration.Format(await Run());
        Assert.DoesNotContain("Секрет", text);
        Assert.Contains("ЛС 100", text);
    }

    [Fact]
    public async Task BadPeriodArgument_IsRefused()
    {
        Assert.False((await Run(period: "ноябрь")).Ok);
    }
}
