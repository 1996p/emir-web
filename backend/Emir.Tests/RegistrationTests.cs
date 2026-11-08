using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

/// <summary>Регистрация сохраняет всё, что заполнено в форме: контакты, паспорт (зашифрован), проживающего и выбранные услуги.</summary>
public sealed class RegistrationTests : IDisposable
{
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> LoginAs(string login)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = "test-password" })).EnsureSuccessStatusCode();
        return client;
    }

    private EmirDb Db() => new(new DbContextOptionsBuilder<EmirDb>().UseNpgsql(_factory.ConnectionString).Options);

    private static object Person(object? services = null, object? passport = null, bool? addResident = null, string? email = "ivanov@example.ru") => new
    {
        kind = "person", name = "Иванов Иван Иванович", place = "р.п. Чистоозерное", street = "ул. Школьная", house = "12", flat = "40",
        phone = "+7 900 000-00-99", email, birthDate = "1980-05-17", passport, addResident, services,
    };

    [Fact]
    public async Task Person_SavesContactsPassportResidentAndServices()
    {
        var oper = await LoginAs("oper1");
        var passport = new { series = "50 01", number = "123456", issuedAt = "2010-02-01", departmentCode = "540001" };
        var response = await oper.PostAsJsonAsync("/api/abonents", Person(new { heat = "norm", water = "meter", meterNumber = "ВК-777", meterReading = 15.5 }, passport));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var abonent = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = abonent.GetProperty("id").GetString()!;

        var services = abonent.GetProperty("services").EnumerateArray().ToList();
        Assert.Equal(new[] { "Теплоснабжение", "Холодная вода" }, services.Select(line => line.GetProperty("name").GetString()));
        var meter = services[1].GetProperty("meter");
        Assert.Equal("ВК-777", meter.GetProperty("number").GetString());
        Assert.Equal(15.5m, meter.GetProperty("lastReading").GetDecimal());

        var details = await oper.GetFromJsonAsync<JsonElement>($"/api/abonents/{id}/details");
        Assert.Equal("ivanov@example.ru", details.GetProperty("email").GetString());
        Assert.Equal("17.05.1980", details.GetProperty("birthDate").GetString());
        Assert.True(details.GetProperty("hasPassport").GetBoolean());
        Assert.Equal("Иванов Иван Иванович", Assert.Single(details.GetProperty("residents").EnumerateArray()).GetProperty("name").GetString());

        // У счётчика воды есть строка на экране показаний дома
        var rows = await oper.GetFromJsonAsync<JsonElement>($"/api/readings/rows?house={Uri.EscapeDataString(abonent.GetProperty("houseKey").GetString()!)}");
        Assert.Contains(rows.EnumerateArray(), row => row.GetProperty("meterNumber").GetString() == "ВК-777");

        // В базе паспорт зашифрован, в журнале — только отметка
        await using (var db = Db())
        {
            var stored = await db.Abonents.FirstAsync(item => item.Id == id);
            Assert.DoesNotContain("123456", stored.PassportProtected);
            Assert.Equal(2, await db.ServiceTerms.CountAsync(term => term.AbonentId == id));
        }
        var journal = await oper.GetFromJsonAsync<JsonElement>($"/api/abonents/{id}/journal");
        var registered = journal.EnumerateArray().First(record => record.GetProperty("action").GetString() == "Регистрация абонента").GetProperty("details").GetString()!;
        Assert.Contains("паспорт заполнен", registered);
        Assert.DoesNotContain("123456", registered);

        // Администратор видит паспорт, просмотр в журнале
        var admin = await LoginAs("admin");
        var account = abonent.GetProperty("account").GetString();
        var view = await (await admin.PostAsync($"/api/directories/people/{account}/passport", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("50 01 № 123456, выдан 01.02.2010, код подразделения 540-001", view.GetProperty("value").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await oper.PostAsync($"/api/directories/people/{account}/passport", null)).StatusCode);
    }

    [Fact]
    public async Task ServicesAndResident_FollowTheForm()
    {
        var oper = await LoginAs("oper1");
        var response = await oper.PostAsJsonAsync("/api/abonents", Person(new { heat = "none", water = "norm" }, addResident: false, email: null));
        response.EnsureSuccessStatusCode();
        var abonent = await response.Content.ReadFromJsonAsync<JsonElement>();
        var line = Assert.Single(abonent.GetProperty("services").EnumerateArray());
        Assert.Equal("Холодная вода", line.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("meter").ValueKind);
        Assert.Equal(0, abonent.GetProperty("residents").GetInt32());
        var details = await oper.GetFromJsonAsync<JsonElement>($"/api/abonents/{abonent.GetProperty("id").GetString()}/details");
        Assert.Empty(details.GetProperty("residents").EnumerateArray());
        Assert.False(details.GetProperty("hasPassport").GetBoolean());
    }

    [Fact]
    public async Task Organization_SavesKpp()
    {
        var oper = await LoginAs("oper1");
        var response = await oper.PostAsJsonAsync("/api/abonents", new { kind = "org", name = "ООО «Ромашка»", inn = "5400000123", kpp = "540001001", place = "с. Журавка", street = "ул. Новая", house = "3" });
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var details = await oper.GetFromJsonAsync<JsonElement>($"/api/abonents/{id}/details");
        Assert.Equal("540001001", details.GetProperty("kpp").GetString());
        Assert.Empty(details.GetProperty("residents").EnumerateArray());
    }

    public static TheoryData<string> Invalid => new()
    {
        """{"email":"не почта"}""",
        """{"birthDate":"2999-01-01"}""",
        """{"passport":{"series":"50","number":"123456"}}""",
        """{"passport":{"series":"5001","number":"12"}}""",
        """{"passport":{"series":"5001","number":"123456","departmentCode":"54"}}""",
        """{"services":{"water":"meter"}}""",
        """{"services":{"water":"meter","meterNumber":"С-1","meterReading":-1}}""",
        """{"services":{"heat":"по-любому"}}""",
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task RejectsInvalidFields(string extra)
    {
        var oper = await LoginAs("oper1");
        var body = JsonSerializer.SerializeToNode(Person())!.AsObject();
        foreach (var (key, value) in System.Text.Json.Nodes.JsonNode.Parse(extra)!.AsObject()) body[key] = value?.DeepClone();
        var response = await oper.PostAsJsonAsync("/api/abonents", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Correction_RecordsOperationDate_AndRejectsFuture()
    {
        var buh = await LoginAs("buh1");
        var future = DateOnly.FromDateTime(DateTime.Today.AddDays(1)).ToString("yyyy-MM-dd");
        var bad = await buh.PostAsJsonAsync("/api/corrections", new { abonentId = "a1", typeId = "write-off", amount = 1000, reason = "тест", operationDate = future });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var date = DateOnly.FromDateTime(DateTime.Today.AddDays(-3));
        (await buh.PostAsJsonAsync("/api/corrections", new { abonentId = "a1", typeId = "write-off", amount = 1000, reason = "акт сверки", operationDate = date.ToString("yyyy-MM-dd") })).EnsureSuccessStatusCode();
        var journal = await buh.GetFromJsonAsync<JsonElement>("/api/abonents/a1/journal");
        Assert.Contains(journal.EnumerateArray(), record => record.GetProperty("details").GetString()!.Contains($"дата операции {date:dd.MM.yyyy}"));
    }

    [Theory]
    [InlineData("89131234567", "+7 913 123-45-67")]
    [InlineData("+7 (913) 123-45-67", "+7 913 123-45-67")]
    [InlineData("913 123 45 67", "+7 913 123-45-67")]
    [InlineData("8-383-512-34-56", "+7 383 512-34-56")]
    [InlineData("", null)]
    public void Phone_IsNormalized(string input, string? expected) => Assert.Equal(expected, Emir.Api.Domain.Registration.Phone(input));

    [Theory]
    [InlineData("9999999999")]       // все цифры одинаковые
    [InlineData("12345")]            // короткий
    [InlineData("+1 913 123 45 67")] // не российский
    [InlineData("1131234567")]       // код не с 3, 4, 8, 9
    [InlineData("913 123 45 67 доб")]
    [InlineData("891312345678")]     // лишняя цифра
    public async Task Phone_InvalidIsRejected_OnRegistrationAndContact(string phone)
    {
        var oper = await LoginAs("oper1");
        var body = JsonSerializer.SerializeToNode(Person())!.AsObject();
        body["phone"] = phone;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync("/api/abonents", body)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync("/api/abonents/a2/contact", new { phone })).StatusCode);
    }

    [Fact]
    public async Task Phone_IsStoredInOneFormat()
    {
        var oper = await LoginAs("oper1");
        (await oper.PostAsJsonAsync("/api/abonents/a2/contact", new { phone = "8 (913) 123 45 67" })).EnsureSuccessStatusCode();
        Assert.Equal("+7 913 123-45-67", (await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a2")).GetProperty("phone").GetString());
    }
}
