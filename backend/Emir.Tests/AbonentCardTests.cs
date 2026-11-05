using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

public sealed class AbonentCardTests : IDisposable
{
    private const string Nov = "2026-11";
    private const string Dec = "2026-12";
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> Login(string login = "oper1")
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = "test-password" })).EnsureSuccessStatusCode();
        return client;
    }

    private EmirDb Db() => new(new DbContextOptionsBuilder<EmirDb>().UseNpgsql(_factory.ConnectionString).Options);

    private static Task<HttpResponseMessage> SetFact(HttpClient client, string id, string from, decimal area, int residents = 2, string reason = "Выписка") =>
        client.PostAsJsonAsync($"/api/abonents/{id}/facts", new { fromPeriod = from, area, residents, reason });

    private async Task<long> PlannedHeat(string id, string period)
    {
        _ = _factory.Server;
        await using var db = Db();
        var abonents = await db.Abonents.Include(item => item.Services).Where(item => item.Id == id).ToListAsync();
        return (await ChargingService.Plan(db, abonents, period)).First(item => item.Line.ServiceId == "heat").Result.Amount;
    }

    private static async Task CloseMonth(HttpClient client)
    {
        foreach (var step in new[] { "charges", "checks", "penalty", "close" })
            (await client.PostAsync($"/api/period/steps/{step}/run", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task FutureArea_ChangesOnlyItsMonths_AndAppliesWhenMonthOpens()
    {
        var oper = await Login();
        (await SetFact(oper, "a2", Dec, 100m)).EnsureSuccessStatusCode();

        // 44 м² × 35,43 ₽ сейчас, 100 м² × 35,43 ₽ с декабря
        Assert.Equal(155892, await PlannedHeat("a2", Nov));
        Assert.Equal(354300, await PlannedHeat("a2", Dec));
        Assert.Equal(44m, (await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a2")).GetProperty("area").GetDecimal());

        var buh = await Login("buh1");
        await CloseMonth(buh);
        (await buh.PostAsync("/api/period/open-next", null)).EnsureSuccessStatusCode();
        Assert.Equal(100m, (await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a2")).GetProperty("area").GetDecimal());
    }

    [Fact]
    public async Task AreaInOpenMonth_AppliesBeforeCharges_AndIsRefusedAfter()
    {
        var oper = await Login();
        (await SetFact(oper, "a2", Nov, 50m)).EnsureSuccessStatusCode();
        Assert.Equal(50m, (await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a2")).GetProperty("area").GetDecimal());
        Assert.Equal(177150, await PlannedHeat("a2", Nov));

        (await oper.PostAsync("/api/abonents/a2/recharge", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await SetFact(oper, "a2", Nov, 60m)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetFact(oper, "a2", "2026-10", 60m)).StatusCode);
        (await SetFact(oper, "a2", Dec, 60m)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Reclose_WithCorrectedArea_ProducesDifference_AndCheckStaysClean()
    {
        var buh = await Login("buh1");
        await CloseMonth(buh);
        var before = (await buh.GetFromJsonAsync<JsonElement>("/api/abonents/a2")).GetProperty("services")[0].GetProperty("debt").GetInt64();

        (await buh.PostAsJsonAsync($"/api/period/months/{Nov}/reopen", new { reason = "Ошибка в площади" })).EnsureSuccessStatusCode();
        (await SetFact(buh, "a2", Nov, 54m)).EnsureSuccessStatusCode();
        var preview = await buh.GetFromJsonAsync<JsonElement>("/api/period/reclose/preview");
        var change = Assert.Single(preview.GetProperty("changes").EnumerateArray());
        // 10 м² × 35,43 ₽
        Assert.Equal(35430, change.GetProperty("delta").GetInt64());

        (await buh.PostAsJsonAsync("/api/period/reclose", new { reason = "Акт", expectedDelta = 35430L })).EnsureSuccessStatusCode();
        Assert.Equal(before + 35430, (await buh.GetFromJsonAsync<JsonElement>("/api/abonents/a2")).GetProperty("services")[0].GetProperty("debt").GetInt64());

        var checks = await buh.GetFromJsonAsync<JsonElement>($"/api/checks?period={Nov}");
        Assert.Equal(0, checks.GetProperty("groups").EnumerateArray().First(group => group.GetProperty("id").GetString() == "differences").GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task EarlierMonths_KeepTheirArea_WhenLaterChanged()
    {
        var oper = await Login();
        (await SetFact(oper, "a2", Nov, 50m)).EnsureSuccessStatusCode();
        Assert.Equal(155892, await PlannedHeat("a2", "2026-10"));
    }

    [Fact]
    public async Task NewAbonent_HasFactFromRegistrationMonth()
    {
        var oper = await Login();
        var created = await oper.PostAsJsonAsync("/api/abonents", new { kind = "person", name = "Новый Абонент", address = "ул. Тест, 1" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var details = await oper.GetFromJsonAsync<JsonElement>($"/api/abonents/{id}/details");
        Assert.Equal(Nov, details.GetProperty("facts")[0].GetProperty("fromPeriod").GetString());
        (await SetFact(oper, id!, Nov, 30m, 1)).EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData(-1, 1, "Причина")]
    [InlineData(1.23456, 1, "Причина")]
    [InlineData(10, -1, "Причина")]
    [InlineData(10, 1, " ")]
    public async Task BadFact_Is422(decimal area, int residents, string reason)
    {
        var oper = await Login();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetFact(oper, "a2", Dec, area, residents, reason)).StatusCode);
    }

    [Fact]
    public async Task Note_AnyEmployeeWrites_ClearsAndIsJournaled()
    {
        var cashier = await Login("kassa1");
        (await cashier.PostAsJsonAsync("/api/abonents/a2/note", new { note = "  Вносить по актам сверки " })).EnsureSuccessStatusCode();
        Assert.Equal("Вносить по актам сверки", (await cashier.GetFromJsonAsync<JsonElement>("/api/abonents/a2/details")).GetProperty("note").GetString());

        (await cashier.PostAsJsonAsync("/api/abonents/a2/note", new { note = " " })).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await cashier.GetFromJsonAsync<JsonElement>("/api/abonents/a2/details")).GetProperty("note").ValueKind);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await cashier.PostAsJsonAsync("/api/abonents/a2/note", new { note = new string('х', 2001) })).StatusCode);

        await using var db = Db();
        Assert.Contains(await db.Journal.Where(item => item.AbonentId == "a2").ToListAsync(), item => item.Action == "Заметка" && item.Details.Contains("актам"));
    }

    [Fact]
    public async Task Consent_DefaultsToUnknown_RecordsWhen_AndOnlyChangesJournal()
    {
        var oper = await Login();
        var details = await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a2/details");
        Assert.Equal(JsonValueKind.Null, details.GetProperty("consent").ValueKind);

        (await oper.PostAsJsonAsync("/api/abonents/a2/legal", new { consent = true, ownershipDocument = "Выписка ЕГРН № 1" })).EnsureSuccessStatusCode();
        details = await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a2/details");
        Assert.True(details.GetProperty("consent").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, details.GetProperty("consentWhen").ValueKind);
        Assert.Equal("Выписка ЕГРН № 1", details.GetProperty("ownershipDocument").GetString());

        (await oper.PostAsJsonAsync("/api/abonents/a2/legal", new { consent = true, ownershipDocument = "Выписка ЕГРН № 1" })).EnsureSuccessStatusCode();
        await using var db = Db();
        Assert.Single(await db.Journal.Where(item => item.AbonentId == "a2" && item.Action == "Данные абонента").ToListAsync());
    }

    [Fact]
    public async Task Residents_AddAndEnd_KeepHistory_AndValidateDates()
    {
        var oper = await Login();
        (await oper.PostAsJsonAsync("/api/abonents/a2/residents", new { name = "Жилец Первый", fromDate = "2026-01-10" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync("/api/abonents/a2/residents", new { name = " " })).StatusCode);
        var details = await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a2/details");
        var resident = details.GetProperty("residents")[0];
        Assert.Equal("10.01.2026", resident.GetProperty("fromDate").GetString());
        var id = resident.GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync($"/api/abonents/a2/residents/{id}/end", new { toDate = "2025-01-01" })).StatusCode);
        (await oper.PostAsJsonAsync($"/api/abonents/a2/residents/{id}/end", new { toDate = "2026-06-01" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await oper.PostAsJsonAsync($"/api/abonents/a2/residents/{id}/end", new { toDate = "2026-06-02" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await oper.PostAsJsonAsync($"/api/abonents/a3/residents/{id}/end", new { toDate = "2026-06-02" })).StatusCode);
        Assert.Equal("01.06.2026", (await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a2/details")).GetProperty("residents")[0].GetProperty("toDate").GetString());
    }

    [Fact]
    public async Task ResidentsCheck_FlagsListDifferentFromCalculation()
    {
        var oper = await Login();
        var buh = await Login("buh1");
        static async Task<int> Rows(HttpClient client)
        {
            var checks = await client.GetFromJsonAsync<JsonElement>("/api/checks");
            return checks.GetProperty("groups").EnumerateArray().First(group => group.GetProperty("id").GetString() == "residents").GetProperty("rows").GetArrayLength();
        }
        // Пустой список не ошибка: его просто ещё не вели
        Assert.Equal(0, await Rows(buh));
        (await oper.PostAsJsonAsync("/api/abonents/a2/residents", new { name = "Один Жилец" })).EnsureSuccessStatusCode();
        Assert.Equal(1, await Rows(buh));
        (await oper.PostAsJsonAsync("/api/abonents/a2/residents", new { name = "Второй Жилец" })).EnsureSuccessStatusCode();
        Assert.Equal(0, await Rows(buh));
    }

    [Fact]
    public async Task Roles_CashierCannotChangeDataAffectingMoney()
    {
        var cashier = await Login("kassa1");
        Assert.Equal(HttpStatusCode.Forbidden, (await SetFact(cashier, "a2", Dec, 10m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/abonents/a2/legal", new { consent = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/abonents/a2/residents", new { name = "Х" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/abonents/a2/contact", new { phone = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/abonents/a2/details")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/abonents/a2/details")).StatusCode);
    }

    [Fact]
    public async Task Contact_Updates()
    {
        var oper = await Login();
        (await oper.PostAsJsonAsync("/api/abonents/a2/contact", new { phone = " +7 900 000-00-99 " })).EnsureSuccessStatusCode();
        Assert.Equal("+7 900 000-00-99", (await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a2")).GetProperty("phone").GetString());
    }

    [Fact]
    public async Task ServiceLine_ShowsPriceOfWorkMonth()
    {
        var oper = await Login();
        var abonent = await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a1");
        var services = abonent.GetProperty("services").EnumerateArray().ToList();
        Assert.Equal(35.43m, services.First(line => line.GetProperty("id").GetString() == "heat").GetProperty("price").GetDecimal());
        Assert.Equal(JsonValueKind.Null, services.First(line => line.GetProperty("id").GetString() == "penalty").GetProperty("price").ValueKind);
    }
}
