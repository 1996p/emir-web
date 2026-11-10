using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Domain;
using Emir.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

/// <summary>Адрес абонента из справочников: Город → Улица → Дом, добавление улицы и дома из формы, переименование в справочнике.</summary>
public sealed class AddressTests : IDisposable
{
    private const string Chi = "р.п. Чистоозерное";
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> LoginAs(string login)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = "test-password" })).EnsureSuccessStatusCode();
        return client;
    }

    private static string Q(string value) => Uri.EscapeDataString(value);

    [Fact]
    public async Task Lists_AreBuiltFromAbonentAddresses_AndCascade()
    {
        var client = await LoginAs("oper1");
        var cities = await client.GetFromJsonAsync<string[]>("/api/addresses/cities");
        Assert.Equal(new[] { Chi, "с. Журавка" }, cities);
        var streets = await client.GetFromJsonAsync<string[]>($"/api/addresses/streets?place={Q(Chi)}");
        Assert.Contains("ул. Школьная", streets!);
        Assert.DoesNotContain("ул. Новая", streets!);
        var houses = await client.GetFromJsonAsync<string[]>($"/api/addresses/houses?place={Q(Chi)}&street={Q("ул. Мира")}");
        Assert.Equal(new[] { "21", "23" }, houses);
    }

    [Fact]
    public async Task NewStreetAndHouse_ThenRegister()
    {
        var client = await LoginAs("oper1");
        var register = new { kind = "person", name = "Новиков Иван", place = Chi, street = "ул. Полевая", house = "5а", flat = "2" };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/abonents", register)).StatusCode);

        // дом нельзя завести на улицу, которой нет
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/addresses/houses", new { place = Chi, street = "ул. Полевая", house = "5а", reason = "акт" })).StatusCode);
        // без причины не добавляется
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/addresses/streets", new { place = Chi, street = "ул. Полевая", reason = "" })).StatusCode);

        (await client.PostAsJsonAsync("/api/addresses/streets", new { place = Chi, street = "ул. Полевая", reason = "новая улица" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/addresses/streets", new { place = Chi, street = "УЛ. ПОЛЕВАЯ", reason = "дубль" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/addresses/houses", new { place = Chi, street = "ул. Полевая", house = "дом", reason = "акт" })).StatusCode);
        (await client.PostAsJsonAsync("/api/addresses/houses", new { place = Chi, street = "ул. Полевая", house = "5а", reason = "акт ввода" })).EnsureSuccessStatusCode();
        Assert.Equal(new[] { "5а" }, await client.GetFromJsonAsync<string[]>($"/api/addresses/houses?place={Q(Chi)}&street={Q("ул. Полевая")}"));

        var created = await client.PostAsJsonAsync("/api/abonents", register);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var abonent = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ул. Полевая 5а", abonent.GetProperty("street").GetString());
        Assert.Equal(Addresses.HouseKey(Chi, "ул. Полевая 5а"), abonent.GetProperty("houseKey").GetString());
    }

    [Fact]
    public async Task CashierCannotAddStreets()
    {
        var client = await LoginAs("kassa1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/addresses/streets", new { place = Chi, street = "ул. Касса", reason = "тест" })).StatusCode);
    }

    [Fact]
    public async Task ChangeAddress_UsesDirectories()
    {
        var client = await LoginAs("oper1");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/abonents/a2/address", new { place = Chi, street = "ул. Нет такой", house = "1" })).StatusCode);
        var response = await client.PostAsJsonAsync("/api/abonents/a2/address", new { place = "с. Журавка", street = "ул. Новая", house = "3", flat = "9" });
        response.EnsureSuccessStatusCode();
        var abonent = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("с. Журавка", abonent.GetProperty("place").GetString());
        Assert.Equal("ул. Новая 3", abonent.GetProperty("street").GetString());
        Assert.Equal("9", abonent.GetProperty("flat").GetString());
        // тот же адрес ещё раз — не изменение
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/abonents/a2/address", new { place = "с. Журавка", street = "ул. Новая", house = "3", flat = "9" })).StatusCode);
        var journal = await client.GetFromJsonAsync<JsonElement>("/api/abonents/a2/journal");
        Assert.Contains(journal.EnumerateArray(), record => record.GetProperty("action").GetString() == "Смена адреса" && record.GetProperty("details").GetString()!.Contains("с. Журавка, ул. Новая 3-9"));
    }

    [Fact]
    public async Task RenamingStreet_MovesAbonents_DeletingUsedHouse_IsRefused()
    {
        var client = await LoginAs("admin");
        var table = await client.GetFromJsonAsync<JsonElement>($"/api/directories/table?name={Q("Улицы")}");
        var rows = table.GetProperty("rows").EnumerateArray().Select(row => row.EnumerateArray().Select(cell => cell.GetString()!).ToArray()).ToList();
        var index = rows.FindIndex(row => row[0] == Chi && row[1] == "ул. Мира");
        var renamed = await client.PostAsJsonAsync($"/api/directories/rows/{index}", new { name = "Улицы", expected = rows[index], row = new[] { Chi, "ул. Мира (бывш. Ленина)" }, reason = "переименование" });
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        var a7 = await client.GetFromJsonAsync<JsonElement>("/api/abonents/a7");
        Assert.Equal("ул. Мира (бывш. Ленина) 21", a7.GetProperty("street").GetString());
        Assert.Equal("ул. Мира (бывш. Ленина)", a7.GetProperty("streetName").GetString());

        // дом 21 у абонента a7: удалить его из справочника нельзя
        var houses = await client.GetFromJsonAsync<JsonElement>($"/api/directories/table?name={Q("Дома")}");
        var houseRows = houses.GetProperty("rows").EnumerateArray().Select(row => row.EnumerateArray().Select(cell => cell.GetString()!).ToArray()).ToList();
        var houseAt = houseRows.FindIndex(row => row[1] == Chi && row[3] == "21");
        Assert.Equal("ул. Мира (бывш. Ленина)", houseRows[houseAt][2]);
        var delete = await client.PostAsJsonAsync($"/api/directories/rows/{houseAt}/delete", new { name = "Дома", expected = houseRows[houseAt], reason = "ошибка" });
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task ImportCsv_AddsOnlyMissing_DryRunWritesNothing()
    {
        await LoginAs("admin");   // поднимает приложение: справочники адресов созданы из тестовых абонентов
        var file = Path.GetTempFileName();
        await File.WriteAllLinesAsync(file,
        [
            "﻿Населённый пункт;Улица;Дом;Квартир в базе;ГИС ЖКХ;ФИАС",
            $"{Chi};ул. Школьная;12;3;;",          // уже есть
            "Яблоневка;Садовая;4;2;AAH01;guid",     // новый пункт, улица и дом
            "Яблоневка;Садовая;4;1;;",              // дубль
            "Яблоневка;Садовая;14/1;0;;",
            "Яблоневка;Садовая;б/н;0;;",            // не номер дома
        ]);
        await using var db = new EmirDb(new DbContextOptionsBuilder<EmirDb>().UseNpgsql(_factory.ConnectionString).Options);

        var dry = await Addresses.ImportCsv(db, file, apply: false);
        Assert.Equal((1, 1, 2, 2, 1), (dry.CitiesAdded, dry.StreetsAdded, dry.HousesAdded, dry.Existing, dry.Problems.Count));
        Assert.Empty(await Addresses.StreetList(db, "Яблоневка"));

        var applied = await Addresses.ImportCsv(db, file, apply: true);
        Assert.Equal(2, applied.HousesAdded);
        Assert.Equal(new[] { "4", "14/1" }, await Addresses.HouseList(db, "Яблоневка", "Садовая"));
        Assert.Contains("21", await Addresses.HouseList(db, Chi, "ул. Мира"));   // существующее не тронуто

        var again = await Addresses.ImportCsv(db, file, apply: true);
        Assert.Equal((0, 0, 0), (again.CitiesAdded, again.StreetsAdded, again.HousesAdded));
        File.Delete(file);
    }

    [Fact]
    public void HouseKey_IgnoresTypesCaseAndPunctuation()
    {
        Assert.Equal(Addresses.HouseKey("рп. Чистоозерное", "ул. Школьная, 12"), Addresses.HouseKey("Чистоозерное рп.", "Школьная ул. 12"));
        Assert.NotEqual(Addresses.HouseKey("с. Журавка", "ул. Школьная 12"), Addresses.HouseKey("р.п. Чистоозерное", "ул. Школьная 12"));
        Assert.Equal(("ул. Школьная", "12"), Addresses.Split("ул. Школьная, 12"));
        Assert.Equal(("Гагарина ул.", "16/1"), Addresses.Split("Гагарина ул. 16/1"));
        Assert.Equal(("нет адреса", ""), Addresses.Split("нет адреса"));
        // Номера домов, как они записаны в старой системе
        Assert.All(new[] { "12", "12а", "16/1", "3-5", "48 А", "50 б" }, house => Assert.True(Addresses.IsHouseNumber(house)));
        Assert.All(new[] { "б/н", "дом", "А 48", "12  а" }, house => Assert.False(Addresses.IsHouseNumber(house)));
    }
}
