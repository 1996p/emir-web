using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Emir.Api.Domain;
using Emir.Api.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Emir.Tests;

public sealed class EmirFactory : WebApplicationFactory<Program>
{
    // Сервер PostgreSQL должен быть запущен. Адрес можно переопределить через EMIR_TEST_POSTGRES
    private static readonly string Server = Environment.GetEnvironmentVariable("EMIR_TEST_POSTGRES")
        ?? "Host=localhost;Port=5433;Username=emir;Password=emir-dev-local";

    public string ConnectionString { get; } = $"{Server};Database=emir_test_{Guid.NewGuid():N};Maximum Pool Size=20";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Seed:DemoPassword", "test-password");
        builder.UseSetting("Auth:LoginAttemptsPerMinute", "100000");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        var options = new DbContextOptionsBuilder<Emir.Api.Data.EmirDb>().UseNpgsql(ConnectionString).Options;
        using var db = new Emir.Api.Data.EmirDb(options);
        NpgsqlConnection.ClearAllPools();
        db.Database.EnsureDeleted();
    }
}

/// <summary>Каждый тест получает отдельную базу.</summary>
public sealed class ApiTests : IDisposable
{
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> LoginAs(string login)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/api/auth/login", new { login, password = "test-password" });
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<long> Balance(HttpClient client, string id)
    {
        var abonent = await client.GetFromJsonAsync<JsonElement>($"/api/abonents/{id}");
        var debt = abonent.GetProperty("services").EnumerateArray().Sum(line => line.GetProperty("debt").GetInt64());
        return abonent.GetProperty("credit").GetInt64() - debt;
    }

    private static object Payment(params (string Service, long Amount)[] lines) => new
    {
        abonentId = "a1", source = "cash-person", allocations = lines.Select(line => new { serviceId = line.Service, amount = line.Amount }),
    };

