using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

/// <summary>Проверки, которые имеют смысл только на настоящем PostgreSQL.</summary>
public sealed class PostgresTests : IDisposable
{
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> Login(string login)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = "test-password" })).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<long> Balance(HttpClient client, string id)
    {
        var abonent = await client.GetFromJsonAsync<JsonElement>($"/api/abonents/{id}");
        var debt = abonent.GetProperty("services").EnumerateArray().Sum(line => line.GetProperty("debt").GetInt64());
        return abonent.GetProperty("credit").GetInt64() - debt;
    }

    private static HttpRequestMessage Pay(string key, long amount)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/cash/payments")
        {
            Content = JsonContent.Create(new { abonentId = "a3", source = "cash-person", allocations = new[] { new { serviceId = "heat", amount } } }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    /// <summary>Как кассир: при конфликте повторяет с тем же ключом.</summary>
    private static async Task<HttpStatusCode> PayWithRetry(HttpClient client, string key, long amount)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = Pay(key, amount);
            var response = await client.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.Conflict || attempt > 50) return response.StatusCode;
            await Task.Delay(20 + Random.Shared.Next(50));
        }
    }

    [Fact]
    public async Task ParallelPayments_NoMoneyIsLost()
    {
        var client = await Login("kassa1");
        var before = await Balance(client, "a3");

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => PayWithRetry(client, $"parallel-{index}", 1000)));

        Assert.All(results, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(before + 20 * 1000, await Balance(client, "a3"));
    }

    [Fact]
    public async Task ParallelSameKey_CreatesOnePayment()
    {
        var client = await Login("kassa1");
        var before = await Balance(client, "a3");

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PayWithRetry(client, "same-parallel-key", 1000)));

        Assert.All(results, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(before + 1000, await Balance(client, "a3"));
    }

    [Fact]
    public async Task ParallelRefunds_OnlyOneSucceeds()
    {
        var client = await Login("kassa1");
        var before = await Balance(client, "a3");
        var body = new { abonentId = "a3", paymentId = "a3-p1", reason = "Тест одновременного возврата" };

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.PostAsJsonAsync("/api/cash/refunds", body)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(before - 150000, await Balance(client, "a3"));
    }

    [Fact]
    public async Task FailedPayment_RollsBackReading()
    {
        var client = await Login("kassa1");
        var before = await Balance(client, "a1");
        var response = await client.PostAsJsonAsync("/api/cash/payments", new
        {
            abonentId = "a1", source = "cash-person", reading = 500m,
            allocations = new[] { new { serviceId = "heat", amount = -5L } },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(before, await Balance(client, "a1"));
        var abonent = await client.GetFromJsonAsync<JsonElement>("/api/abonents/a1");
        var water = abonent.GetProperty("services").EnumerateArray().First(line => line.GetProperty("id").GetString() == "water");
        Assert.Equal(412m, water.GetProperty("meter").GetProperty("lastReading").GetDecimal());
    }

    [Fact]
    public async Task Decimals_KeepPrecisionInDatabase()
    {
        var client = await Login("oper1");
        (await client.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 332.125m })).EnsureSuccessStatusCode();
        var abonent = await client.GetFromJsonAsync<JsonElement>("/api/abonents/a7");
        var water = abonent.GetProperty("services").EnumerateArray().First(line => line.GetProperty("id").GetString() == "water");
        Assert.Equal(332.125m, water.GetProperty("meter").GetProperty("lastReading").GetDecimal());
        // 1,125 м³ × 52 ₽ = 58,50 ₽
        Assert.Equal(5850, water.GetProperty("debt").GetInt64());
    }

    [Fact]
    public async Task CashReport_UsesTimestampsCorrectly()
    {
        var client = await Login("kassa1");
        using (var request = Pay("report-key", 1000)) (await client.SendAsync(request)).EnsureSuccessStatusCode();
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var report = await client.GetFromJsonAsync<JsonElement>($"/api/reports/run?reportId=cash-turnover&period=Свои даты&cut=Дни&who=Все&from={today}&to={today}");
        var row = Assert.Single(report.GetProperty("rows").EnumerateArray());
        Assert.Equal(today, row.GetProperty("name").GetString());
        Assert.Equal(1000, row.GetProperty("values")[3].GetInt64());
    }

    [Fact]
    public async Task CreateAdmin_ValidatesAndAllowsLogin()
    {
        await using var db = new EmirDb(new DbContextOptionsBuilder<EmirDb>().UseNpgsql(_factory.ConnectionString).Options);
        _ = _factory.Server; // Сервер приложения применяет миграции при старте

        await Assert.ThrowsAsync<ApiError>(() => AdminTools.CreateAdmin(db, "boss", "Начальник", "short"));
        await AdminTools.CreateAdmin(db, "boss", "Начальник", "long-enough-password");
        await Assert.ThrowsAsync<ApiError>(() => AdminTools.CreateAdmin(db, "boss", "Начальник", "long-enough-password"));

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login = "boss", password = "long-enough-password" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/sections")).StatusCode);
    }

    [Fact]
    public async Task Schema_MatchesMigrations()
    {
        await using var db = new EmirDb(new DbContextOptionsBuilder<EmirDb>().UseNpgsql(_factory.ConnectionString).Options);
        _ = _factory.Server;
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
