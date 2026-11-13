using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

/// <summary>Перенос денег: платёж на другой адрес и между услугами одного абонента.</summary>
public sealed class MoneyTransferTests : IDisposable
{
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> Login(string login)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = "test-password" })).EnsureSuccessStatusCode();
        return client;
    }

    private EmirDb Db() => new(new DbContextOptionsBuilder<EmirDb>().UseNpgsql(_factory.ConnectionString).Options);

    private static long Balance(JsonElement abonent) =>
        abonent.GetProperty("credit").GetInt64() - abonent.GetProperty("services").EnumerateArray().Sum(line => line.GetProperty("debt").GetInt64());

    /// <summary>Принимает оплату в кассе и возвращает этот платёж.</summary>
    private async Task<Payment> Pay(HttpClient kassa, string abonentId, long amount)
    {
        var abonent = await kassa.GetFromJsonAsync<JsonElement>($"/api/abonents/{abonentId}");
        var service = abonent.GetProperty("services")[0].GetProperty("id").GetString();
        (await kassa.PostAsJsonAsync("/api/cash/payments", new { abonentId, source = "cash-person", allocations = new[] { new { serviceId = service, amount } } })).EnsureSuccessStatusCode();
        await using var db = Db();
        return await db.Payments.OrderByDescending(item => item.AtUtc).FirstAsync(item => item.AbonentId == abonentId);
    }

    private static object MoveAddress(string paymentId, long amount, string? target) =>
        new { abonentId = "a2", typeId = "move-address", amount, reason = "Ошиблись адресом", paymentId, targetAbonentId = target };

    [Fact]
    public async Task MoveAddress_WholePayment_MovesMoneyAndPayment()
    {
        var kassa = await Login("kassa1");
        var payment = await Pay(kassa, "a2", 50000);
        var before2 = Balance(await kassa.GetFromJsonAsync<JsonElement>("/api/abonents/a2"));
        var before7 = Balance(await kassa.GetFromJsonAsync<JsonElement>("/api/abonents/a7"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await kassa.PostAsJsonAsync("/api/corrections", MoveAddress(payment.Id, 50000, null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await kassa.PostAsJsonAsync("/api/corrections", MoveAddress(payment.Id, 50000, "a2"))).StatusCode);
        (await kassa.PostAsJsonAsync("/api/corrections", MoveAddress(payment.Id, 50000, "a7"))).EnsureSuccessStatusCode();

        Assert.Equal(before2 - 50000, Balance(await kassa.GetFromJsonAsync<JsonElement>("/api/abonents/a2")));
        Assert.Equal(before7 + 50000, Balance(await kassa.GetFromJsonAsync<JsonElement>("/api/abonents/a7")));
        await using var db = Db();
        var moved = await db.Payments.FirstAsync(item => item.Id == payment.Id);
        Assert.Equal(("a7", 50000L, "Проведена"), (moved.AbonentId, moved.Amount, moved.Status));
        // Деньги не появились и не пропали: сумма проводок переноса по обоим абонентам равна нулю
        Assert.Equal(0, await db.LedgerEntries.Where(entry => entry.Kind == "correction").SumAsync(entry => entry.CreditDelta - entry.DebtDelta));
    }

    [Fact]
    public async Task MoveAddress_PartOfPayment_SplitsPaymentKeepingCashTotal()
    {
        var kassa = await Login("kassa1");
        var payment = await Pay(kassa, "a2", 50000);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await kassa.PostAsJsonAsync("/api/corrections", MoveAddress(payment.Id, 60000, "a7"))).StatusCode);
        (await kassa.PostAsJsonAsync("/api/corrections", MoveAddress(payment.Id, 20000, "a7"))).EnsureSuccessStatusCode();

        await using var db = Db();
        var rest = await db.Payments.FirstAsync(item => item.Id == payment.Id);
        var part = await db.Payments.SingleAsync(item => item.AbonentId == "a7" && item.AtUtc == payment.AtUtc);
        Assert.Equal(30000, rest.Amount);
        Assert.Equal((20000L, payment.Cashier, payment.Source), (part.Amount, part.Cashier, part.Source));
        Assert.Contains(await db.Journal.Where(item => item.AbonentId == "a7").Select(item => item.Details).ToListAsync(), text => text.Contains("с ЛС"));
    }

    [Fact]
    public async Task MoveService_BetweenServicesAndCredit_KeepsBalance()
    {
        var buh = await Login("buh1");
        var abonent = await buh.GetFromJsonAsync<JsonElement>("/api/abonents/a1");
        var services = abonent.GetProperty("services").EnumerateArray().Select(line => line.GetProperty("id").GetString()!).ToList();
        Assert.True(services.Count >= 2);
        static long Debt(JsonElement card, string id) => card.GetProperty("services").EnumerateArray().First(line => line.GetProperty("id").GetString() == id).GetProperty("debt").GetInt64();
        var before = Balance(abonent);

        object Move(string from, string to, long amount) => new { abonentId = "a1", typeId = "move-service", amount, reason = "Оплатили не ту услугу", fromServiceId = from, toServiceId = to };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await buh.PostAsJsonAsync("/api/corrections", Move(services[0], services[0], 1000))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await buh.PostAsJsonAsync("/api/corrections", Move("credit", services[0], abonent.GetProperty("credit").GetInt64() + 1))).StatusCode);

        (await buh.PostAsJsonAsync("/api/corrections", Move(services[0], "credit", 1000))).EnsureSuccessStatusCode();
        var after = await buh.GetFromJsonAsync<JsonElement>("/api/abonents/a1");
        Assert.Equal(Debt(abonent, services[0]) + 1000, Debt(after, services[0]));
        Assert.Equal(abonent.GetProperty("credit").GetInt64() + 1000, after.GetProperty("credit").GetInt64());
        Assert.Equal(before, Balance(after));

        (await buh.PostAsJsonAsync("/api/corrections", Move("credit", services[1], 1000))).EnsureSuccessStatusCode();
        Assert.Equal(before, Balance(await buh.GetFromJsonAsync<JsonElement>("/api/abonents/a1")));
    }
}
