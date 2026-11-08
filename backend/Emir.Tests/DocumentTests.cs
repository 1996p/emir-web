using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

public sealed class DocumentTests : IDisposable
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

    private static async Task CloseMonth(HttpClient client)
    {
        foreach (var step in new[] { "charges", "checks", "penalty", "close" })
            (await client.PostAsync($"/api/period/steps/{step}/run", null)).EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> Generate(HttpClient client, string period = Nov) => client.PostAsJsonAsync("/api/documents/generate", new { period });

    private static async Task<List<JsonElement>> Docs(HttpClient client, string query = $"period={Nov}") =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/documents?{query}")).GetProperty("documents").EnumerateArray().ToList();

    private static JsonElement Doc(IEnumerable<JsonElement> docs, string abonent, string kind) =>
        docs.First(item => item.GetProperty("abonentId").GetString() == abonent && item.GetProperty("kind").GetString() == kind);

    private static async Task<JsonElement> Print(HttpClient client, int id)
    {
        var printed = await client.GetFromJsonAsync<JsonElement>($"/api/documents/print?ids={id}");
        return printed[0].GetProperty("data");
    }

    [Theory]
    [InlineData(0, "Ноль рублей 00 копеек")]
    [InlineData(100, "Один рубль 00 копеек")]
    [InlineData(2101, "Двадцать один рубль 01 копейка")]
    [InlineData(1102, "Одиннадцать рублей 02 копейки")]
    [InlineData(176000, "Одна тысяча семьсот шестьдесят рублей 00 копеек")]
    [InlineData(200000, "Две тысячи рублей 00 копеек")]
    [InlineData(1100011, "Одиннадцать тысяч рублей 11 копеек")]
    [InlineData(100000000, "Один миллион рублей 00 копеек")]
    [InlineData(251300025, "Два миллиона пятьсот тринадцать тысяч рублей 25 копеек")]
    public void MoneyWords_AreCorrect(long kopecks, string expected) => Assert.Equal(expected, MoneyWords.Rubles(kopecks));

    [Fact]
    public async Task Generate_BeforeClose_IsRefused()
    {
        var client = await Login();
        Assert.Equal(HttpStatusCode.Conflict, (await Generate(client)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Generate(client, "2020-01")).StatusCode);
    }

    [Fact]
    public async Task Generate_CreatesKindsByAbonentType_WithSequentialNumbers()
    {
        var client = await Login();
        await CloseMonth(client);
        (await Generate(client)).EnsureSuccessStatusCode();
        var docs = await Docs(client);

        Assert.Equal("receipt", Assert.Single(docs, item => item.GetProperty("abonentId").GetString() == "a2").GetProperty("kind").GetString());
        var orgKinds = docs.Where(item => item.GetProperty("abonentId").GetString() == "a4").Select(item => item.GetProperty("kind").GetString()).Order();
        Assert.Equal(["act", "bill", "facture"], orgKinds);

        // a4 и a9 получают по счёту: номера идут подряд с единицы
        var billNumbers = docs.Where(item => item.GetProperty("kind").GetString() == "bill").Select(item => item.GetProperty("number").GetString()).Order();
        Assert.Equal(["1", "2"], billNumbers);
    }

    [Fact]
    public async Task Generate_Twice_CreatesNothingNew()
    {
        var client = await Login();
        await CloseMonth(client);
        var first = await (await Generate(client)).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await Generate(client)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(first.GetProperty("created").GetInt32() > 0);
        Assert.Equal(0, second.GetProperty("created").GetInt32());
        Assert.Equal(first.GetProperty("created").GetInt32(), second.GetProperty("existing").GetInt32());
    }

    [Fact]
    public async Task Receipt_ContainsChargeDebtAndAmountToPay()
    {
        var client = await Login();
        await CloseMonth(client);
        (await Generate(client)).EnsureSuccessStatusCode();
        var row = Doc(await Docs(client), "a2", "receipt");
        var data = await Print(client, row.GetProperty("id").GetInt32());

        // 44 м² × 35,43 ₽ = 1 558,92 ₽
        var line = data.GetProperty("lines")[0];
        Assert.Equal(155892, line.GetProperty("amount").GetInt64());
        Assert.Equal(35.43m, line.GetProperty("price").GetDecimal());
        Assert.Equal(44m, line.GetProperty("volume").GetDecimal());
        Assert.Equal("м²", line.GetProperty("unit").GetString());
        Assert.Equal(155892, data.GetProperty("toPay").GetInt64());
        Assert.Equal("40010002-2026-11", data.GetProperty("number").GetString());
        Assert.Equal("30.11.2026", data.GetProperty("date").GetString());
        Assert.Contains("Петров", data.GetProperty("buyer").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Facture_ExtractsVatFromAmount_AndExcludesPenalty()
    {
        await using (var db = Db())
        {
            _ = _factory.Server;
            db.ChangeTracker.Clear();
        }
        var client = await Login();
        await CloseMonth(client);
        await using (var db = Db())
        {
            db.ChargeEntries.Add(new ChargeEntry { AbonentId = "a4", ServiceId = "penalty", Period = Nov, Scheme = "Penalty", Amount = 999, AppliedToDebt = true, AtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        (await Generate(client)).EnsureSuccessStatusCode();
        var data = await Print(client, Doc(await Docs(client), "a4", "facture").GetProperty("id").GetInt32());

        var line = Assert.Single(data.GetProperty("lines").EnumerateArray());
        // 310 м² × 35,43 ₽ = 10 983,30 ₽, из них НДС 5 %: 10 983,30 × 5 / 105 = 523,01 ₽
        Assert.Equal(1098330, line.GetProperty("amount").GetInt64());
        Assert.Equal(52301, line.GetProperty("vat").GetInt64());
        Assert.Equal(1098330, data.GetProperty("total").GetInt64());
        Assert.Equal(5m, data.GetProperty("vatRate").GetDecimal());
    }

    [Fact]
    public async Task Receipt_ListsAllDebts_SoColumnMatchesTotal()
    {
        var client = await Login();
        await CloseMonth(client);
        (await Generate(client)).EnsureSuccessStatusCode();
        var data = await Print(client, Doc(await Docs(client), "a1", "receipt").GetProperty("id").GetInt32());

        var lines = data.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(data.GetProperty("debt").GetInt64(), lines.Sum(line => line.GetProperty("debt").GetInt64()));
        Assert.Equal(data.GetProperty("total").GetInt64(), lines.Sum(line => line.GetProperty("amount").GetInt64()));
        // Вода и пени без начисления в этом месяце, но с долгом
        Assert.Equal(3, lines.Count);
        Assert.Equal(397686, data.GetProperty("toPay").GetInt64());
    }

    [Fact]
    public async Task Facture_RefersToActNumber_NotItsOwn()
    {
        var client = await Login();
        (await client.PostAsJsonAsync("/api/documents/counters", new { year = 2026, kind = "act", last = 500 })).EnsureSuccessStatusCode();
        await CloseMonth(client);
        (await Generate(client)).EnsureSuccessStatusCode();
        var docs = await Docs(client);
        var act = Doc(docs, "a4", "act").GetProperty("number").GetString();
        var facture = Doc(docs, "a4", "facture");
        Assert.NotEqual(act, facture.GetProperty("number").GetString());
        Assert.Equal(act, (await Print(client, facture.GetProperty("id").GetInt32())).GetProperty("actNumber").GetString());

        // Ссылка сохраняется при перевыпуске и при досоздании пропущенного счёта-фактуры
        var reissued = await (await client.PostAsJsonAsync($"/api/documents/{facture.GetProperty("id").GetInt32()}/reissue", new { reason = "Тест" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(act, (await Print(client, reissued.GetProperty("id").GetInt32())).GetProperty("actNumber").GetString());
    }

    [Fact]
    public async Task OrgWithPenaltyOnly_GetsNoEmptyDocuments_AndNumbersAreNotWasted()
    {
        var client = await Login();
        await CloseMonth(client);
        await using (var db = Db())
        {
            db.ChargeEntries.RemoveRange(db.ChargeEntries.Where(item => item.AbonentId == "a4" && item.Period == Nov));
            db.ChargeEntries.Add(new ChargeEntry { AbonentId = "a4", ServiceId = "penalty", Period = Nov, Scheme = "Penalty", Amount = 999, AppliedToDebt = true, AtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        (await Generate(client)).EnsureSuccessStatusCode();
        var docs = await Docs(client);
        Assert.DoesNotContain(docs, item => item.GetProperty("abonentId").GetString() == "a4");
        Assert.Equal("1", Doc(docs, "a9", "bill").GetProperty("number").GetString());
    }

    [Fact]
    public async Task Document_KeepsSnapshot_WhenProfileAndAddressChangeLater()
    {
        var client = await Login();
        await CloseMonth(client);
        (await Generate(client)).EnsureSuccessStatusCode();
        var id = Doc(await Docs(client), "a2", "receipt").GetProperty("id").GetInt32();

        (await client.PostAsJsonAsync("/api/abonents/a2/address", new { place = "с. Журавка", street = "ул. Новая", house = "3", flat = "100" })).EnsureSuccessStatusCode();
        var profile = await client.GetFromJsonAsync<JsonElement>("/api/documents/profile");
        var changed = JsonSerializer.Deserialize<Dictionary<string, object?>>(profile.GetRawText())!;
        changed["name"] = "Новое название";
        (await client.PostAsJsonAsync("/api/documents/profile", changed)).EnsureSuccessStatusCode();

        var data = await Print(client, id);
        Assert.DoesNotContain("Новая 3", data.GetProperty("buyer").GetProperty("address").GetString());
        Assert.NotEqual("Новое название", data.GetProperty("seller").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Reclose_MarksDocumentsStale_ReissueKeepsNumberAndHistory()
    {
        var client = await Login();
        await CloseMonth(client);
        (await Generate(client)).EnsureSuccessStatusCode();
        var before = Doc(await Docs(client), "a4", "facture");
        var oldId = before.GetProperty("id").GetInt32();
        var number = before.GetProperty("number").GetString();

        (await client.PostAsJsonAsync($"/api/period/months/{Nov}/reopen", new { reason = "Цена" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Generate(client)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/documents/{oldId}/reissue", new { reason = "Рано" })).StatusCode);

        (await client.PostAsJsonAsync("/api/tariffs/heat-norm/prices", new { fromPeriod = Nov, price = 40m, reason = "Приказ" })).EnsureSuccessStatusCode();
        var preview = await client.GetFromJsonAsync<JsonElement>("/api/period/reclose/preview");
        (await client.PostAsJsonAsync("/api/period/reclose", new { reason = "Приказ", expectedDelta = preview.GetProperty("delta").GetInt64() })).EnsureSuccessStatusCode();

        Assert.True(Doc(await Docs(client), "a4", "facture").GetProperty("stale").GetBoolean());
        var checks = await client.GetFromJsonAsync<JsonElement>($"/api/checks?period={Nov}");
        var stale = checks.GetProperty("groups").EnumerateArray().First(group => group.GetProperty("id").GetString() == "stale-docs");
        Assert.True(stale.GetProperty("rows").GetArrayLength() > 0);

        var response = await client.PostAsJsonAsync($"/api/documents/{oldId}/reissue", new { reason = "Новая цена" });
        response.EnsureSuccessStatusCode();
        var newId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var data = await Print(client, newId);
        Assert.Equal(number, data.GetProperty("number").GetString());
        Assert.Equal(2, data.GetProperty("revision").GetInt32());
        // 310 м² × 40 ₽
        Assert.Equal(1240000, data.GetProperty("total").GetInt64());
        Assert.False(Doc(await Docs(client), "a4", "facture").GetProperty("stale").GetBoolean());

        // Старый документ остался как был и второй раз не перевыпускается
        Assert.Equal(1098330, (await Print(client, oldId)).GetProperty("total").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/documents/{oldId}/reissue", new { reason = "Ещё" })).StatusCode);
        var history = await Docs(client, "abonentId=a4&kind=facture&history=true");
        Assert.Equal(2, history.Count);
        await using var db = Db();
        Assert.Contains(await db.Journal.Where(item => item.AbonentId == "a4").ToListAsync(), item => item.Action == "Перевыпуск документа" && item.Reason == "Новая цена");
    }

    [Fact]
    public async Task ReissueStale_ReplacesOnlyStaleDocuments_Atomically()
    {
        var client = await Login();
        await CloseMonth(client);
        (await Generate(client)).EnsureSuccessStatusCode();
        await using (var db = Db())
        {
            var stale = await db.BillingDocuments.Where(item => item.AbonentId == "a4" || item.AbonentId == "a2").ToListAsync();
            foreach (var item in stale) item.Stale = true;
            await db.SaveChangesAsync();
        }
        var untouched = Doc(await Docs(client), "a1", "receipt").GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/documents/reissue-stale", new { period = Nov, reason = "" })).StatusCode);

        var response = await client.PostAsJsonAsync("/api/documents/reissue-stale", new { period = Nov, reason = "Новая цена" });
        response.EnsureSuccessStatusCode();
        Assert.Equal(4, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reissued").GetInt32());

        var docs = await Docs(client);
        Assert.DoesNotContain(docs, item => item.GetProperty("stale").GetBoolean());
        Assert.Equal(untouched, Doc(docs, "a1", "receipt").GetProperty("id").GetInt32());
        Assert.Equal(2, Doc(docs, "a4", "bill").GetProperty("revision").GetInt32());
        Assert.Equal(1, Doc(docs, "a1", "receipt").GetProperty("revision").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Login("kassa1")).PostAsJsonAsync("/api/documents/reissue-stale", new { period = Nov, reason = "Х" })).StatusCode);
    }

    [Fact]
    public async Task ReissueWithoutReason_Is422()
    {
        var client = await Login();
        await CloseMonth(client);
        (await Generate(client)).EnsureSuccessStatusCode();
        var id = Doc(await Docs(client), "a2", "receipt").GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync($"/api/documents/{id}/reissue", new { reason = " " })).StatusCode);
    }

    [Fact]
    public async Task NoDocsCheck_ListsClosedMonthAbonents_UntilGenerated()
    {
        var client = await Login();
        await CloseMonth(client);
        static async Task<int> Missing(HttpClient http)
        {
            var checks = await http.GetFromJsonAsync<JsonElement>($"/api/checks?period={Nov}");
            return checks.GetProperty("groups").EnumerateArray().First(group => group.GetProperty("id").GetString() == "no-docs").GetProperty("rows").GetArrayLength();
        }
        Assert.Equal(10, await Missing(client));
        (await Generate(client)).EnsureSuccessStatusCode();
        Assert.Equal(0, await Missing(client));
    }

    [Fact]
    public async Task DocsStep_Generates()
    {
        var client = await Login();
        await CloseMonth(client);
        var response = await client.PostAsync("/api/period/steps/docs/run", null);
        response.EnsureSuccessStatusCode();
        Assert.Contains("Создано", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.NotEmpty(await Docs(client));
    }

    [Fact]
    public async Task Generate_WithoutProfile_IsRefused_AndBadProfileRejected()
    {
        var client = await Login();
        await CloseMonth(client);
        await using (var db = Db())
        {
            db.OrganizationProfiles.RemoveRange(db.OrganizationProfiles);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await Generate(client)).StatusCode);
        var bad = await client.PostAsJsonAsync("/api/documents/profile", new { name = "Х", inn = "123", address = "А", bankName = "Б", bik = "044525000", account = "40702810000000000001" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    [Fact]
    public async Task Counter_MovesOnlyForward_AndIsUsed()
    {
        var client = await Login();
        (await client.PostAsJsonAsync("/api/documents/counters", new { year = 2026, kind = "bill", last = 100 })).EnsureSuccessStatusCode();
        await CloseMonth(client);
        (await Generate(client)).EnsureSuccessStatusCode();
        var numbers = (await Docs(client)).Where(item => item.GetProperty("kind").GetString() == "bill").Select(item => item.GetProperty("number").GetString()).Order();
        Assert.Equal(["101", "102"], numbers);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/documents/counters", new { year = 2026, kind = "bill", last = 50 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/documents/counters", new { year = 2026, kind = "receipt", last = 5 })).StatusCode);
    }

    [Fact]
    public async Task Roles_CashierReadsAndPrints_ButCannotChange()
    {
        var buh = await Login();
        await CloseMonth(buh);
        (await Generate(buh)).EnsureSuccessStatusCode();
        var id = Doc(await Docs(buh), "a2", "receipt").GetProperty("id").GetInt32();

        var cashier = await Login("kassa1");
        Assert.NotEmpty(await Docs(cashier));
        (await cashier.GetAsync($"/api/documents/print?ids={id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await Generate(cashier)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/documents/{id}/reissue", new { reason = "Х" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/documents/profile", new { name = "Х" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/documents/counters")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"/api/documents/print?ids={id}")).StatusCode);
    }

    [Fact]
    public async Task ParallelGenerate_DoesNotDuplicateOrSkipNumbers()
    {
        var client = await Login();
        await CloseMonth(client);
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Generate(client)));
        Assert.Contains(responses, response => response.IsSuccessStatusCode);
        Assert.All(responses, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));

        await using var db = Db();
        var bills = await db.BillingDocuments.Where(item => item.Kind == "bill").Select(item => item.Number).ToListAsync();
        Assert.Equal(["1", "2"], bills.Order());
        Assert.Equal(2, (await db.DocumentCounters.FirstAsync(item => item.Kind == "bill")).Last);
    }

    [Fact]
    public async Task Print_RejectsBadIds()
    {
        var client = await Login();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/api/documents/print?ids=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/api/documents/print?ids=")).StatusCode);
    }
}
