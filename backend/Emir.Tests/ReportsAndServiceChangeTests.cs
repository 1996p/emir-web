using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Emir.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

/// <summary>Разрезы отчётов, которых не было, и смена услуги на одном ЛС.</summary>
public sealed class ReportsAndServiceChangeTests : IDisposable
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

    private static async Task<JsonElement> Report(HttpClient client, string reportId, string cut)
    {
        var report = await client.GetFromJsonAsync<JsonElement>($"/api/reports/run?reportId={reportId}&period={Uri.EscapeDataString("Текущий месяц")}&cut={Uri.EscapeDataString(cut)}&who={Uri.EscapeDataString("Все")}");
        Assert.True(report.GetProperty("available").GetBoolean(), report.GetProperty("message").GetString());
        return report;
    }

    private static List<string> Names(JsonElement report) => report.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("name").GetString()!).ToList();

    [Fact]
    public async Task MobileCashierDebtors_OnlyDebtors_SortedByAddress()
    {
        var buh = await Login("buh1");
        var report = await Report(buh, "debtors", "Для мобильного кассира");
        var names = Names(report);
        var abonents = await buh.GetFromJsonAsync<JsonElement>("/api/abonents");
        var debtors = abonents.EnumerateArray().Count(abonent => abonent.GetProperty("services").EnumerateArray().Any(line => line.GetProperty("debt").GetInt64() > 0));
        Assert.Equal(debtors, names.Count);
        Assert.All(names, name => Assert.Contains(" · ЛС ", name));
        Assert.All(report.GetProperty("rows").EnumerateArray(), row => Assert.True(row.GetProperty("values")[2].GetInt64() > 0));
    }

    [Fact]
    public async Task CashierRegistry_For1C_ListsEachPayment_GorodOnlyRegistryPayments()
    {
        var kassa = await Login("kassa1");
        var account = (await kassa.GetFromJsonAsync<JsonElement>("/api/abonents/a2")).GetProperty("account").GetString()!;
        (await kassa.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a2", source = "cash-person", allocations = new[] { new { serviceId = "heat", amount = 1234L } } })).EnsureSuccessStatusCode();
        var buh = await Login("buh1");
        var oneC = await Report(buh, "cashier", "Для 1С");
        Assert.Contains(oneC.GetProperty("rows").EnumerateArray(), row => row.GetProperty("name").GetString()!.Contains($"ЛС {account}") && row.GetProperty("values")[0].GetInt64() == 1234);
        Assert.Empty(Names(await Report(buh, "cashier", "Система Город")));

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes($"{account};5,00\n")), "file", "gorod.csv");
        var upload = await buh.PostAsync("/api/exchange/upload", form);
        upload.EnsureSuccessStatusCode();
        var batch = await upload.Content.ReadFromJsonAsync<JsonElement>();
        (await buh.PostAsync($"/api/exchange/batches/{batch.GetProperty("id").GetString()}/post", null)).EnsureSuccessStatusCode();
        var gorod = await Report(buh, "cashier", "Система Город");
        var row = Assert.Single(gorod.GetProperty("rows").EnumerateArray());
        Assert.Equal(500, row.GetProperty("values")[0].GetInt64());
    }

    [Fact]
    public async Task Median_ListsMetersWithoutReading_UntilReadingEntered()
    {
        var buh = await Login("buh1");
        var oper = await Login("oper1");
        var abonent = await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a1");
        var meterLine = abonent.GetProperty("services").EnumerateArray().First(line => line.TryGetProperty("meter", out var meter) && meter.ValueKind == JsonValueKind.Object);
        var account = abonent.GetProperty("account").GetString()!;
        var service = meterLine.GetProperty("name").GetString()!;
        Assert.Contains(Names(await Report(buh, "median", "Абоненты")), name => name.StartsWith(account + " ") && name.Contains(service));

        var last = meterLine.GetProperty("meter").GetProperty("lastReading").GetDecimal();
        (await oper.PostAsJsonAsync("/api/readings", new { abonentId = "a1", serviceId = meterLine.GetProperty("id").GetString(), value = last + 1 })).EnsureSuccessStatusCode();
        Assert.DoesNotContain(Names(await Report(buh, "median", "Абоненты")), name => name.StartsWith(account + " ") && name.Contains(service));
    }

    [Fact]
    public async Task ReplaceService_MovesDebtAndClosesOldService()
    {
        var oper = await Login("oper1");
        var abonent = await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a1");
        var services = abonent.GetProperty("services").EnumerateArray().Where(line => line.GetProperty("id").GetString() != "penalty").ToList();
        Assert.True(services.Count >= 2);
        string from = services[0].GetProperty("id").GetString()!, to = services[1].GetProperty("id").GetString()!;
        long fromDebt = services[0].GetProperty("debt").GetInt64(), toDebt = services[1].GetProperty("debt").GetInt64();
        static long Balance(JsonElement card) => card.GetProperty("credit").GetInt64() - card.GetProperty("services").EnumerateArray().Sum(line => line.GetProperty("debt").GetInt64());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync($"/api/abonents/a1/services/{from}/replace", new { targetServiceId = to, reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync($"/api/abonents/a1/services/{from}/replace", new { targetServiceId = from, reason = "заявление" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Login("kassa1")).PostAsJsonAsync($"/api/abonents/a1/services/{from}/replace", new { targetServiceId = to, reason = "заявление" })).StatusCode);
        (await oper.PostAsJsonAsync($"/api/abonents/a1/services/{from}/replace", new { targetServiceId = to, reason = "заявление" })).EnsureSuccessStatusCode();

        var after = await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a1");
        JsonElement Line(string id) => after.GetProperty("services").EnumerateArray().First(line => line.GetProperty("id").GetString() == id);
        Assert.Equal(0, Line(from).GetProperty("debt").GetInt64());
        Assert.Equal("Расторгнута", Line(from).GetProperty("state").GetString());
        Assert.Equal(toDebt + fromDebt, Line(to).GetProperty("debt").GetInt64());
        Assert.Equal(Balance(abonent), Balance(after));
        // На закрытую услугу перейти нельзя
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync($"/api/abonents/a1/services/{to}/replace", new { targetServiceId = from, reason = "заявление" })).StatusCode);
        await using var db = Db();
        Assert.True(await db.ServiceTerms.AnyAsync(term => term.AbonentId == "a1" && term.ServiceId == from && term.State == "Расторгнута"));
    }
}
