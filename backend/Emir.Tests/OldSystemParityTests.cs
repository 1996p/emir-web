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

    private static async Task<List<string>> ReportNames(HttpClient client, string query)
    {
        var report = await client.GetFromJsonAsync<JsonElement>("/api/reports/run?" + query);
        Assert.True(report.GetProperty("available").GetBoolean(), report.GetProperty("message").GetString());
        return report.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("name").GetString()!).ToList();
    }

    [Fact]
    public async Task PlatosferaRegistry_SkipsFlaggedAccount_OtherBanksKeepIt()
    {
        var buh = await Login("buh1");
        var account = (await buh.GetFromJsonAsync<JsonElement>("/api/abonents/a8")).GetProperty("account").GetString()!;
        (await buh.PostAsJsonAsync("/api/abonents/a8/account-flags", new { hideInPlatosfera = true, hideIn1C = false, ignoreDuplicateCheck = false })).EnsureSuccessStatusCode();
        var plato = await ReportNames(buh, "reportId=bank&period=Текущий месяц&cut=Платосфера&who=Все");
        var sber = await ReportNames(buh, "reportId=bank&period=Текущий месяц&cut=Сбербанк&who=Все");
        Assert.DoesNotContain(plato, name => name.StartsWith(account + " "));
        Assert.Contains(sber, name => name.StartsWith(account + " "));
        Assert.Equal(sber.Count - 1, plato.Count);
    }

    [Fact]
    public async Task OneCUpload_FlaggedAccountIsNotPosted()
    {
        var buh = await Login("buh1");
        var hiddenAccount = (await buh.GetFromJsonAsync<JsonElement>("/api/abonents/a8")).GetProperty("account").GetString()!;
        var openAccount = (await buh.GetFromJsonAsync<JsonElement>("/api/abonents/a7")).GetProperty("account").GetString()!;
        (await buh.PostAsJsonAsync("/api/abonents/a8/account-flags", new { hideInPlatosfera = false, hideIn1C = true, ignoreDuplicateCheck = false })).EnsureSuccessStatusCode();

        async Task<JsonElement> Upload(string fileName)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes($"{hiddenAccount};100\n{openAccount};200\n")), "file", fileName);
            var response = await buh.PostAsync("/api/exchange/upload", form);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        static string State(JsonElement batch, string key) => batch.GetProperty("lines").EnumerateArray().First(line => line.GetProperty("key").GetString() == key).GetProperty("state").GetString()!;

        var oneC = await Upload("platezhi_1c.csv");
        Assert.Equal("hidden", State(oneC, hiddenAccount));
        Assert.Equal("ok", State(oneC, openAccount));
        // Флаг касается только файлов 1С
        Assert.Equal("ok", State(await Upload("sber.csv"), hiddenAccount));

        (await buh.PostAsync($"/api/exchange/batches/{oneC.GetProperty("id").GetString()}/post", null)).EnsureSuccessStatusCode();
        await using var db = Db();
        Assert.False(await db.Payments.AnyAsync(payment => payment.AbonentId == "a8" && payment.Cashier == "1С"));
        Assert.True(await db.Payments.AnyAsync(payment => payment.AbonentId == "a7" && payment.Cashier == "1С" && payment.Period != null));
    }

    [Fact]
    public async Task CashTurnover_GroupsByOfficeAndPeriod()
    {
        var kassa = await Login("kassa1");
        var period = (await kassa.GetFromJsonAsync<JsonElement>("/api/period/current")).GetProperty("period").GetString()!;
        (await kassa.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a2", source = "cash-person", allocations = new[] { new { serviceId = "heat", amount = 1000L } }, office = "Чистоозерное. Касса. ЮЛ.", period })).EnsureSuccessStatusCode();
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var buh = await Login("buh1");
        var range = $"period=Свои даты&who=Все&from={today}&to={today}";
        Assert.Contains("Чистоозерное. Касса. ЮЛ.", await ReportNames(buh, $"reportId=cash-turnover&cut=Офисы&{range}"));
        Assert.Contains(period, await ReportNames(buh, $"reportId=cash-turnover&cut=Расчётные периоды&{range}"));
    }
}
