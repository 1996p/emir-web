using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

public sealed class ChecksTests : IDisposable
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

    private static async Task<JsonElement> Group(HttpClient client, string id, string? period = null)
    {
        var checks = await client.GetFromJsonAsync<JsonElement>("/api/checks" + (period is null ? "" : $"?period={period}"));
        return checks.GetProperty("groups").EnumerateArray().First(group => group.GetProperty("id").GetString() == id);
    }

    private static int Count(JsonElement group) => group.GetProperty("rows").GetArrayLength();

    private static IEnumerable<string> Cells(JsonElement group) =>
        group.GetProperty("rows").EnumerateArray().SelectMany(row => row.GetProperty("cells").EnumerateArray()).Select(cell => cell.GetString()!);

    private static async Task CloseMonth(HttpClient client)
    {
        foreach (var step in new[] { "charges", "checks", "penalty", "close" })
            (await client.PostAsync($"/api/period/steps/{step}/run", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ClosedMonth_WithoutInterference_HasNoDifferences()
    {
        var client = await Login();
        (await (await Login("oper1")).PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 335.5m })).EnsureSuccessStatusCode();
        await CloseMonth(client);
        Assert.Equal(0, Count(await Group(client, "differences")));
    }

    [Fact]
    public async Task ChangedChargeAmount_IsFound_WithExactDifference()
    {
        var client = await Login();
        await CloseMonth(client);
        await using (var db = Db())
        {
            var entry = await db.ChargeEntries.FirstAsync(item => item.AbonentId == "a2" && item.ServiceId == "heat" && item.Period == Nov);
            entry.Amount -= 500;
            await db.SaveChangesAsync();
        }
        var group = await Group(client, "differences");
        var row = Assert.Single(group.GetProperty("rows").EnumerateArray());
        Assert.Equal("a2", row.GetProperty("abonentId").GetString());
        Assert.Equal("5,00", row.GetProperty("cells")[4].GetString());
        Assert.Equal("error", group.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task MissingChargeInClosedMonth_IsFound_ButNotBeforeRun()
    {
        var client = await Login();
        Assert.Equal(0, Count(await Group(client, "differences")));

        await CloseMonth(client);
        await using (var db = Db())
        {
            var entry = await db.ChargeEntries.FirstAsync(item => item.AbonentId == "a2" && item.ServiceId == "heat" && item.Period == Nov);
            db.ChargeEntries.Remove(entry);
            await db.SaveChangesAsync();
        }
        Assert.Contains("Петров С. И.", Cells(await Group(client, "differences")));
    }

    [Fact]
    public async Task NotCharged_ReportsMissingReadingAndTariff()
    {
        var client = await Login();
        (await client.PostAsJsonAsync("/api/abonents/a2/services/heat/terms", new { fromPeriod = Nov, tariffId = (string?)null, state = "Работает", reason = "Тест" })).EnsureSuccessStatusCode();
        var cells = Cells(await Group(client, "not-charged")).ToList();
        Assert.Contains("Не выбран тариф", cells);
        Assert.Contains(cells, cell => cell.Contains("показан", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SharpChange_IsFound_ByThreshold()
    {
        var client = await Login();
        await CloseMonth(client);
        (await client.PostAsync("/api/period/open-next", null)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/tariffs/heat-norm/prices", new { fromPeriod = "2026-12", price = 53m, reason = "Рост на 49,6 %" })).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/abonents/a2/recharge", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, Count(await Group(client, "sharp-change")));

        await using (var db = Db())
        {
            var entry = await db.ChargeEntries.FirstAsync(item => item.AbonentId == "a2" && item.ServiceId == "heat" && item.Period == "2026-12");
            entry.Amount = entry.Amount * 2;
            await db.SaveChangesAsync();
        }
        Assert.Contains("Петров С. И.", Cells(await Group(client, "sharp-change")));
    }

    [Fact]
    public async Task SharpChange_IgnoresOpeningDebtEntries()
    {
        var client = await Login();
        (await client.PostAsync("/api/abonents/a1/recharge", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, Count(await Group(client, "sharp-change")));
    }

    [Fact]
    public async Task SuspendedReading_IsListed()
    {
        var client = await Login();
        (await client.PostAsJsonAsync("/api/abonents/a7/services/water/terms", new { fromPeriod = Nov, tariffId = "water-meter", state = "Приостановлена", reason = "Тест" })).EnsureSuccessStatusCode();
        (await (await Login("oper1")).PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 340m })).EnsureSuccessStatusCode();
        var cells = Cells(await Group(client, "suspended-readings")).ToList();
        Assert.Contains("Волков Д. А.", cells);
        Assert.Contains("Приостановлена", cells);
    }

    [Fact]
    public async Task OtherTariffInPlace_FlagsOnlyMinority()
    {
        var client = await Login();
        Assert.Equal(0, Count(await Group(client, "tariff-place")));
        (await client.PostAsJsonAsync("/api/abonents/a2/services/heat/terms", new { fromPeriod = Nov, tariffId = "water-meter", state = "Работает", reason = "Тест" })).EnsureSuccessStatusCode();
        var row = Assert.Single((await Group(client, "tariff-place")).GetProperty("rows").EnumerateArray());
        Assert.Equal("a2", row.GetProperty("abonentId").GetString());
    }

    [Fact]
    public async Task Duplicates_FindSameNameAndSameAddress_NotOrganizations()
    {
        var client = await Login();
        Assert.Equal(0, Count(await Group(client, "duplicates")));

        var oper = await Login("oper1");
        (await oper.PostAsJsonAsync("/api/abonents", new { kind = "person", name = "петров сергей ильич ", place = "р.п. Чистоозерное", street = "ул. Мира", house = "21", flat = "77" })).EnsureSuccessStatusCode();
        var byName = Cells(await Group(client, "duplicates")).ToList();
        Assert.Equal(2, byName.Count(cell => cell == "Одинаковое ФИО"));

        await using (var db = Db())
        {
            var other = await db.Abonents.FirstAsync(item => item.Id == "a8");
            var first = await db.Abonents.FirstAsync(item => item.Id == "a7");
            (other.Place, other.Street, other.Flat) = (first.Place, first.Street, first.Flat);
            var org = await db.Abonents.FirstAsync(item => item.Id == "a4");
            var orgTwin = await db.Abonents.FirstAsync(item => item.Id == "a9");
            (orgTwin.Place, orgTwin.Street) = (org.Place, org.Street);
            await db.SaveChangesAsync();
        }
        var cells = Cells(await Group(client, "duplicates")).ToList();
        Assert.Equal(2, cells.Count(cell => cell == "Одинаковый адрес"));
        Assert.DoesNotContain("ООО «Пример»", cells);
    }

    [Fact]
    public async Task CreditAndDebt_Together_IsListed()
    {
        var client = await Login();
        Assert.Equal(0, Count(await Group(client, "credit-debt")));
        (await client.PostAsync("/api/abonents/a7/recharge", null)).EnsureSuccessStatusCode();
        Assert.Contains("Волков Д. А.", Cells(await Group(client, "credit-debt")));
    }

    [Fact]
    public async Task UnresolvedLoads_ListOnlyProblemLinesOfUnpostedBatches()
    {
        var client = await Login();
        var cells = Cells(await Group(client, "unresolved-loads")).ToList();
        Assert.Contains("Счёт не найден", cells);
        Assert.Contains("Дубль", cells);
        Assert.DoesNotContain("sber_demo.csv", cells);
    }

    [Fact]
    public async Task Revisions_ShowRecloseChanges_ForThatMonth()
    {
        var client = await Login();
        await CloseMonth(client);
        (await client.PostAsJsonAsync($"/api/period/months/{Nov}/reopen", new { reason = "Тест" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/tariffs/heat-norm/prices", new { fromPeriod = Nov, price = 40m, reason = "Приказ" })).EnsureSuccessStatusCode();
        var preview = await client.GetFromJsonAsync<JsonElement>("/api/period/reclose/preview");
        (await client.PostAsJsonAsync("/api/period/reclose", new { reason = "Основание теста", expectedDelta = preview.GetProperty("delta").GetInt64() })).EnsureSuccessStatusCode();

        Assert.Contains("Основание теста", Cells(await Group(client, "revisions")));
        // После перезакрытия расчёт снова совпадает
        Assert.Equal(0, Count(await Group(client, "differences")));
    }

    [Fact]
    public async Task EarlierMonth_CanBeChecked_AndUnknownMonthIs404()
    {
        var client = await Login();
        await CloseMonth(client);
        (await client.PostAsync("/api/period/open-next", null)).EnsureSuccessStatusCode();

        var checks = await client.GetFromJsonAsync<JsonElement>("/api/checks");
        Assert.Equal("2026-12", checks.GetProperty("period").GetString());
        Assert.Equal(2, checks.GetProperty("months").GetArrayLength());
        Assert.Equal(0, Count(await Group(client, "differences", Nov)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/checks?period=2020-01")).StatusCode);
    }

    [Fact]
    public async Task Checks_ChangeNothing()
    {
        var client = await Login();
        await CloseMonth(client);
        await using var before = Db();
        var debt = await before.ServiceLines.SumAsync(line => line.Debt);
        var entries = await before.ChargeEntries.CountAsync();
        (await client.GetAsync("/api/checks")).EnsureSuccessStatusCode();
        await using var after = Db();
        Assert.Equal(debt, await after.ServiceLines.SumAsync(line => line.Debt));
        Assert.Equal(entries, await after.ChargeEntries.CountAsync());
    }

    [Fact]
    public async Task Roles_OnlyAccountantAndAdmin()
    {
        foreach (var login in new[] { "kassa1", "oper1" })
        {
            var client = await Login(login);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/checks")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/checks/duplicates/export.csv")).StatusCode);
        }
        (await (await Login("admin")).GetAsync("/api/checks")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Export_EscapesFormulas_AndUnknownCheckIs404()
    {
        var oper = await Login("oper1");
        (await oper.PostAsJsonAsync("/api/abonents", new { kind = "person", name = "=HYPERLINK(1)", place = "р.п. Чистоозерное", street = "ул. Школьная", house = "12", flat = "101" })).EnsureSuccessStatusCode();
        (await oper.PostAsJsonAsync("/api/abonents", new { kind = "person", name = "=hyperlink(1)", place = "р.п. Чистоозерное", street = "ул. Школьная", house = "12", flat = "102" })).EnsureSuccessStatusCode();
        var client = await Login();
        var response = await client.GetAsync("/api/checks/duplicates/export.csv");
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("'=HYPERLINK(1)", text);
        Assert.DoesNotContain("\"=HYPERLINK", text);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/checks/nope/export.csv")).StatusCode);
    }

    [Fact]
    public async Task ChecksStep_ReportsCountsAndWritesJournal()
    {
        var client = await Login();
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        var response = await client.PostAsync("/api/period/steps/checks/run", null);
        response.EnsureSuccessStatusCode();
        var message = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()!;
        Assert.Contains("Ошибок: ", message);
        Assert.Contains("предупреждений: ", message);
        await using var db = Db();
        Assert.Contains(await db.Journal.Where(item => item.Action == "Расчётный месяц").ToListAsync(), item => item.Details.Contains("Ошибок: "));
    }
}
