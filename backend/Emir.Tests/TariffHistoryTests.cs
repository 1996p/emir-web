using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

public sealed class TariffHistoryTests : IDisposable
{
    private const string Open = "2026-11";
    private const string Next = "2026-12";
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> Login(string login)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = "test-password" })).EnsureSuccessStatusCode();
        return client;
    }

    private EmirDb Db() => new(new DbContextOptionsBuilder<EmirDb>().UseNpgsql(_factory.ConnectionString).Options);

    private static Task<HttpResponseMessage> SetPrice(HttpClient client, string tariff, string from, decimal price) =>
        client.PostAsJsonAsync($"/api/tariffs/{tariff}/prices", new { fromPeriod = from, price, reason = "Приказ" });

    private static Task<HttpResponseMessage> SetTerm(HttpClient client, string abonent, string service, string from, string? tariffId, string state) =>
        client.PostAsJsonAsync($"/api/abonents/{abonent}/services/{service}/terms", new { fromPeriod = from, tariffId, state, reason = "Заявление" });

    private async Task<PlannedCharge> PlanFor(string abonentId, string serviceId, string period)
    {
        _ = _factory.Server;
        await using var db = Db();
        var abonents = await db.Abonents.Include(item => item.Services).Where(item => item.Id == abonentId).ToListAsync();
        return Assert.Single(await ChargingService.Plan(db, abonents, period), item => item.Line.ServiceId == serviceId);
    }

    [Fact]
    public void PriceOn_TakesLatestStartedPrice()
    {
        TariffPrice Price(string from, decimal value) => new() { TariffId = "t", FromPeriod = from, Price = value, Who = "" };
        var prices = new[] { Price("2026-07", 2), Price("2000-01", 1), Price("2027-01", 3), new TariffPrice { TariffId = "other", FromPeriod = "2026-10", Price = 9, Who = "" } };

        Assert.Null(TariffHistory.PriceOn(prices, "t", "1999-12"));
        Assert.Equal(1, TariffHistory.PriceOn(prices, "t", "2026-06")!.Price);
        Assert.Equal(2, TariffHistory.PriceOn(prices, "t", "2026-07")!.Price);
        Assert.Equal(2, TariffHistory.PriceOn(prices, "t", "2026-12")!.Price);
        Assert.Equal(3, TariffHistory.PriceOn(prices, "t", "2027-01")!.Price);
    }

    [Fact]
    public async Task Plan_UsesPriceOfRequestedMonth()
    {
        var client = await Login("buh1");
        (await SetPrice(client, "heat-norm", Next, 50m)).EnsureSuccessStatusCode();

        var open = await PlanFor("a1", "heat", Open);
        var next = await PlanFor("a1", "heat", Next);

        Assert.Equal(35.43m, open.Price);
        Assert.Equal(50m, next.Price);
        Assert.True(next.Result.Amount > open.Result.Amount);
    }

    [Fact]
    public async Task Plan_RecalculatesPastMonthAtItsOldPrice()
    {
        _ = _factory.Server;
        await using (var db = Db())
        {
            db.TariffPrices.Add(new TariffPrice { TariffId = "heat-norm", FromPeriod = "2026-06", Price = 40m, Who = "Тест", AtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.Equal(35.43m, (await PlanFor("a1", "heat", "2026-05")).Price);
        Assert.Equal(40m, (await PlanFor("a1", "heat", "2026-06")).Price);
    }

    [Fact]
    public async Task ChargeEntry_KeepsPriceAfterFutureChange()
    {
        var client = await Login("buh1");
        (await client.PostAsync("/api/abonents/a2/recharge", null)).EnsureSuccessStatusCode();
        (await SetPrice(client, "heat-norm", Next, 99m)).EnsureSuccessStatusCode();

        var charges = await client.GetFromJsonAsync<JsonElement>("/api/abonents/a2/charges");
        var entry = charges.EnumerateArray().First(item => item.GetProperty("period").GetString() == Open);
        Assert.Equal(35.43m, entry.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task PriceInOpenMonth_AppliesToReadingAndChargeTogether()
    {
        var buh = await Login("buh1");
        (await SetPrice(buh, "water-meter", Open, 60m)).EnsureSuccessStatusCode();
        var oper = await Login("oper1");
        (await oper.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 332m })).EnsureSuccessStatusCode();

        var abonent = await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a7");
        var water = abonent.GetProperty("services").EnumerateArray().First(line => line.GetProperty("id").GetString() == "water");
        Assert.Equal(6000, water.GetProperty("debt").GetInt64());
        Assert.Equal(6000m, water.GetProperty("meter").GetProperty("tariff").GetDecimal());
    }

    [Fact]
    public async Task ChangesForStartedOrPastMonth_AreRefused()
    {
        var buh = await Login("buh1");
        Assert.Equal(HttpStatusCode.Conflict, (await SetPrice(buh, "heat-norm", "2026-10", 50m)).StatusCode);

        (await buh.PostAsync("/api/abonents/a2/recharge", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await SetPrice(buh, "heat-norm", Open, 50m)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetTerm(buh, "a2", "heat", Open, "heat-norm", "Приостановлена")).StatusCode);

        // Следующий месяц и другая услуга не затронуты
        (await SetPrice(buh, "heat-norm", Next, 50m)).EnsureSuccessStatusCode();
        (await SetPrice(buh, "water-meter", Open, 53m)).EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("2026-13", 10, "Приказ")]
    [InlineData("abc", 10, "Приказ")]
    [InlineData("2026-12", 0, "Приказ")]
    [InlineData("2026-12", -1, "Приказ")]
    [InlineData("2026-12", 10, " ")]
    public async Task BadPriceRequest_Returns422(string from, decimal price, string reason)
    {
        var client = await Login("buh1");
        var response = await client.PostAsJsonAsync("/api/tariffs/heat-norm/prices", new { fromPeriod = from, price, reason });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Prices_RequireAccountantOrAdmin()
    {
        foreach (var login in new[] { "kassa1", "oper1" })
            Assert.Equal(HttpStatusCode.Forbidden, (await SetPrice(await Login(login), "heat-norm", Next, 50m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Login("kassa1")).GetAsync("/api/tariffs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetTerm(await Login("kassa1"), "a2", "heat", Next, null, "Работает")).StatusCode);
    }

    [Fact]
    public async Task CancelPrice_RestoresPreviousAndKeepsFirst()
    {
        var client = await Login("buh1");
        (await SetPrice(client, "heat-norm", Next, 50m)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/tariffs/heat-norm/prices/{Next}/cancel", new { reason = "Ошибка" })).EnsureSuccessStatusCode();
        Assert.Equal(35.43m, (await PlanFor("a1", "heat", Next)).Price);

        var history = await client.GetFromJsonAsync<JsonElement>("/api/tariffs");
        var heat = history.GetProperty("tariffs").EnumerateArray().First(item => item.GetProperty("id").GetString() == "heat-norm");
        Assert.Single(heat.GetProperty("prices").EnumerateArray());

        // Первую цену прошлого убрать нельзя: сначала срабатывает запрет на прошлое
        var first = await client.PostAsJsonAsync($"/api/tariffs/heat-norm/prices/{TariffHistory.Beginning}/cancel", new { reason = "Ошибка" });
        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
    }

    [Fact]
    public async Task SuspendedService_IsNotChargedFromItsMonth_ButReadingIsKept()
    {
        var client = await Login("buh1");
        (await SetTerm(client, "a7", "water", Open, "water-meter", "Приостановлена")).EnsureSuccessStatusCode();

        var abonent = await client.GetFromJsonAsync<JsonElement>("/api/abonents/a7");
        var water = abonent.GetProperty("services").EnumerateArray().First(line => line.GetProperty("id").GetString() == "water");
        Assert.Equal("Приостановлена", water.GetProperty("state").GetString());

        var oper = await Login("oper1");
        (await oper.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 340m })).EnsureSuccessStatusCode();
        abonent = await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a7");
        water = abonent.GetProperty("services").EnumerateArray().First(line => line.GetProperty("id").GetString() == "water");
        Assert.Equal(0, water.GetProperty("debt").GetInt64());
        Assert.Equal(340m, water.GetProperty("meter").GetProperty("lastReading").GetDecimal());
    }

    [Fact]
    public async Task FutureTerm_DoesNotChangeOpenMonth()
    {
        var client = await Login("buh1");
        (await SetTerm(client, "a2", "heat", Next, null, "Расторгнута")).EnsureSuccessStatusCode();

        var abonent = await client.GetFromJsonAsync<JsonElement>("/api/abonents/a2");
        var heat = abonent.GetProperty("services").EnumerateArray().First();
        Assert.Equal("Работает", heat.GetProperty("state").GetString());
        Assert.NotNull((await PlanFor("a2", "heat", Open)).Price);

        await using var db = Db();
        var abonents = await db.Abonents.Include(item => item.Services).Where(item => item.Id == "a2").ToListAsync();
        Assert.Empty(await ChargingService.Plan(db, abonents, Next));

        var terms = await client.GetFromJsonAsync<JsonElement>("/api/abonents/a2/services/heat/terms");
        Assert.Equal(Next, terms[0].GetProperty("fromPeriod").GetString());
        Assert.Equal(2, terms.GetArrayLength());
    }

    [Fact]
    public async Task NewService_GetsTariffFromTerm_AndIsCharged()
    {
        var oper = await Login("oper1");
        var created = await oper.PostAsJsonAsync("/api/abonents", new { kind = "person", name = "Новый Абонент Тестович", place = "р.п. Чистоозерное", street = "ул. Школьная", house = "12", flat = "98" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var before = await PlanFor(id, "heat", Open);
        Assert.Equal("Не выбран тариф", before.Result.Error);

        (await SetTerm(oper, id, "heat", Open, "heat-norm", "Работает")).EnsureSuccessStatusCode();
        Assert.Equal(35.43m, (await PlanFor(id, "heat", Open)).Price);
        Assert.False((await SetTerm(oper, id, "heat", "2026-10", "heat-norm", "Работает")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Journal_RecordsPriceChange()
    {
        var client = await Login("admin");
        (await SetPrice(client, "heat-norm", Next, 50m)).EnsureSuccessStatusCode();
        var sections = await client.GetFromJsonAsync<JsonElement>("/api/admin/sections");
        var audit = sections.EnumerateArray().First(item => item.GetProperty("id").GetString() == "audit").GetProperty("rows");
        Assert.Contains(audit.EnumerateArray(), row => row[2].GetString() == "Цена тарифа" && row[3].GetString()!.Contains("50") && row[4].GetString() == "Приказ");
    }

    [Fact]
    public async Task ParallelPriceChanges_DoNotBreakHistory()
    {
        var client = await Login("buh1");
        var responses = await Task.WhenAll(Enumerable.Range(1, 6).Select(index => SetPrice(client, "heat-norm", Next, 40m + index)));
        Assert.All(responses, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict }));
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        await using var db = Db();
        Assert.Equal(1, await db.TariffPrices.CountAsync(price => price.TariffId == "heat-norm" && price.FromPeriod == Next));
    }
}
