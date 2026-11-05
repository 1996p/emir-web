using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

public sealed class ReclosingTests : IDisposable
{
    private const string Nov = "2026-11";
    private const string Dec = "2026-12";
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> Login(string login = "buh1")
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = "test-password" })).EnsureSuccessStatusCode();
        return client;
    }

    private EmirDb Db() => new(new DbContextOptionsBuilder<EmirDb>().UseNpgsql(_factory.ConnectionString).Options);

    private static async Task CloseMonth(HttpClient client)
    {
        foreach (var step in new[] { "charges", "checks", "penalty", "close" })
            (await client.PostAsync($"/api/period/steps/{step}/run", null)).EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> Reopen(HttpClient client, string period = Nov) =>
        client.PostAsJsonAsync($"/api/period/months/{period}/reopen", new { reason = "Ошибка в цене" });

    private static async Task<JsonElement> Preview(HttpClient client) => await client.GetFromJsonAsync<JsonElement>("/api/period/reclose/preview");

    private static async Task<HttpResponseMessage> Reclose(HttpClient client)
    {
        var preview = await Preview(client);
        return await client.PostAsJsonAsync("/api/period/reclose", new { reason = "Приказ об исправлении", expectedDelta = preview.GetProperty("delta").GetInt64() });
    }

    private static async Task<long> Debt(HttpClient client, string id, string service)
    {
        var abonent = await client.GetFromJsonAsync<JsonElement>($"/api/abonents/{id}");
        return abonent.GetProperty("services").EnumerateArray().First(line => line.GetProperty("id").GetString() == service).GetProperty("debt").GetInt64();
    }

    private static Task<HttpResponseMessage> SetPrice(HttpClient client, string tariff, string from, decimal price) =>
        client.PostAsJsonAsync($"/api/tariffs/{tariff}/prices", new { fromPeriod = from, price, reason = "Приказ" });

    [Fact]
    public async Task NewBase_StartsInSeededMonth_AndCurrentIsOpen()
    {
        var client = await Login();
        var current = await client.GetFromJsonAsync<JsonElement>("/api/period/current");
        Assert.Equal(Nov, current.GetProperty("period").GetString());
        Assert.Equal("Ноябрь 2026", current.GetProperty("title").GetString());
        Assert.Equal("open", current.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OpenNext_RequiresClosedMonth_ThenStartsFreshCycle()
    {
        var client = await Login();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/period/open-next", null)).StatusCode);

        await CloseMonth(client);
        (await client.PostAsync("/api/period/open-next", null)).EnsureSuccessStatusCode();

        var current = await client.GetFromJsonAsync<JsonElement>("/api/period/current");
        Assert.Equal(Dec, current.GetProperty("period").GetString());
        Assert.Equal("open", current.GetProperty("status").GetString());
        var steps = await client.GetFromJsonAsync<JsonElement>("/api/period/steps");
        Assert.Equal("current", steps.EnumerateArray().First(step => step.GetProperty("id").GetString() == "charges").GetProperty("state").GetString());
        Assert.Equal("next", steps.EnumerateArray().First(step => step.GetProperty("id").GetString() == "close").GetProperty("state").GetString());

        // Декабрь считается отдельно от ноября и добавляет к долгу новую сумму
        var before = await Debt(client, "a2", "heat");
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        Assert.True(await Debt(client, "a2", "heat") > before);
        await using var db = Db();
        Assert.Equal(2, await db.ChargeEntries.CountAsync(entry => entry.AbonentId == "a2" && entry.ServiceId == "heat"));
    }

    [Fact]
    public async Task OpenNext_AppliesTermsOfNewMonth()
    {
        var client = await Login();
        (await client.PostAsJsonAsync("/api/abonents/a2/services/heat/terms", new { fromPeriod = Dec, tariffId = (string?)null, state = "Приостановлена", reason = "Заявление" })).EnsureSuccessStatusCode();
        await CloseMonth(client);
        (await client.PostAsync("/api/period/open-next", null)).EnsureSuccessStatusCode();
        var abonent = await client.GetFromJsonAsync<JsonElement>("/api/abonents/a2");
        Assert.Equal("Приостановлена", abonent.GetProperty("services")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task ClosedMonth_BlocksChanges_UntilReopened()
    {
        var client = await Login();
        await CloseMonth(client);
        Assert.Equal(HttpStatusCode.Conflict, (await SetPrice(client, "heat-norm", Nov, 40m)).StatusCode);
        var oper = await Login("oper1");
        Assert.Equal(HttpStatusCode.Conflict, (await oper.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 340m })).StatusCode);

        (await Reopen(client)).EnsureSuccessStatusCode();
        (await oper.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 340m })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Reclose_WithoutChanges_MovesNoMoney()
    {
        var client = await Login();
        (await (await Login("oper1")).PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 335.5m })).EnsureSuccessStatusCode();
        await CloseMonth(client);
        (await Reopen(client)).EnsureSuccessStatusCode();

        var preview = await Preview(client);
        Assert.Equal(0, preview.GetProperty("delta").GetInt64());
        Assert.Equal(0, preview.GetProperty("changes").GetArrayLength());

        (await Reclose(client)).EnsureSuccessStatusCode();
        var closed = await client.GetFromJsonAsync<JsonElement>("/api/period/closed");
        Assert.Equal(1, closed[0].GetProperty("reclosedCount").GetInt32());
        Assert.False(closed[0].GetProperty("reopened").GetBoolean());
    }

    [Fact]
    public async Task Reclose_NewPrice_AddsOnlyDifference_AndKeepsOldValues()
    {
        var client = await Login();
        await CloseMonth(client);
        var debtBefore = await Debt(client, "a2", "heat");
        await using (var db = Db())
        {
            var entry = await db.ChargeEntries.FirstAsync(item => item.AbonentId == "a2" && item.ServiceId == "heat" && item.Period == Nov);
            Assert.Equal(35.43m, entry.Price);
        }

        (await Reopen(client)).EnsureSuccessStatusCode();
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/tariffs")).GetProperty("reopened").GetBoolean());
        (await SetPrice(client, "heat-norm", Nov, 40m)).EnsureSuccessStatusCode();

        // Предпросмотр не трогает деньги. 44 м² × (40 − 35,43) = 201,08 ₽
        var preview = await Preview(client);
        var change = preview.GetProperty("changes").EnumerateArray().First(item => item.GetProperty("abonentId").GetString() == "a2");
        Assert.Equal(20108, change.GetProperty("newAmount").GetInt64() - change.GetProperty("oldAmount").GetInt64());
        Assert.Equal(debtBefore, await Debt(client, "a2", "heat"));

        (await Reclose(client)).EnsureSuccessStatusCode();
        Assert.Equal(debtBefore + 20108, await Debt(client, "a2", "heat"));

        await using var check = Db();
        var updated = await check.ChargeEntries.FirstAsync(item => item.AbonentId == "a2" && item.ServiceId == "heat" && item.Period == Nov);
        Assert.Equal(40m, updated.Price);
        var revision = await check.ChargeRevisions.FirstAsync(item => item.AbonentId == "a2");
        Assert.Equal(35.43m, revision.OldPrice);
        Assert.Equal(40m, revision.NewPrice);
        Assert.Equal("Приказ об исправлении", revision.Reason);
        Assert.Equal(20108, revision.NewAmount - revision.OldAmount);
        Assert.Contains(await check.Journal.Where(item => item.AbonentId == "a2").ToListAsync(), item => item.Action == "Перезакрытие месяца");

        // Повтор не удваивает разницу
        (await Reopen(client)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await Preview(client)).GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task Reclose_LowerPrice_PaidCharge_BecomesCredit()
    {
        var client = await Login();
        await CloseMonth(client);
        var debt = await Debt(client, "a2", "heat");
        var cashier = await Login("kassa1");
        (await cashier.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a2", source = "cash-person", allocations = new[] { new { serviceId = "heat", amount = debt } } })).EnsureSuccessStatusCode();

        (await Reopen(client)).EnsureSuccessStatusCode();
        (await SetPrice(client, "heat-norm", Nov, 30m)).EnsureSuccessStatusCode();
        (await Reclose(client)).EnsureSuccessStatusCode();

        var abonent = await client.GetFromJsonAsync<JsonElement>("/api/abonents/a2");
        Assert.Equal(0, await Debt(client, "a2", "heat"));
        // 44 м² × (35,43 − 30) = 238,92 ₽ вернулись переплатой
        Assert.Equal(23892, abonent.GetProperty("credit").GetInt64());
    }

    [Fact]
    public async Task Reclose_UsesAreaAtChargeTime_NotTodays()
    {
        var client = await Login();
        await CloseMonth(client);
        await using (var db = Db())
        {
            var abonent = await db.Abonents.FirstAsync(item => item.Id == "a2");
            abonent.Area = 500;
            await db.SaveChangesAsync();
        }
        (await Reopen(client)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await Preview(client)).GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task Reclose_CorrectedReading_AddsDifferenceOnce()
    {
        var oper = await Login("oper1");
        (await oper.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 341m })).EnsureSuccessStatusCode();
        var client = await Login();
        await CloseMonth(client);
        Assert.Equal(52000, await Debt(client, "a7", "water"));

        (await Reopen(client)).EnsureSuccessStatusCode();
        (await oper.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 343m })).EnsureSuccessStatusCode();
        // Показание сразу добавило разницу в долг, а перезакрытие приводит начисление к тому же значению
        Assert.Equal(62400, await Debt(client, "a7", "water"));
        var change = Assert.Single((await Preview(client)).GetProperty("changes").EnumerateArray());
        Assert.Equal(change.GetProperty("oldAmount").GetInt64(), change.GetProperty("newAmount").GetInt64());

        (await Reclose(client)).EnsureSuccessStatusCode();
        Assert.Equal(62400, await Debt(client, "a7", "water"));
        await using var db = Db();
        var entry = await db.ChargeEntries.FirstAsync(item => item.AbonentId == "a7" && item.ServiceId == "water" && item.Period == Nov);
        Assert.Equal(62400, entry.Amount);
        Assert.Equal(12m, entry.Volume);
    }

    [Fact]
    public async Task Reclose_CorrectedPrice_OnMeterReading_AdjustsDebt()
    {
        var oper = await Login("oper1");
        (await oper.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 341m })).EnsureSuccessStatusCode();
        var client = await Login();
        await CloseMonth(client);

        (await Reopen(client)).EnsureSuccessStatusCode();
        (await SetPrice(client, "water-meter", Nov, 60m)).EnsureSuccessStatusCode();
        (await Reclose(client)).EnsureSuccessStatusCode();
        // 10 м³ × 60 ₽
        Assert.Equal(60000, await Debt(client, "a7", "water"));
    }

    [Fact]
    public async Task Reclose_SuspendedService_RemovesCharge()
    {
        var client = await Login();
        await CloseMonth(client);
        var before = await Debt(client, "a2", "heat");
        (await Reopen(client)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/abonents/a2/services/heat/terms", new { fromPeriod = Nov, tariffId = "heat-norm", state = "Приостановлена", reason = "Ошибка" })).EnsureSuccessStatusCode();
        (await Reclose(client)).EnsureSuccessStatusCode();

        Assert.Equal(before - 155892, await Debt(client, "a2", "heat"));
        await using var db = Db();
        Assert.False(await db.ChargeEntries.AnyAsync(item => item.AbonentId == "a2" && item.ServiceId == "heat" && item.Period == Nov));
    }

    [Fact]
    public async Task Reclose_RemovedTariff_IsBlocked_NotSilentlyZeroed()
    {
        var client = await Login();
        await CloseMonth(client);
        (await Reopen(client)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/abonents/a2/services/heat/terms", new { fromPeriod = Nov, tariffId = (string?)null, state = "Работает", reason = "Тест" })).EnsureSuccessStatusCode();

        var preview = await Preview(client);
        Assert.Equal(1, preview.GetProperty("blocking").GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, (await Reclose(client)).StatusCode);
    }

    [Fact]
    public async Task Reclose_StalePreview_IsRefused()
    {
        var client = await Login();
        await CloseMonth(client);
        (await Reopen(client)).EnsureSuccessStatusCode();
        (await SetPrice(client, "heat-norm", Nov, 40m)).EnsureSuccessStatusCode();
        var response = await client.PostAsJsonAsync("/api/period/reclose", new { reason = "Тест", expectedDelta = 1L });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True((await Preview(client)).GetProperty("changes").GetArrayLength() > 0);
    }

    [Fact]
    public async Task WhileReopened_OtherMonthWorkIsBlocked_ButPaymentsContinue()
    {
        var client = await Login();
        await CloseMonth(client);
        (await client.PostAsync("/api/period/open-next", null)).EnsureSuccessStatusCode();
        (await Reopen(client)).EnsureSuccessStatusCode();

        var current = await client.GetFromJsonAsync<JsonElement>("/api/period/current");
        Assert.Equal(Nov, current.GetProperty("period").GetString());
        Assert.Equal("reopened", current.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/period/steps/charges/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/abonents/a2/recharge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/period/open-next", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Reopen(client)).StatusCode);

        var cashier = await Login("kassa1");
        (await cashier.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a3", source = "cash-person", allocations = new[] { new { serviceId = "heat", amount = 1000L } } })).EnsureSuccessStatusCode();

        (await Reclose(client)).EnsureSuccessStatusCode();
        current = await client.GetFromJsonAsync<JsonElement>("/api/period/current");
        Assert.Equal(Dec, current.GetProperty("period").GetString());
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Reopen_RefusedAfterNextMonthStarted_AndForOldMonths()
    {
        var client = await Login();
        await CloseMonth(client);
        (await client.PostAsync("/api/period/open-next", null)).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/abonents/a2/recharge", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await Reopen(client)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Reopen(client, Dec)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Reopen(client, "2020-01")).StatusCode);
        var closed = await client.GetFromJsonAsync<JsonElement>("/api/period/closed");
        Assert.Contains("корректировк", closed[0].GetProperty("cannotReopen").GetString());
    }

    [Fact]
    public async Task Reclose_KeepsTermsOfLatestMonthOnServiceLines()
    {
        var client = await Login();
        await CloseMonth(client);
        (await client.PostAsync("/api/period/open-next", null)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/abonents/a2/services/heat/terms", new { fromPeriod = Dec, tariffId = "heat-norm", state = "Приостановлена", reason = "Заявление" })).EnsureSuccessStatusCode();
        (await Reopen(client)).EnsureSuccessStatusCode();
        (await Reclose(client)).EnsureSuccessStatusCode();

        var abonent = await client.GetFromJsonAsync<JsonElement>("/api/abonents/a2");
        Assert.Equal("Приостановлена", abonent.GetProperty("services")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task Reclose_Roles()
    {
        var client = await Login();
        await CloseMonth(client);
        foreach (var login in new[] { "kassa1", "oper1" })
        {
            var other = await Login(login);
            Assert.Equal(HttpStatusCode.Forbidden, (await Reopen(other)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync("/api/period/open-next", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/period/reclose/preview")).StatusCode);
        }
        var noReason = await client.PostAsJsonAsync($"/api/period/months/{Nov}/reopen", new { reason = " " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
    }

    [Fact]
    public async Task ParallelReclose_AppliesDifferenceOnce()
    {
        var client = await Login();
        await CloseMonth(client);
        var before = await Debt(client, "a2", "heat");
        (await Reopen(client)).EnsureSuccessStatusCode();
        (await SetPrice(client, "heat-norm", Nov, 40m)).EnsureSuccessStatusCode();
        var delta = (await Preview(client)).GetProperty("delta").GetInt64();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.PostAsJsonAsync("/api/period/reclose", new { reason = "Параллельно", expectedDelta = delta })));

        Assert.Single(responses, response => response.IsSuccessStatusCode);
        Assert.Equal(before + 20108, await Debt(client, "a2", "heat"));
    }

    [Fact]
    public async Task StartPeriod_OnlyForEmptyBase()
    {
        _ = _factory.Server;
        await using var db = Db();
        await Assert.ThrowsAsync<ApiError>(() => AdminTools.StartPeriod(db, "2027-01"));
        await Assert.ThrowsAsync<ApiError>(() => AdminTools.StartPeriod(db, "bad"));
    }
}
