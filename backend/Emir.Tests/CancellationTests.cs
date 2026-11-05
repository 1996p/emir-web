using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

public sealed class CancellationTests : IDisposable
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

    private static Task<JsonElement> Preview(HttpClient client, string query) => client.GetFromJsonAsync<JsonElement>($"/api/period/cancel/preview?{query}");

    /// <summary>Как на экране: сначала просмотр, затем отмена той же выборки.</summary>
    private static async Task<HttpResponseMessage> Cancel(HttpClient client, string[] services, string? kind = null, string? place = null, string? query = null, string reason = "Ошибка в запуске")
    {
        var filter = $"services={string.Join(',', services)}" + (kind is null ? "" : $"&kind={kind}") + (place is null ? "" : $"&place={Uri.EscapeDataString(place)}") + (query is null ? "" : $"&query={Uri.EscapeDataString(query)}");
        var preview = await Preview(client, filter);
        return await client.PostAsJsonAsync("/api/period/cancel", new { kind, place, query, services, reason, expectedTotal = preview.GetProperty("total").GetInt64(), expectedLines = preview.GetProperty("lines").GetArrayLength() });
    }

    private static async Task<long> Debt(HttpClient client, string id, string service) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/abonents/{id}")).GetProperty("services").EnumerateArray().First(line => line.GetProperty("id").GetString() == service).GetProperty("debt").GetInt64();

    private static async Task<int> LedgerMismatches(HttpClient client)
    {
        var checks = await client.GetFromJsonAsync<JsonElement>("/api/checks");
        return checks.GetProperty("groups").EnumerateArray().First(group => group.GetProperty("id").GetString() == "ledger").GetProperty("rows").GetArrayLength();
    }

    [Fact]
    public async Task Preview_ChangesNothing_AndShowsWhatWouldBeCancelled()
    {
        var client = await Login();
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        var before = await Debt(client, "a2", "heat");
        var preview = await Preview(client, "services=heat");
        Assert.Equal(10, preview.GetProperty("lines").GetArrayLength());
        Assert.True(preview.GetProperty("chargesDone").GetBoolean());
        Assert.Contains(preview.GetProperty("services").EnumerateArray(), item => item.GetProperty("id").GetString() == "heat");
        Assert.Equal(before, await Debt(client, "a2", "heat"));
    }

    [Fact]
    public async Task CancelAll_RemovesCharges_ThenRerunRestoresExactly()
    {
        var client = await Login();
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        var charged = await Debt(client, "a1", "heat");

        (await Cancel(client, ["heat"])).EnsureSuccessStatusCode();
        // 52 м² × 35,43 ₽ сняты, старый долг остался
        Assert.Equal(charged - 184236, await Debt(client, "a1", "heat"));
        await using (var db = Db())
        {
            Assert.False(await db.ChargeEntries.AnyAsync(entry => entry.Period == Nov && entry.ServiceId == "heat"));
            Assert.True(await db.ChargeEntries.AnyAsync(entry => entry.Period == "2026-10"));
            Assert.Equal(10, await db.ChargeRevisions.CountAsync(revision => revision.NewAmount == 0 && revision.Reason.StartsWith("Отмена")));
        }

        // Шаг снова можно запустить
        var steps = await client.GetFromJsonAsync<JsonElement>("/api/period/steps");
        Assert.Equal("current", steps.EnumerateArray().First(step => step.GetProperty("id").GetString() == "charges").GetProperty("state").GetString());
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        Assert.Equal(charged, await Debt(client, "a1", "heat"));
        Assert.Equal(0, await LedgerMismatches(client));
    }

    [Fact]
    public async Task PaidCharge_BecomesCredit_NotNegativeDebt()
    {
        var client = await Login();
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        var kassa = await Login("kassa1");
        (await kassa.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a2", source = "cash-person", allocations = new[] { new { serviceId = "heat", amount = 155892L } } })).EnsureSuccessStatusCode();

        (await Cancel(client, ["heat"], query: "40010002")).EnsureSuccessStatusCode();
        Assert.Equal(0, await Debt(client, "a2", "heat"));
        Assert.Equal(155892, (await client.GetFromJsonAsync<JsonElement>("/api/abonents/a2")).GetProperty("credit").GetInt64());
        Assert.Equal(0, await LedgerMismatches(client));
    }

    [Fact]
    public async Task MeterCharge_IsCancelled_ReadingStays_RerunBringsItBack()
    {
        var oper = await Login("oper1");
        (await oper.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 341m })).EnsureSuccessStatusCode();
        var client = await Login();
        Assert.Equal(52000, await Debt(client, "a7", "water"));

        // Показание есть, запуска ещё не было: начисление всё равно отменяется
        (await Cancel(client, ["water"], query: "40010006")).EnsureSuccessStatusCode();
        Assert.Equal(0, await Debt(client, "a7", "water"));
        var abonent = await client.GetFromJsonAsync<JsonElement>("/api/abonents/a7");
        Assert.Equal(341m, abonent.GetProperty("services").EnumerateArray().First(line => line.GetProperty("id").GetString() == "water").GetProperty("meter").GetProperty("lastReading").GetDecimal());

        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        Assert.Equal(52000, await Debt(client, "a7", "water"));
        Assert.Equal(0, await LedgerMismatches(client));

        // Повторный запуск не задваивает
        (await client.PostAsync("/api/abonents/a7/recharge", null)).EnsureSuccessStatusCode();
        Assert.Equal(52000, await Debt(client, "a7", "water"));
    }

    [Fact]
    public async Task MeterCharge_AfterCancel_ReenteredReadingChargesFullOnce()
    {
        var oper = await Login("oper1");
        (await oper.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 341m })).EnsureSuccessStatusCode();
        var client = await Login();
        (await Cancel(client, ["water"], query: "40010006")).EnsureSuccessStatusCode();
        (await oper.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 343m })).EnsureSuccessStatusCode();
        // 12 м³ × 52 ₽ от начала месяца
        Assert.Equal(62400, await Debt(client, "a7", "water"));
    }

    [Theory]
    [InlineData("person", null, null, 8)]
    [InlineData("org", null, null, 2)]
    [InlineData(null, "с. Журавка", null, 3)]
    [InlineData(null, null, "Петров С", 1)]
    [InlineData(null, null, "Петров", 2)] // И Петров, и Анна Петровна: поэтому перед отменой показывается список
    [InlineData(null, null, "40010002", 1)]
    [InlineData(null, null, "ул. Школьная", 2)]
    [InlineData(null, null, "нет такого", 0)]
    public async Task Filters_NarrowTheSelection(string? kind, string? place, string? query, int expected)
    {
        var client = await Login();
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        var filter = "services=heat" + (kind is null ? "" : $"&kind={kind}") + (place is null ? "" : $"&place={Uri.EscapeDataString(place)}") + (query is null ? "" : $"&query={Uri.EscapeDataString(query)}");
        Assert.Equal(expected, (await Preview(client, filter)).GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task Partial_LeavesOthersUntouched_AndOtherServices()
    {
        var client = await Login();
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        var a3 = await Debt(client, "a3", "heat");
        (await Cancel(client, ["heat"], kind: "org")).EnsureSuccessStatusCode();
        Assert.Equal(a3, await Debt(client, "a3", "heat"));
        await using var db = Db();
        Assert.False(await db.ChargeEntries.AnyAsync(entry => entry.AbonentId == "a4" && entry.Period == Nov));
        Assert.True(await db.ChargeEntries.AnyAsync(entry => entry.AbonentId == "a3" && entry.Period == Nov));
    }

    [Fact]
    public async Task Penalty_CanBeCancelled_AndStepsRestart()
    {
        var client = await Login();
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        await using (var db = Db())
        {
            // Пени за месяц без ожидания 31 дня просрочки
            db.ChargeEntries.Add(new ChargeEntry { AbonentId = "a1", ServiceId = "penalty", Period = Nov, Scheme = "Penalty", Amount = 700, AppliedToDebt = true, AtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var before = await Debt(client, "a1", "penalty");
        (await Cancel(client, ["penalty"], query: "40010001")).EnsureSuccessStatusCode();
        Assert.Equal(before - 700, await Debt(client, "a1", "penalty"));
    }

    [Fact]
    public async Task ClosedOrReopenedMonth_IsRefused()
    {
        var client = await Login();
        foreach (var step in new[] { "charges", "checks", "penalty", "close" }) (await client.PostAsync($"/api/period/steps/{step}/run", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Cancel(client, ["heat"])).StatusCode);
        (await client.PostAsJsonAsync($"/api/period/months/{Nov}/reopen", new { reason = "Тест" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Cancel(client, ["heat"])).StatusCode);
    }

    [Fact]
    public async Task StalePreview_EmptySelection_AndNoReason_AreRefused()
    {
        var client = await Login();
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/period/cancel", new { services = new[] { "heat" }, reason = "Х", expectedTotal = 1L, expectedLines = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Cancel(client, ["heat"], query: "нет такого")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Cancel(client, ["heat"], reason: " ")).StatusCode);
    }

    [Fact]
    public async Task Roles_OnlyAccountantAndAdmin()
    {
        foreach (var login in new[] { "kassa1", "oper1" })
        {
            var other = await Login(login);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/period/cancel/preview?services=heat")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync("/api/period/cancel", new { services = new[] { "heat" }, reason = "Х", expectedTotal = 0L, expectedLines = 0 })).StatusCode);
        }
    }

    [Fact]
    public async Task Audit_LedgerJournalTurnoverAndCheck()
    {
        var client = await Login();
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        (await Cancel(client, ["heat"], query: "40010002", reason: "Приказ 7")).EnsureSuccessStatusCode();

        await using (var db = Db())
        {
            var row = await db.LedgerEntries.OrderBy(item => item.Id).LastAsync(item => item.AbonentId == "a2");
            Assert.Equal("cancel", row.Kind);
            Assert.Equal(-155892, row.DebtDelta);
            Assert.Contains(await db.Journal.Where(item => item.AbonentId == "a2").ToListAsync(), item => item.Action == "Отмена начислений" && item.Reason == "Приказ 7");
        }

        var rows = await client.GetFromJsonAsync<JsonElement>("/api/abonents/a2/ledger");
        Assert.Equal("Отмена начисления", rows[0].GetProperty("kindName").GetString());

        var report = await client.GetFromJsonAsync<JsonElement>("/api/reports/run?reportId=turnover&period=Этот месяц&cut=Абоненты&who=Все");
        var petrov = report.GetProperty("rows").EnumerateArray().First(item => item.GetProperty("name").GetString() == "Петров С. И.").GetProperty("values");
        Assert.Equal(0, petrov[1].GetInt64());
        Assert.Equal(0, petrov[4].GetInt64());

        var checks = await client.GetFromJsonAsync<JsonElement>("/api/checks");
        var revisions = checks.GetProperty("groups").EnumerateArray().First(group => group.GetProperty("id").GetString() == "revisions");
        Assert.Contains(revisions.GetProperty("rows").EnumerateArray(), row => row.GetProperty("cells")[6].GetString()!.Contains("Приказ 7"));
        Assert.Equal(0, checks.GetProperty("groups").EnumerateArray().First(group => group.GetProperty("id").GetString() == "differences").GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task ParallelCancel_AppliesOnce()
    {
        var client = await Login();
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        var before = await Debt(client, "a2", "heat");
        var preview = await Preview(client, "services=heat&query=40010002");
        var body = new { query = "40010002", services = new[] { "heat" }, reason = "Параллельно", expectedTotal = preview.GetProperty("total").GetInt64(), expectedLines = 1 };
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.PostAsJsonAsync("/api/period/cancel", body)));
        Assert.Single(responses, response => response.IsSuccessStatusCode);
        Assert.Equal(before - 155892, await Debt(client, "a2", "heat"));
    }
}
