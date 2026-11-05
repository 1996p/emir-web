using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

public sealed class BenefitTests : IDisposable
{
    private const string Nov = "2026-11";
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> Login(string login = "buh1")
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = "test-password" })).EnsureSuccessStatusCode();
        return client;
    }

    private EmirDb Db() => new(new DbContextOptionsBuilder<EmirDb>().UseNpgsql(_factory.ConnectionString).Options);

    private static async Task<int> CategoryId(HttpClient client, string name = "Ветеран труда")
    {
        var categories = await client.GetFromJsonAsync<JsonElement>("/api/benefits/categories");
        return categories.EnumerateArray().First(item => item.GetProperty("name").GetString() == name).GetProperty("id").GetInt32();
    }

    private static async Task<HttpResponseMessage> Add(HttpClient client, string abonentId, string fullName = "Петров Сергей Ильич", string? fromDate = null, int? category = null) =>
        await client.PostAsJsonAsync("/api/benefits", new { abonentId, categoryId = category ?? await CategoryId(client), fullName, fromDate });

    private static Task<HttpResponseMessage> Import(HttpClient client, string text, bool apply = false, string fileName = "list.csv")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", fileName);
        return client.PostAsync($"/api/benefits/import?apply={apply.ToString().ToLowerInvariant()}", form);
    }

    private static async Task<List<JsonElement>> List(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/benefits{query}")).EnumerateArray().ToList();

    [Fact]
    public async Task Benefit_DoesNotChangeCharges()
    {
        var client = await Login();
        await using (var before = Db())
        {
            var abonents = await before.Abonents.Include(item => item.Services).Where(item => item.Id == "a2").ToListAsync();
            Assert.Equal(155892, (await ChargingService.Plan(before, abonents, Nov))[0].Result.Amount);
        }
        (await Add(client, "a2")).EnsureSuccessStatusCode();
        await using var after = Db();
        var same = await after.Abonents.Include(item => item.Services).Where(item => item.Id == "a2").ToListAsync();
        Assert.Equal(155892, (await ChargingService.Plan(after, same, Nov))[0].Result.Amount);
    }

    [Fact]
    public async Task Beneficiary_ShowsCategoryUuidAndLiveDebtorFlag()
    {
        var client = await Login();
        (await Add(client, "a3", "Сидорова Мария Олеговна")).EnsureSuccessStatusCode();
        (await Add(client, "a2")).EnsureSuccessStatusCode();
        var rows = await List(client);
        var debtor = rows.First(item => item.GetProperty("abonentId").GetString() == "a3");
        Assert.True(debtor.GetProperty("debtor").GetBoolean());
        Assert.Equal("Ветеран труда", debtor.GetProperty("category").GetString());
        Assert.True(Guid.TryParse(debtor.GetProperty("uuid").GetString(), out _));
        Assert.False(rows.First(item => item.GetProperty("abonentId").GetString() == "a2").GetProperty("debtor").GetBoolean());

        var kassa = await Login("kassa1");
        (await kassa.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a3", source = "cash-person", allocations = new[] { new { serviceId = "heat", amount = 711360L }, new { serviceId = "penalty", amount = 29640L } } })).EnsureSuccessStatusCode();
        Assert.False((await List(client)).First(item => item.GetProperty("abonentId").GetString() == "a3").GetProperty("debtor").GetBoolean());
    }

    [Fact]
    public async Task Duplicate_IsRefused_AfterEndCanBeAddedAgain()
    {
        var client = await Login();
        (await Add(client, "a2")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Add(client, "a2", " петров сергей ильич ")).StatusCode);
        var id = (await List(client)).Single().GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync($"/api/benefits/{id}/end", new { toDate = "2026-11-01", reason = " " })).StatusCode);
        (await client.PostAsJsonAsync($"/api/benefits/{id}/end", new { toDate = "2026-11-01", reason = "Истёк срок" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/benefits/{id}/end", new { toDate = "2026-11-02", reason = "Ещё" })).StatusCode);
        Assert.False((await List(client)).Single().GetProperty("active").GetBoolean());
        (await Add(client, "a2")).EnsureSuccessStatusCode();
        Assert.Equal(2, (await List(client)).Count);
    }

    [Fact]
    public async Task EndDateBeforeStart_Is422()
    {
        var client = await Login();
        (await Add(client, "a2", fromDate: "2026-06-01")).EnsureSuccessStatusCode();
        var id = (await List(client)).Single().GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync($"/api/benefits/{id}/end", new { toDate = "2026-01-01", reason = "Х" })).StatusCode);
    }

    [Fact]
    public async Task Categories_RenameDisable_DuplicateRefused_AndDisabledCannotBeUsed()
    {
        var client = await Login();
        (await client.PostAsJsonAsync("/api/benefits/categories", new { name = "Новая категория" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/benefits/categories", new { name = "Новая категория" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/benefits/categories", new { name = " " })).StatusCode);
        var id = await CategoryId(client, "Новая категория");
        (await client.PostAsJsonAsync($"/api/benefits/categories/{id}", new { name = "Переименована", isActive = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Add(client, "a2", category: id)).StatusCode);
    }

    [Fact]
    public async Task Import_PreviewChangesNothing_ApplyAddsOnlyValidLines()
    {
        var client = await Login();
        const string text = "﻿Лицевой счёт;ФИО;Категория;Дата начала\n40010002;Петров Сергей Ильич;Ветеран труда;01.01.2026\n40019999;Нет Такого;Ветеран труда;\n40010003;Сидорова Мария;Неизвестная;\n40010003;Сидорова Мария;ветеран труда;32.13.2026\n40010002;петров сергей ильич;Ветеран труда;\n40010005;Морозова Елена;Ветеран труда;\n";

        var preview = await (await Import(client, text)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(preview.GetProperty("applied").GetBoolean());
        Assert.Equal(["ok", "not-found", "unknown-category", "bad-date", "duplicate", "ok"], preview.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("state").GetString()));
        Assert.Empty(await List(client));

        var result = await (await Import(client, text, apply: true)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("added").GetInt32());
        var rows = await List(client);
        Assert.Equal(2, rows.Count);
        Assert.Equal("01.01.2026", rows.First(item => item.GetProperty("abonentId").GetString() == "a2").GetProperty("fromDate").GetString());

        // Повтор файла ничего не задваивает
        var again = await (await Import(client, text, apply: true)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, again.GetProperty("added").GetInt32());
        Assert.Equal(2, (await List(client)).Count);
    }

    [Theory]
    [InlineData("", "list.csv")]
    [InlineData("просто текст", "list.csv")]
    [InlineData("40010002;ФИО;Категория", "list.exe")]
    public async Task Import_BadFile_Is422(string text, string fileName)
    {
        var client = await Login();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Import(client, text, fileName: fileName)).StatusCode);
    }

    [Fact]
    public async Task Response_ContainsChargesPaymentsAndOnlyActiveInThatMonth_AndIsJournaled()
    {
        var client = await Login();
        var kassa = await Login("kassa1");
        (await Add(client, "a3", "Сидорова Мария Олеговна")).EnsureSuccessStatusCode();
        (await Add(client, "a2", "Петров Сергей Ильич", fromDate: "2027-01-01")).EnsureSuccessStatusCode();
        (await kassa.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a3", source = "cash-person", allocations = new[] { new { serviceId = "heat", amount = 10000L } } })).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();

        var response = await client.GetAsync($"/api/benefits/response.csv?period={Nov}");
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("Сидорова Мария Олеговна", text);
        Assert.DoesNotContain("Петров", text);
        // 39 м² × 35,43 ₽ = 1 381,77 ₽, оплачено 100 ₽
        Assert.Contains("1381,77", text);
        Assert.Contains("100,00", text);

        await using var db = Db();
        Assert.Contains(await db.Journal.ToListAsync(), item => item.Action == "Выгрузка для соцзащиты");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/benefits/response.csv?period=2020-01")).StatusCode);
    }

    [Fact]
    public async Task Response_EscapesFormulas()
    {
        var client = await Login();
        (await Add(client, "a2", "=HYPERLINK(1)")).EnsureSuccessStatusCode();
        var text = await client.GetStringAsync($"/api/benefits/response.csv?period={Nov}");
        Assert.Contains("'=HYPERLINK(1)", text);
    }

    [Fact]
    public async Task Roles_CashierSeesNothing_ServiceEditsButNoFileExchange()
    {
        var buh = await Login();
        (await Add(buh, "a2")).EnsureSuccessStatusCode();
        var id = (await List(buh)).Single().GetProperty("id").GetInt32();

        var kassa = await Login("kassa1");
        Assert.Equal(HttpStatusCode.Forbidden, (await kassa.GetAsync("/api/benefits")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kassa.GetAsync("/api/benefits?abonentId=a2")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kassa.GetAsync("/api/benefits/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kassa.PostAsJsonAsync("/api/benefits", new { abonentId = "a2", categoryId = 1, fullName = "Х" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kassa.GetAsync($"/api/benefits/response.csv?period={Nov}")).StatusCode);

        var oper = await Login("oper1");
        (await oper.GetAsync("/api/benefits")).EnsureSuccessStatusCode();
        (await oper.PostAsJsonAsync($"/api/benefits/{id}/end", new { toDate = "2026-11-01", reason = "Х" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await Import(oper, "40010002;Х;Ветеран труда")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await oper.GetAsync($"/api/benefits/response.csv?period={Nov}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await oper.PostAsJsonAsync("/api/benefits/categories", new { name = "Х" })).StatusCode);
    }

    [Fact]
    public async Task AbonentJournal_DoesNotRevealCategory()
    {
        var client = await Login();
        (await Add(client, "a2")).EnsureSuccessStatusCode();
        var kassa = await Login("kassa1");
        var journal = await kassa.GetStringAsync("/api/abonents/a2/journal");
        Assert.Contains("Льготник добавлен", journal);
        Assert.DoesNotContain("Ветеран", journal);
    }
}
