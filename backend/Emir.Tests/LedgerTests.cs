using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Emir.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

public sealed class LedgerTests : IDisposable
{
    private const string Nov = "2026-11";
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> Login(string login = "buh1")
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = "test-password" })).EnsureSuccessStatusCode();
        return client;
    }

    private EmirDb Db() => new(new DbContextOptionsBuilder<EmirDb>().UseNpgsql(_factory.ConnectionString).Options);

    private async Task<int> Mismatches(HttpClient client)
    {
        var checks = await client.GetFromJsonAsync<JsonElement>("/api/checks");
        return checks.GetProperty("groups").EnumerateArray().First(group => group.GetProperty("id").GetString() == "ledger").GetProperty("rows").GetArrayLength();
    }

    /// <summary>Журнал обязан сойтись с остатками по каждой услуге и каждому абоненту.</summary>
    private async Task AssertLedgerMatchesBalances()
    {
        await using var db = Db();
        var ledger = await db.LedgerEntries.ToListAsync();
        foreach (var line in await db.ServiceLines.ToListAsync())
            Assert.Equal(line.Debt, ledger.Where(item => item.AbonentId == line.AbonentId && item.ServiceId == line.ServiceId).Sum(item => item.DebtDelta));
        foreach (var abonent in await db.Abonents.ToListAsync())
            Assert.Equal(abonent.Credit, ledger.Where(item => item.AbonentId == abonent.Id).Sum(item => item.CreditDelta));
    }

    [Fact]
    public async Task NewBase_LedgerStartsFromOpeningBalances()
    {
        var client = await Login();
        await AssertLedgerMatchesBalances();
        Assert.Equal(0, await Mismatches(client));
        await using var db = Db();
        Assert.All(await db.LedgerEntries.ToListAsync(), item => Assert.Equal("opening", item.Kind));
    }

    [Fact]
    public async Task EveryMoneyOperation_IsRecorded_AndLedgerStaysEqualToBalances()
    {
        var buh = await Login();
        var kassa = await Login("kassa1");
        var oper = await Login("oper1");

        // Показание, оплата с показанием, оплата, начисление
        (await oper.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 335m })).EnsureSuccessStatusCode();
        (await kassa.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a1", source = "cash-person", reading = 420m, allocations = new[] { new { serviceId = "heat", amount = 50000L } } })).EnsureSuccessStatusCode();
        (await kassa.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a3", source = "cash-person", allocations = new[] { new { serviceId = "heat", amount = 900000L } } })).EnsureSuccessStatusCode();
        (await buh.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        (await buh.PostAsync("/api/abonents/a2/recharge", null)).EnsureSuccessStatusCode();

        // Возврат, все виды корректировок
        (await kassa.PostAsJsonAsync("/api/cash/refunds", new { abonentId = "a5", paymentId = "a5-p1", reason = "Тест" })).EnsureSuccessStatusCode();
        var types = await buh.GetFromJsonAsync<JsonElement>("/api/corrections/types");
        foreach (var type in types.EnumerateArray())
        {
            var needsPayment = type.GetProperty("needsPayment").GetBoolean();
            var response = await buh.PostAsJsonAsync("/api/corrections", new { abonentId = "a6", typeId = type.GetProperty("id").GetString(), amount = 100L, reason = "Тест", paymentId = needsPayment ? "a6-p2" : null });
            if (needsPayment && !response.IsSuccessStatusCode) Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            else response.EnsureSuccessStatusCode();
        }

        // Реестр
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Лицевой счёт;Сумма\n40010007;640,00\n")), "file", "sber.csv");
            var upload = await buh.PostAsync("/api/exchange/upload", form);
            upload.EnsureSuccessStatusCode();
            var id = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            (await buh.PostAsync($"/api/exchange/batches/{id}/post", null)).EnsureSuccessStatusCode();
        }

        // Пени, закрытие, перезакрытие
        foreach (var step in new[] { "checks", "penalty", "close" }) (await buh.PostAsync($"/api/period/steps/{step}/run", null)).EnsureSuccessStatusCode();
        (await buh.PostAsJsonAsync($"/api/period/months/{Nov}/reopen", new { reason = "Тест" })).EnsureSuccessStatusCode();
        (await buh.PostAsJsonAsync("/api/tariffs/heat-norm/prices", new { fromPeriod = Nov, price = 40m, reason = "Тест" })).EnsureSuccessStatusCode();
        var preview = await buh.GetFromJsonAsync<JsonElement>("/api/period/reclose/preview");
        (await buh.PostAsJsonAsync("/api/period/reclose", new { reason = "Тест", expectedDelta = preview.GetProperty("delta").GetInt64() })).EnsureSuccessStatusCode();

        await AssertLedgerMatchesBalances();
        Assert.Equal(0, await Mismatches(buh));
        await using var db = Db();
        var kinds = await db.LedgerEntries.Select(item => item.Kind).Distinct().ToListAsync();
        foreach (var expected in new[] { "opening", "reading", "payment", "charge", "refund", "correction", "exchange", "reclose" }) Assert.Contains(expected, kinds);
        Assert.All(await db.LedgerEntries.ToListAsync(), item => { Assert.NotEqual("", item.Who); Assert.True(item.DebtDelta != 0 || item.CreditDelta != 0); });
    }

    [Fact]
    public async Task PaymentWithReading_IsTwoSeparateMovements()
    {
        var kassa = await Login("kassa1");
        (await kassa.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a7", source = "cash-person", reading = 341m, allocations = new[] { new { serviceId = "water", amount = 52000L } } })).EnsureSuccessStatusCode();
        await using var db = Db();
        var rows = await db.LedgerEntries.Where(item => item.AbonentId == "a7" && item.ServiceId == "water" && item.Kind != "opening").OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(["reading", "payment"], rows.Select(item => item.Kind));
        Assert.Equal(52000, rows[0].DebtDelta);
        Assert.Equal(-52000, rows[1].DebtDelta);
    }

    [Fact]
    public async Task FailedOperation_LeavesNoLedgerRows()
    {
        var kassa = await Login("kassa1");
        await using var before = Db();
        var count = await before.LedgerEntries.CountAsync();
        var response = await kassa.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a1", source = "cash-person", reading = 500m, allocations = new[] { new { serviceId = "heat", amount = -5L } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await using var after = Db();
        Assert.Equal(count, await after.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task ChangeWithoutOperation_IsRefused()
    {
        _ = _factory.Server;
        await using var db = Db();
        var line = await db.ServiceLines.FirstAsync(item => item.AbonentId == "a2" && item.ServiceId == "heat");
        line.Debt += 100;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ChangeBypassingApplication_IsFoundByCheck()
    {
        var client = await Login();
        await using (var db = Db())
        {
            await db.Database.ExecuteSqlRawAsync("""UPDATE "ServiceLines" SET "Debt" = "Debt" + 777 WHERE "AbonentId" = 'a2' AND "ServiceId" = 'heat'""");
        }
        Assert.Equal(1, await Mismatches(client));
        var checks = await client.GetFromJsonAsync<JsonElement>("/api/checks");
        var row = checks.GetProperty("groups").EnumerateArray().First(group => group.GetProperty("id").GetString() == "ledger").GetProperty("rows")[0];
        Assert.Equal("a2", row.GetProperty("abonentId").GetString());
        Assert.Equal("7,77", row.GetProperty("cells")[4].GetString());
    }

    [Theory]
    [InlineData("""UPDATE "LedgerEntries" SET "DebtDelta" = 0""")]
    [InlineData("""DELETE FROM "LedgerEntries" """)]
    [InlineData("""TRUNCATE "LedgerEntries" """)]
    public async Task LedgerCannotBeChanged(string sql)
    {
        _ = _factory.Server;
        await using var db = Db();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Contains("Журнал денег", error.Message);
    }

    [Fact]
    public async Task AbonentLedger_ShowsRunningBalance_NewestFirst()
    {
        var kassa = await Login("kassa1");
        (await kassa.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a3", source = "cash-person", allocations = new[] { new { serviceId = "heat", amount = 10000L } } })).EnsureSuccessStatusCode();
        var rows = await kassa.GetFromJsonAsync<JsonElement>("/api/abonents/a3/ledger");
        var abonent = await kassa.GetFromJsonAsync<JsonElement>("/api/abonents/a3");
        var balance = abonent.GetProperty("credit").GetInt64() - abonent.GetProperty("services").EnumerateArray().Sum(line => line.GetProperty("debt").GetInt64());
        Assert.Equal(balance, rows[0].GetProperty("balanceAfter").GetInt64());
        Assert.Equal("Оплата", rows[0].GetProperty("kindName").GetString());
        Assert.Equal(-10000, rows[0].GetProperty("debtDelta").GetInt64());
        Assert.Equal("Входящий остаток", rows[rows.GetArrayLength() - 1].GetProperty("kindName").GetString());
    }

    [Fact]
    public async Task Turnover_ClosingEqualsCurrentBalance_AndRowsAddUp()
    {
        var buh = await Login();
        var kassa = await Login("kassa1");
        (await kassa.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a3", source = "cash-person", allocations = new[] { new { serviceId = "heat", amount = 10000L } } })).EnsureSuccessStatusCode();
        (await buh.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();

        var report = await buh.GetFromJsonAsync<JsonElement>("/api/reports/run?reportId=turnover&period=Этот месяц&cut=Абоненты&who=Все");
        Assert.True(report.GetProperty("available").GetBoolean());
        var abonents = await buh.GetFromJsonAsync<JsonElement>("/api/abonents");
        long Balance(JsonElement abonent) => abonent.GetProperty("credit").GetInt64() - abonent.GetProperty("services").EnumerateArray().Sum(line => line.GetProperty("debt").GetInt64());

        foreach (var row in report.GetProperty("rows").EnumerateArray())
        {
            var v = row.GetProperty("values").EnumerateArray().Select(item => item.GetInt64()).ToList();
            // Входящий − начислено + оплачено + корректировки = исходящий
            Assert.Equal(v[4], v[0] - v[1] + v[2] + v[3]);
            var abonent = abonents.EnumerateArray().First(item => item.GetProperty("shortName").GetString() == row.GetProperty("name").GetString());
            Assert.Equal(Balance(abonent), v[4]);
        }
        var sidorova = report.GetProperty("rows").EnumerateArray().First(item => item.GetProperty("name").GetString() == "Сидорова М. О.").GetProperty("values");
        Assert.Equal(10000, sidorova[2].GetInt64());
        Assert.True(sidorova[1].GetInt64() > 0);
    }

    [Fact]
    public async Task Turnover_ByServices_AndFutureRange_KeepsBalance()
    {
        var buh = await Login();
        var report = await buh.GetFromJsonAsync<JsonElement>("/api/reports/run?reportId=turnover&period=Этот месяц&cut=Услуги&who=Все");
        Assert.Contains(report.GetProperty("rows").EnumerateArray(), row => row.GetProperty("name").GetString() == "Переплата");
        Assert.Equal(HttpStatusCode.OK, (await buh.GetAsync("/api/reports/export.csv?reportId=turnover&period=Этот месяц&cut=Услуги&who=Все")).StatusCode);

        // Диапазон до начала учёта: движений и остатков ещё нет
        var old = await buh.GetFromJsonAsync<JsonElement>("/api/reports/run?reportId=turnover&period=Свои даты&cut=Абоненты&who=Все&from=2001-01-01&to=2001-01-31");
        Assert.All(old.GetProperty("rows").EnumerateArray(), row => Assert.All(row.GetProperty("values").EnumerateArray(), value => Assert.Equal(0, value.GetInt64())));
    }
}
