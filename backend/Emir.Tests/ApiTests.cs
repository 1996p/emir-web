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

    private Emir.Api.Data.EmirDb Db() => new(new DbContextOptionsBuilder<Emir.Api.Data.EmirDb>().UseNpgsql(_factory.ConnectionString).Options);

    [Theory]
    [InlineData("111", "р.п. Чистоозерное", "ул. Школьная", "12")]      // имя без букв
    [InlineData("Иванов Иван", "", "ул. Школьная", "12")]                // без населённого пункта
    [InlineData("Иванов Иван", "р.п. Чистоозерное", "ул. Южная", "7")]   // улицы нет в справочнике
    [InlineData("Иванов Иван", "с. Журавка", "ул. Школьная", "12")]      // улица другого посёлка
    [InlineData("Иванов Иван", "р.п. Чистоозерное", "ул. Школьная", "99")] // дома нет в справочнике
    [InlineData("Иванов Иван", "р.п. Чистоозерное", "ул. Школьная", "")]   // без дома
    public async Task Register_RejectsJunk(string name, string place, string street, string house)
    {
        var client = await LoginAs("oper1");
        var response = await client.PostAsJsonAsync("/api/abonents", new { kind = "person", name, place, street, house });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Directory_ReferenceColumn_IsDropdownAndValidated()
    {
        var client = await LoginAs("admin");   // поднимает приложение и создаёт базу теста
        static string J(object value) => System.Text.Json.JsonSerializer.Serialize(value);
        await using (var db = Db())
        {
            db.ReferenceTables.Add(new Emir.Api.Data.ReferenceTable { Name = "Котельные", ColumnsJson = J(new[] { "ID", "Название" }), RowsJson = J(new[] { new[] { "1", "Журавка" }, new[] { "2", "Степное" } }), RowCount = 2 });
            db.ReferenceTables.Add(new Emir.Api.Data.ReferenceTable { Name = "Абоненты ЖКХ", ColumnsJson = J(new[] { "ЛС", "Котельная" }), RowsJson = J(new[] { new[] { "40010001", "Журавка" } }), RowCount = 1 });
            await db.SaveChangesAsync();
        }

        // таблица сообщает, что колонка «Котельная» — ссылка на справочник «Котельные»
        var table = await client.GetFromJsonAsync<JsonElement>("/api/directories/table?name=" + Uri.EscapeDataString("Абоненты ЖКХ"));
        var reference = table.GetProperty("references").GetProperty("Котельная");
        Assert.Equal("Котельные", reference.GetProperty("source").GetString());

        // значения выпадающего списка — по алфавиту из источника
        var options = await client.GetFromJsonAsync<string[]>("/api/directories/options?" + $"name={Uri.EscapeDataString("Котельные")}&column={Uri.EscapeDataString("Название")}");
        Assert.Equal(new[] { "Журавка", "Степное" }, options);

        // значение не из справочника отклоняется
        var bad = await client.PostAsJsonAsync("/api/directories/rows/0", new { name = "Абоненты ЖКХ", expected = new[] { "40010001", "Журавка" }, row = new[] { "40010001", "Несуществующая" }, reason = "тест" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        // значение из справочника принимается
        var ok = await client.PostAsJsonAsync("/api/directories/rows/0", new { name = "Абоненты ЖКХ", expected = new[] { "40010001", "Журавка" }, row = new[] { "40010001", "Степное" }, reason = "тест" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
    }

    [Fact]
    public async Task Register_AcceptsRealAddress()
    {
        var client = await LoginAs("oper1");
        var response = await client.PostAsJsonAsync("/api/abonents", new { kind = "person", name = "Петров Пётр", place = "р.п. Чистоозерное", street = "ул. Школьная", house = "12", flat = "5" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("р.п. Чистоозерное", created.GetProperty("place").GetString());
        Assert.Equal("ул. Школьная 12", created.GetProperty("street").GetString());
        Assert.Equal("ул. Школьная", created.GetProperty("streetName").GetString());
        Assert.Equal("12", created.GetProperty("house").GetString());
        Assert.Equal("5", created.GetProperty("flat").GetString());
    }

    [Fact]
    public async Task HeatNorm_Apply_SetsNormForWholeCategory()
    {
        var client = await LoginAs("buh1");   // поднимает приложение и создаёт базу теста
        await using (var db = Db())
        {
            foreach (var id in new[] { "a1", "a2" }) (await db.Abonents.FirstAsync(abonent => abonent.Id == id)).HouseCategory = "Кирпич 5эт";
            db.ReferenceTables.Add(new Emir.Api.Data.ReferenceTable { Name = "Категории домов", ColumnsJson = JsonSerializer.Serialize(new[] { "ID", "Название", "Норма теплопотребления" }), RowsJson = JsonSerializer.Serialize(new[] { new[] { "1", "Кирпич 5эт", ".015" } }), RowCount = 1 });
            await db.SaveChangesAsync();
        }

        // категории нет в справочнике — норму не применить
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/heat-norms/apply", new { category = "Дерево 1эт", norm = 0.0188m, reason = "приказ 1" })).StatusCode);

        var apply = await client.PostAsJsonAsync("/api/heat-norms/apply", new { category = "Кирпич 5эт", norm = 0.0188m, reason = "приказ 1" });
        apply.EnsureSuccessStatusCode();
        var result = await apply.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("total").GetInt32());

        await using (var db = Db())
        {
            Assert.Equal(0.0188m, await db.HeatNormCategories.Where(item => item.Category == "Кирпич 5эт").Select(item => item.Norm).FirstAsync());
            Assert.All(await db.Abonents.Where(abonent => abonent.HouseCategory == "Кирпич 5эт").ToListAsync(), abonent => Assert.Equal(0.0188m, abonent.HouseHeatNorm));
            // норма записана и в справочник «Категории домов»
            Assert.Contains("0.0188", (await db.ReferenceTables.FirstAsync(item => item.Name == "Категории домов")).RowsJson);
        }
    }

    [Fact]
    public async Task HeatNorm_Apply_ForbiddenForCashier()
    {
        var client = await LoginAs("kassa1");
        var response = await client.PostAsJsonAsync("/api/heat-norms/apply", new { category = "x", norm = 0.01m, reason = "y" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_SumsBlockedLinesAndReportsHouseMeters()
    {
        var client = await LoginAs("buh1");
        var readiness = await client.GetFromJsonAsync<JsonElement>("/api/period/readiness");

        var problems = readiness.GetProperty("problems").EnumerateArray().ToList();
        var blockedFromGroups = problems.Sum(problem => problem.GetProperty("lines").GetInt32());
        Assert.Equal(readiness.GetProperty("blockedLines").GetInt32(), blockedFromGroups);
        // Каждая причина знает про своих абонентов и приводит примеры
        Assert.All(problems, problem =>
        {
            Assert.True(problem.GetProperty("abonents").GetInt32() > 0);
            Assert.NotEmpty(problem.GetProperty("examples").EnumerateArray());
        });
        Assert.True(readiness.GetProperty("houseMetersTotal").GetInt32() >= 0);
        Assert.True(readiness.GetProperty("wouldCharge").GetInt64() > 0);
    }

    [Fact]
    public async Task Readiness_ForbiddenForCashier()
    {
        var client = await LoginAs("kassa1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/period/readiness")).StatusCode);
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
