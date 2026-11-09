using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

/// <summary>Возможности старой системы: общая площадь, флаги лицевого счёта, офис и период платежа, показания за выбранный месяц.</summary>
public sealed class OldSystemParityTests : IDisposable
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

    [Fact]
    public async Task TotalArea_NeedsReason_AndDoesNotChangeHeatedArea()
    {
        var oper = await Login("oper1");
        var area = (await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a2")).GetProperty("area").GetDecimal();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync("/api/abonents/a2/total-area", new { totalArea = 60.5m, reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync("/api/abonents/a2/total-area", new { totalArea = -1m, reason = "техпаспорт" })).StatusCode);
        (await oper.PostAsJsonAsync("/api/abonents/a2/total-area", new { totalArea = 60.5m, reason = "техпаспорт" })).EnsureSuccessStatusCode();
        var details = await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a2/details");
        Assert.Equal(60.5m, details.GetProperty("totalArea").GetDecimal());
        Assert.Equal(area, (await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a2")).GetProperty("area").GetDecimal());
    }

    [Fact]
    public async Task AccountFlags_OnlyAccountant_AndDuplicateFlagHidesFromCheck()
    {
        var flags = new { hideInPlatosfera = true, hideIn1C = true, ignoreDuplicateCheck = true };
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Login("oper1")).PostAsJsonAsync("/api/abonents/a8/account-flags", flags)).StatusCode);
        await using (var db = Db())
        {
            // a8 по адресу a7 — дубль по адресу
            var a7 = await db.Abonents.FirstAsync(item => item.Id == "a7");
            var a8 = await db.Abonents.FirstAsync(item => item.Id == "a8");
            (a8.Place, a8.Street, a8.Flat) = (a7.Place, a7.Street, a7.Flat);
            await db.SaveChangesAsync();
        }
        var buh = await Login("buh1");
        async Task<int> SameAddress()
        {
            var checks = await buh.GetFromJsonAsync<JsonElement>("/api/checks");
            var group = checks.GetProperty("groups").EnumerateArray().First(item => item.GetProperty("id").GetString() == "duplicates");
            return group.GetProperty("rows").EnumerateArray().SelectMany(row => row.GetProperty("cells").EnumerateArray()).Count(cell => cell.GetString() == "Одинаковый адрес");
        }
        Assert.Equal(2, await SameAddress());
        (await buh.PostAsJsonAsync("/api/abonents/a8/account-flags", flags)).EnsureSuccessStatusCode();
        Assert.Equal(0, await SameAddress());
        var details = await buh.GetFromJsonAsync<JsonElement>("/api/abonents/a8/details");
        Assert.True(details.GetProperty("hideInPlatosfera").GetBoolean());
        Assert.True(details.GetProperty("hideIn1C").GetBoolean());
        Assert.True(details.GetProperty("ignoreDuplicateCheck").GetBoolean());
    }

    [Fact]
    public async Task Payment_StoresOfficeAndPeriod_RejectsUnknownOfficeAndFutureMonth()
    {
        var kassa = await Login("kassa1");
        var offices = await kassa.GetFromJsonAsync<string[]>("/api/cash/offices");
        Assert.Contains("Чистоозерное. Касса. ФЛ.", offices!);
        var period = (await kassa.GetFromJsonAsync<JsonElement>("/api/period/current")).GetProperty("period").GetString()!;
        object Pay(string? office, string? month) => new { abonentId = "a2", source = "cash-person", allocations = new[] { new { serviceId = "heat", amount = 1000L } }, office, period = month };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await kassa.PostAsJsonAsync("/api/cash/payments", Pay("Нет такого офиса", null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await kassa.PostAsJsonAsync("/api/cash/payments", Pay(null, "2099-01"))).StatusCode);
        (await kassa.PostAsJsonAsync("/api/cash/payments", Pay("Чистоозерное. Касса. ФЛ.", period))).EnsureSuccessStatusCode();
        await using var db = Db();
        var payment = await db.Payments.OrderByDescending(item => item.AtUtc).FirstAsync(item => item.AbonentId == "a2");
        Assert.Equal("Чистоозерное. Касса. ФЛ.", payment.Office);
        Assert.Equal(period, payment.Period);
    }

    [Fact]
    public async Task Readings_PastMonthOpens_BadMonthRejected()
    {
        var oper = await Login("oper1");
        var house = Uri.EscapeDataString((await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a1")).GetProperty("houseKey").GetString()!);
        var current = await oper.GetFromJsonAsync<JsonElement>($"/api/readings/rows?house={house}");
        var past = await oper.GetFromJsonAsync<JsonElement>($"/api/readings/rows?house={house}&period=2020-01");
        Assert.Equal(current.GetArrayLength(), past.GetArrayLength());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.GetAsync($"/api/readings/rows?house={house}&period=октябрь")).StatusCode);
    }
}