    [Fact]
    public async Task WithoutLogin_Returns401()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/abonents")).StatusCode);
    }

    [Fact]
    public async Task WrongPassword_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { login = "admin", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RolesAreEnforcedByServer()
    {
        var cashier = await LoginAs("kassa1");
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/admin/sections")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/exchange/batches")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsync("/api/period/steps/charges/run", null)).StatusCode);

        var accountant = await LoginAs("buh1");
        var pay = await accountant.PostAsJsonAsync("/api/cash/payments", Payment(("heat", 100)));
        Assert.Equal(HttpStatusCode.Forbidden, pay.StatusCode);
    }

    [Fact]
    public async Task Payment_WithReading_MatchesExpectedBalance()
    {
        var client = await LoginAs("kassa1");
        Assert.Equal(-213450, await Balance(client, "a1"));

        var response = await client.PostAsJsonAsync("/api/cash/payments", new
        {
            abonentId = "a1", source = "cash-person", reading = 416m,
            allocations = new[] { new { serviceId = "heat", amount = 184210L }, new { serviceId = "water", amount = 41590L } },
        });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(225800, result.GetProperty("total").GetInt64());
        Assert.Equal(-8450, result.GetProperty("newBalance").GetInt64());
        Assert.Equal(-8450, await Balance(client, "a1"));
    }

    [Fact]
    public async Task Payment_SameIdempotencyKey_IsCountedOnce()
    {
        var client = await LoginAs("kassa1");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cash/payments") { Content = JsonContent.Create(Payment(("heat", 100000))) };
            request.Headers.Add("Idempotency-Key", "same-key");
            (await client.SendAsync(request)).EnsureSuccessStatusCode();
        }
        Assert.Equal(-113450, await Balance(client, "a1"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task Payment_NonPositiveAmount_IsRejected(long amount)
    {
        var client = await LoginAs("kassa1");
        var response = await client.PostAsJsonAsync("/api/cash/payments", Payment(("heat", amount)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(-213450, await Balance(client, "a1"));
    }

    [Fact]
    public async Task Refund_Twice_IsRejected()
    {
        var client = await LoginAs("kassa1");
        var body = new { abonentId = "a1", paymentId = "a1-p1", reason = "Заплатили дважды" };
        (await client.PostAsJsonAsync("/api/cash/refunds", body)).EnsureSuccessStatusCode();
        Assert.Equal(-363450, await Balance(client, "a1"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/cash/refunds", body)).StatusCode);
        Assert.Equal(-363450, await Balance(client, "a1"));
    }

    [Fact]
    public async Task Refund_WithoutReason_IsRejected()
    {
        var client = await LoginAs("kassa1");
        var response = await client.PostAsJsonAsync("/api/cash/refunds", new { abonentId = "a1", paymentId = "a1-p1", reason = " " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Reading_ReEntered_ChargesOnlyDifference()
    {
        var client = await LoginAs("oper1");
        var before = await Balance(client, "a1");
        foreach (var value in new[] { 416m, 418m, 416m })
            (await client.PostAsJsonAsync("/api/readings", new { abonentId = "a1", serviceId = "water", value })).EnsureSuccessStatusCode();
        Assert.Equal(before - 20800, await Balance(client, "a1"));

        var lower = await client.PostAsJsonAsync("/api/readings", new { abonentId = "a1", serviceId = "water", value = 100m });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, lower.StatusCode);
    }

    [Fact]
    public async Task Period_StepsMustRunInOrder()
    {
        var client = await LoginAs("buh1");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/period/steps/close/run", null)).StatusCode);
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/period/steps/charges/run", null)).StatusCode);
    }

    [Fact]
    public async Task Correction_CashierCannotUseAccountantOperations()
    {
        var client = await LoginAs("kassa1");
        var response = await client.PostAsJsonAsync("/api/corrections", new { abonentId = "a1", typeId = "write-off", amount = 100, reason = "тест" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Correction_CancelUsesServerAmount()
    {
        var client = await LoginAs("buh1");
        var response = await client.PostAsJsonAsync("/api/corrections", new { abonentId = "a1", typeId = "cancel-payment", amount = 1, reason = "Ошибка", paymentId = "a1-p1" });
        response.EnsureSuccessStatusCode();
        Assert.Equal(-363450, await Balance(client, "a1"));
    }

    [Fact]
    public async Task Upload_ThenPost_AppliesOnlyValidLines_AndOnlyOnce()
    {
        var client = await LoginAs("buh1");
        var csv = "счёт;сумма\n40010003;1000,50\n40010003;1000,50\n49999999;50\n";
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "sber.csv" } };
        var upload = await client.PostAsync("/api/exchange/upload", form);
        upload.EnsureSuccessStatusCode();
        var batch = await upload.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "ok", "duplicate", "not-found" }, batch.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("state").GetString()!).ToArray());

        var id = batch.GetProperty("id").GetString();
        var before = await Balance(client, "a3");
        (await client.PostAsync($"/api/exchange/batches/{id}/post", null)).EnsureSuccessStatusCode();
        Assert.Equal(before + 100050, await Balance(client, "a3"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/exchange/batches/{id}/post", null)).StatusCode);
    }

    [Fact]
    public async Task Upload_UnsupportedFile_IsRejected()
    {
        var client = await LoginAs("buh1");
        using var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "virus.exe" } };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/api/exchange/upload", form)).StatusCode);
    }

    [Fact]
    public async Task Report_Debtors_TotalsAddUp()
    {
        var client = await LoginAs("buh1");
        var report = await client.GetFromJsonAsync<JsonElement>("/api/reports/run?reportId=debtors&period=Этот месяц&cut=Абоненты&who=Все");
        Assert.True(report.GetProperty("available").GetBoolean());
        foreach (var row in report.GetProperty("rows").EnumerateArray())
        {
            var values = row.GetProperty("values").EnumerateArray().Select(value => value.GetInt64()).ToArray();
            Assert.Equal(values[1] + values[2], values[3]);
        }
    }

    [Fact]
    public async Task Report_Csv_EscapesFormulas()
    {
        Assert.Equal("\"'=cmd()\"", ReportEndpoints.Cell("=cmd()"));
        Assert.Equal("\"Иванова \"\"А\"\"\"", ReportEndpoints.Cell("Иванова \"А\""));
        var client = await LoginAs("buh1");
        var response = await client.GetAsync("/api/reports/export.csv?reportId=debtors&period=Этот месяц&cut=Абоненты&who=Все");
        response.EnsureSuccessStatusCode();
        Assert.Contains("Иванова", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Billing_TakeAndGive_KeepCreditAndDebtConsistent()
    {
        var abonent = new Emir.Api.Data.Abonent { Id = "x", Kind = "person", Name = "X", ShortName = "X", Street = "Y", Account = "1", Credit = 500 };
        abonent.Services.Add(new Emir.Api.Data.ServiceLine { AbonentId = "x", ServiceId = "heat", Name = "Тепло", TariffName = "", ChargeStatus = "", Debt = 0 });
        Billing.TakeFromAbonent(abonent, 1500);
        Assert.Equal(-1000, abonent.Balance);
        Billing.GiveToAbonent(abonent, 1200);
        Assert.Equal(200, abonent.Balance);
        Assert.Equal(200, abonent.Credit);
    }
}
