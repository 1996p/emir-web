using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

/// <summary>Правка справочников, тарифов, общедомовых счётчиков и единиц показаний.</summary>
public sealed class DirectoryEditTests : IDisposable
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

    private async Task<long> Debt(string abonentId, string serviceId)
    {
        await using var db = Db();
        return (await db.ServiceLines.FirstAsync(line => line.AbonentId == abonentId && line.ServiceId == serviceId)).Debt;
    }

    [Fact]
    public async Task ReferenceRows_AddEditDelete_WithConflictCheck()
    {
        var oper = await Login();
        await using (var db = Db())
        {
            db.ReferenceTables.Add(new ReferenceTable { Name = "Котельные", ColumnsJson = "[\"ID\",\"Название\"]", RowsJson = "[[\"1\",\"Старая\"]]", RowCount = 1, LoadedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.True((await oper.GetFromJsonAsync<JsonElement>("/api/directories/table?name=Котельные")).GetProperty("editable").GetBoolean());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync("/api/directories/rows", new { name = "Котельные", row = new[] { "2", "Новая" }, reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync("/api/directories/rows", new { name = "Котельные", row = new[] { "2" }, reason = "Акт" })).StatusCode);
        (await oper.PostAsJsonAsync("/api/directories/rows", new { name = "Котельные", row = new[] { "2", " Новая " }, reason = "Акт" })).EnsureSuccessStatusCode();
        (await oper.PostAsJsonAsync("/api/directories/rows/0", new { name = "Котельные", expected = new[] { "1", "Старая" }, row = new[] { "1", "Переименованная" }, reason = "Акт" })).EnsureSuccessStatusCode();
        // Строку уже изменили: правка по устаревшим данным отклоняется
        Assert.Equal(HttpStatusCode.Conflict, (await oper.PostAsJsonAsync("/api/directories/rows/0/delete", new { name = "Котельные", expected = new[] { "1", "Старая" }, reason = "Акт" })).StatusCode);
        (await oper.PostAsJsonAsync("/api/directories/rows/0/delete", new { name = "Котельные", expected = new[] { "1", "Переименованная" }, reason = "Акт" })).EnsureSuccessStatusCode();

        var table = await oper.GetFromJsonAsync<JsonElement>("/api/directories/table?name=Котельные");
        var rows = table.GetProperty("rows").EnumerateArray().Select(row => row.EnumerateArray().Select(cell => cell.GetString()).ToList()).ToList();
        Assert.Equal([["2", "Новая"]], rows);
        await using (var db = Db())
            Assert.Equal(3, await db.Journal.CountAsync(record => record.Action == "Справочник «Котельные»"));

        var cashier = await Login("kassa1");
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/directories/rows", new { name = "Котельные", row = new[] { "3", "Х" }, reason = "Акт" })).StatusCode);
    }

    [Fact]
    public async Task Tariff_CreateAndEdit()
    {
        var buh = await Login("buh1");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await buh.PostAsJsonAsync("/api/tariffs", new { name = "Т", scheme = "Нет такой", penaltyEnabled = true, reason = "Приказ" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await buh.PostAsJsonAsync("/api/tariffs", new { name = "Т", scheme = "AreaNormTarif", tariffNorm = "abc", penaltyEnabled = true, reason = "Приказ" })).StatusCode);
        var created = await buh.PostAsJsonAsync("/api/tariffs", new { name = "Тепло по площади (с. Новое)", scheme = "AreaNormTarif", unit = "Гкал", tariffNorm = "01=0,03#02=0,025", penaltyEnabled = true, reason = "Приказ № 1", price = 3407.48m, fromPeriod = "2026-11" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.Conflict, (await buh.PostAsJsonAsync("/api/tariffs", new { name = "Тепло по площади (с. Новое)", scheme = "NormHouse", penaltyEnabled = true, reason = "Приказ" })).StatusCode);

        // Тариф, уже выбранный у услуги: новое название видно в карточке абонента
        await using (var db = Db())
        {
            var line = await db.ServiceLines.FirstAsync(item => item.AbonentId == "a2" && item.ServiceId == "heat");
            line.TariffId = id;
            await db.SaveChangesAsync();
        }
        (await buh.PostAsJsonAsync($"/api/tariffs/{id}", new { name = "Тепло (с. Новое)", scheme = "NormHouse", unit = "Гкал", penaltyEnabled = false, reason = "Приказ № 2" })).EnsureSuccessStatusCode();
        var tariffs = await buh.GetFromJsonAsync<JsonElement>("/api/tariffs");
        var tariff = tariffs.GetProperty("tariffs").EnumerateArray().First(item => item.GetProperty("id").GetString() == id);
        Assert.Equal("NormHouse", tariff.GetProperty("scheme").GetString());
        Assert.False(tariff.GetProperty("penaltyEnabled").GetBoolean());
        Assert.Equal(3407.48m, tariff.GetProperty("currentPrice").GetDecimal());
        await using (var db = Db())
            Assert.Equal("Тепло (с. Новое)", (await db.ServiceLines.FirstAsync(item => item.AbonentId == "a2" && item.ServiceId == "heat")).TariffName);

        var oper = await Login();
        Assert.Equal(HttpStatusCode.Forbidden, (await oper.PostAsJsonAsync("/api/tariffs", new { name = "Х", scheme = "NormHouse", penaltyEnabled = true, reason = "Приказ" })).StatusCode);
    }

    [Fact]
    public async Task HouseMeter_CreateEditAndLink()
    {
        var oper = await Login();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync("/api/readings/house-meters/new", new { place = "с. Новое", street = "ул. Мира 1", number = "1", unit = "калории" })).StatusCode);
        var created = await oper.PostAsJsonAsync("/api/readings/house-meters/new", new { place = "с. Новое", street = "ул. Мира 1", number = "ТС-1", unit = "ГДж" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        (await oper.PostAsJsonAsync($"/api/readings/house-meters/{id}/edit", new { place = "с. Новое", street = "ул. Мира 1", number = "ТС-2", unit = "кВт·ч" })).EnsureSuccessStatusCode();
        await using (var db = Db())
        {
            var meter = await db.HouseMeters.FirstAsync(item => item.Id == id);
            Assert.Equal(("ТС-2", "кВт·ч"), (meter.Number, meter.Unit));
            Assert.Equal(0.00086m, meter.UnitFactor, 8);
        }

        (await oper.PostAsJsonAsync("/api/abonents/a2/services/heat/house-meter", new { houseMeterId = id })).EnsureSuccessStatusCode();
        var abonent = await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a2");
        Assert.Equal(id, abonent.GetProperty("services").EnumerateArray().First(line => line.GetProperty("id").GetString() == "heat").GetProperty("houseMeterId").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await oper.PostAsJsonAsync("/api/abonents/a2/services/heat/house-meter", new { houseMeterId = "нет" })).StatusCode);
    }

    [Fact]
    public async Task ApartmentMeter_ReadingsInGigajoulesAreChargedInGcal()
    {
        var oper = await Login();
        Assert.Equal(0, await Debt("a7", "water"));
        (await oper.PostAsJsonAsync("/api/abonents/a7/services/water/meter-unit", new { unit = "ГДж" })).EnsureSuccessStatusCode();

        // 41,868 ГДж при коэффициенте старого ЕМИР (÷4,186) = 10,0019 Гкал, цена 52 ₽ за единицу
        (await oper.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 372.868m })).EnsureSuccessStatusCode();
        Assert.Equal(52010, await Debt("a7", "water"));

        // Смена единицы пересчитывает уже внесённое показание месяца
        (await oper.PostAsJsonAsync("/api/abonents/a7/services/water/meter-unit", new { unit = (string?)null })).EnsureSuccessStatusCode();
        Assert.Equal(217714, await Debt("a7", "water"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync("/api/abonents/a7/services/water/meter-unit", new { unit = "Дж" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync("/api/abonents/a7/services/heat/meter-unit", new { unit = "ГДж" })).StatusCode);
    }
}
