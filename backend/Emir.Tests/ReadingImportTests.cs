using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Emir.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

public sealed class ReadingImportTests : IDisposable
{
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> Login(string login = "oper1")
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = "test-password" })).EnsureSuccessStatusCode();
        return client;
    }

    private EmirDb Db() => new(new DbContextOptionsBuilder<EmirDb>().UseNpgsql(_factory.ConnectionString).Options);

    private static Task<HttpResponseMessage> Send(HttpClient client, byte[] bytes, string query = "", string fileName = "readings.csv")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", fileName);
        return client.PostAsync($"/api/readings/import?{query}", form);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string text, string query = "") => Send(client, Encoding.UTF8.GetBytes(text), query);

    private static async Task<JsonElement> Preview(HttpClient client, string text, string query = "")
    {
        var response = await Send(client, text, query);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static IEnumerable<string> States(JsonElement result) => result.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("state").GetString()!);

    private static async Task<JsonElement> Water(HttpClient client, string id) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/abonents/{id}")).GetProperty("services").EnumerateArray().First(line => line.GetProperty("id").GetString() == "water");

    [Fact]
    public async Task Preview_ChecksEveryLine_AndChangesNothing()
    {
        var client = await Login();
        // a7 СТК-10006 было 331, среднее 5: расход 4 в норме, у a4 СТК-20001 было 2140: расход 100 подозрителен
        const string text = "ЛС;Счётчик;Показание\n40010006;СТК-10006;335\n40020001;СТК-20001;2240\n40019999;СТК-1;10\n40010002;СТК-1;10\n40010005;ЧУЖОЙ;90\n40010001;СТК-10001;100\n40010006;СТК-10006;336\n40010008;СТК-10008;мало\n";
        var result = await Preview(client, text);
        Assert.Equal(["ok", "high", "not-found", "no-meter", "unknown-meter", "lower", "duplicate", "bad-value"], States(result));
        Assert.Equal(1, result.GetProperty("applicable").GetInt32());
        Assert.Equal(1, result.GetProperty("high").GetInt32());
        // 4 м³ × 52 ₽
        Assert.Equal(20800, result.GetProperty("lines")[0].GetProperty("charge").GetInt64());
        Assert.Equal(20800, result.GetProperty("charge").GetInt64());
        Assert.Equal(0, (await Water(client, "a7")).GetProperty("debt").GetInt64());
        await using var db = Db();
        Assert.False(await db.PeriodReadings.AnyAsync());
    }

    [Fact]
    public async Task Apply_SavesOnlyGoodLines_SkipsHighUnlessAsked()
    {
        var client = await Login();
        const string text = "40010006;СТК-10006;335\n40020001;СТК-20001;2240\n40019999;СТК-1;10\n";
        var applied = await (await Send(client, text, "apply=true&expected=1")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, applied.GetProperty("saved").GetInt32());
        Assert.Equal(20800, (await Water(client, "a7")).GetProperty("debt").GetInt64());
        Assert.Equal(0, (await Water(client, "a4")).GetProperty("debt").GetInt64());

        var withHigh = await (await Send(client, text, "apply=true&includeHigh=true&expected=1")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, withHigh.GetProperty("saved").GetInt32());
        Assert.Equal(520000, (await Water(client, "a4")).GetProperty("debt").GetInt64());
    }

    [Fact]
    public async Task SameFileAgain_ChargesNothing_CorrectedValueChargesOnlyDifference()
    {
        var client = await Login();
        (await Send(client, "40010006;СТК-10006;335\n", "apply=true&expected=1")).EnsureSuccessStatusCode();
        Assert.Equal(["same"], States(await Preview(client, "40010006;СТК-10006;335\n")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(client, "40010006;СТК-10006;335\n", "apply=true&expected=0")).StatusCode);

        var corrected = await Preview(client, "40010006;СТК-10006;336\n");
        Assert.Equal(5200, corrected.GetProperty("charge").GetInt64());
        (await Send(client, "40010006;СТК-10006;336\n", "apply=true&expected=1")).EnsureSuccessStatusCode();
        Assert.Equal(26000, (await Water(client, "a7")).GetProperty("debt").GetInt64());
    }

    [Fact]
    public async Task StalePreview_IsRefused_AndNothingIsSaved()
    {
        var client = await Login();
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client, "40010006;СТК-10006;335\n", "apply=true&expected=5")).StatusCode);
        Assert.Equal(0, (await Water(client, "a7")).GetProperty("debt").GetInt64());
    }

    [Fact]
    public async Task Columns_AndSeparator_AreConfigurable_AndDecimalCommaWorks()
    {
        var client = await Login();
        var result = await Preview(client, "01.11.2026\t335,5\tСТК-10006\t40010006\n", "separator=tab&accountColumn=4&meterColumn=3&valueColumn=2&dateColumn=1");
        Assert.Equal(["ok"], States(result));
        Assert.Equal(4.5m, result.GetProperty("lines")[0].GetProperty("usage").GetDecimal());

        var quoted = await Preview(client, "\"40010006\",\"СТК-10006\",\"335\"\n", "separator=comma");
        Assert.Equal(["ok"], States(quoted));
    }

    [Fact]
    public async Task WithoutMeterColumn_OneMeterIsFound()
    {
        var client = await Login();
        var result = await Preview(client, "40010006;335\n", "meterColumn=0&valueColumn=2");
        Assert.Equal(["ok"], States(result));
    }

    [Fact]
    public async Task WithoutMeterColumn_SeveralMeters_AreNotGuessed()
    {
        var client = await Login();
        await using (var db = Db())
        {
            db.ServiceLines.Add(new ServiceLine { AbonentId = "a7", ServiceId = "hot", Name = "Горячая вода", TariffName = "", ChargeStatus = "", MeterNumber = "ГВ-1", MeterLastReading = 5 });
            await db.SaveChangesAsync();
        }
        Assert.Equal(["many-meters"], States(await Preview(client, "40010006;335\n", "meterColumn=0&valueColumn=2")));
    }

    [Fact]
    public async Task DateColumn_RefusesOtherMonthAndBadDate()
    {
        var client = await Login();
        var result = await Preview(client, "40010006;СТК-10006;335;15.11.2026\n40010005;СТК-10005;90;15.10.2026\n40010008;СТК-10008;703;когда\n40010001;СТК-10001;413;2026-11-30 10:00\n", "dateColumn=4");
        Assert.Equal(["ok", "other-month", "bad-date", "ok"], States(result));
    }

    [Fact]
    public async Task Windows1251File_IsRead()
    {
        var client = await Login();
        var bytes = CodePagesEncoding().GetBytes("Лицевой;Счётчик;Значение\n40010006;СТК-10006;335\n");
        var response = await Send(client, bytes);
        response.EnsureSuccessStatusCode();
        Assert.Equal(["ok"], States((await response.Content.ReadFromJsonAsync<JsonElement>())));
    }

    private static Encoding CodePagesEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251);
    }

    [Fact]
    public async Task HeaderIsSkipped_OnlyOnFirstLine_BomIsIgnored()
    {
        var client = await Login();
        var result = await Preview(client, "﻿ЛС;Счётчик;Показание\n40010006;СТК-10006;335\nИтого;;сумма\n");
        Assert.Equal(["ok", "bad-value"], States(result));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("просто;текст\n", "")]
    [InlineData("40010006;СТК-10006;335\n", "valueColumn=1")]
    [InlineData("40010006;СТК-10006;335\n", "accountColumn=0")]
    public async Task BadInput_Is422(string text, string query)
    {
        var client = await Login();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(client, text, query)).StatusCode);
    }

    [Fact]
    public async Task WrongExtension_Is422()
    {
        var client = await Login();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(client, Encoding.UTF8.GetBytes("40010006;СТК-10006;335\n"), "", "readings.exe")).StatusCode);
    }

    [Fact]
    public async Task ClosedMonth_RefusesApply()
    {
        var buh = await Login("buh1");
        foreach (var step in new[] { "charges", "checks", "penalty", "close" }) (await buh.PostAsync($"/api/period/steps/{step}/run", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Send(buh, "40010006;СТК-10006;335\n", "apply=true&expected=1")).StatusCode);
    }

    [Fact]
    public async Task Apply_AuditedInLedgerAndJournal_AndChargeRunDoesNotDouble()
    {
        var buh = await Login("buh1");
        (await Send(buh, "40010006;СТК-10006;335\n", "apply=true&expected=1")).EnsureSuccessStatusCode();
        await using (var db = Db())
        {
            var row = await db.LedgerEntries.OrderBy(item => item.Id).LastAsync(item => item.AbonentId == "a7");
            Assert.Equal("reading", row.Kind);
            Assert.Equal("readings.csv", row.Reference);
            Assert.Equal(20800, row.DebtDelta);
            Assert.Contains(await db.Journal.ToListAsync(), item => item.Action == "Загрузка показаний" && item.Details.Contains("readings.csv"));
            Assert.Contains(await db.Journal.Where(item => item.AbonentId == "a7").ToListAsync(), item => item.Details.Contains("СТК-10006"));
        }
        (await buh.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        Assert.Equal(20800, (await Water(buh, "a7")).GetProperty("debt").GetInt64());
    }

    [Fact]
    public async Task SuspendedService_ReadingKeptWithoutCharge()
    {
        var buh = await Login("buh1");
        (await buh.PostAsJsonAsync("/api/abonents/a7/services/water/terms", new { fromPeriod = "2026-11", tariffId = "water-meter", state = "Приостановлена", reason = "Тест" })).EnsureSuccessStatusCode();
        var result = await Preview(buh, "40010006;СТК-10006;335\n");
        Assert.Equal(0, result.GetProperty("charge").GetInt64());
        (await Send(buh, "40010006;СТК-10006;335\n", "apply=true&expected=1")).EnsureSuccessStatusCode();
        Assert.Equal(0, (await Water(buh, "a7")).GetProperty("debt").GetInt64());
    }

    [Fact]
    public async Task Roles_CashierCannotImport()
    {
        var kassa = await Login("kassa1");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(kassa, "40010006;СТК-10006;335\n")).StatusCode);
    }

    [Fact]
    public async Task ParallelApply_ChargesOnce()
    {
        var client = await Login();
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Send(client, "40010006;СТК-10006;335\n", "apply=true&expected=1")));
        Assert.Contains(responses, response => response.IsSuccessStatusCode);
        Assert.Equal(20800, (await Water(client, "a7")).GetProperty("debt").GetInt64());
    }
}
