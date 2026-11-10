using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

/// <summary>Связи справочников: адресная цепочка Город → Улица → Дом, «Типы домов» и единая категория дома.</summary>
public sealed class DirectoryLinkTests : IDisposable
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

    private static string J(object value) => JsonSerializer.Serialize(value);

    private async Task AddTable(string name, string[] columns, params string[][] rows)
    {
        await using var db = Db();
        // Справочники адресов приложение заводит при запуске: тест ставит свои
        db.ReferenceTables.RemoveRange(db.ReferenceTables.Where(item => item.Name == name));
        db.ReferenceTables.Add(new ReferenceTable { Name = name, ColumnsJson = J(columns), RowsJson = J(rows), RowCount = rows.Length });
        await db.SaveChangesAsync();
    }

    private async Task<List<List<string>>> Rows(string name)
    {
        await using var db = Db();
        return DirectoryData.Rows(await db.ReferenceTables.FirstAsync(item => item.Name == name));
    }

    private static string Q(string value) => Uri.EscapeDataString(value);

    private async Task AddressChain()
    {
        await AddTable("Города", ["ID", "Наименование"], ["1", "Журавка"], ["2", "Степное"]);
        await AddTable("Улицы", ["Населённый пункт", "Улица"], ["Журавка", "Центральная"], ["Журавка", "Морозова"], ["Степное", "Школьная"]);
        await AddTable("Дома", ["ID", "Город", "Улица", "Дом"], ["1", "Журавка", "Центральная", "1"], ["2", "Журавка", "Морозова", "45"]);
        await AddTable("Квартиры", ["№", "Город", "Улица", "Дом", "Квартира"], ["1", "Журавка", "Морозова", "45", "1"]);
    }

    [Fact]
    public async Task AddressChain_StreetsFilteredByCity()
    {
        var client = await LoginAs("admin");
        await AddressChain();

        // таблица сообщает каскад: улица берётся из «Улиц» по городу этой записи
        var table = await client.GetFromJsonAsync<JsonElement>($"/api/directories/table?name={Q("Дома")}");
        var street = table.GetProperty("references").GetProperty("Улица");
        Assert.Equal("Улицы", street.GetProperty("source").GetString());
        Assert.Equal("Город", street.GetProperty("where").GetProperty("Населённый пункт").GetString());

        // список улиц — только улицы выбранного города
        var options = await client.GetFromJsonAsync<string[]>($"/api/directories/options?name={Q("Улицы")}&column={Q("Улица")}&where={Q(J(new Dictionary<string, string> { ["Населённый пункт"] = "Журавка" }))}");
        Assert.Equal(new[] { "Морозова", "Центральная" }, options);

        // улица другого города не принимается, своя — принимается
        var bad = await client.PostAsJsonAsync("/api/directories/rows", new { name = "Дома", row = new[] { "3", "Журавка", "Школьная", "2" }, reason = "тест" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Contains("Журавка", (await bad.Content.ReadAsStringAsync()));
        var ok = await client.PostAsJsonAsync("/api/directories/rows", new { name = "Дома", row = new[] { "3", "Степное", "Школьная", "2" }, reason = "тест" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        // улицу без города не выбрать
        var noCity = await client.PostAsJsonAsync("/api/directories/rows", new { name = "Дома", row = new[] { "4", "", "Школьная", "3" }, reason = "тест" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noCity.StatusCode);

        // смена города при той же улице проверяет улицу заново
        var moved = await client.PostAsJsonAsync("/api/directories/rows/0", new { name = "Дома", expected = new[] { "1", "Журавка", "Центральная", "1" }, row = new[] { "1", "Степное", "Центральная", "1" }, reason = "тест" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, moved.StatusCode);

        // дом в квартире — из домов выбранной улицы
        var flat = await client.PostAsJsonAsync("/api/directories/rows", new { name = "Квартиры", row = new[] { "2", "Журавка", "Центральная", "45", "2" }, reason = "тест" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, flat.StatusCode);
        var flatOk = await client.PostAsJsonAsync("/api/directories/rows", new { name = "Квартиры", row = new[] { "2", "Журавка", "Центральная", "1", "2" }, reason = "тест" });
        Assert.Equal(HttpStatusCode.NoContent, flatOk.StatusCode);
    }

    [Fact]
    public async Task AddressChain_RenameSpreadsAndUsedValueCannotBeDeleted()
    {
        var client = await LoginAs("admin");
        await AddressChain();

        // переименование города расходится по улицам, домам и квартирам
        (await client.PostAsJsonAsync("/api/directories/rows/0", new { name = "Города", expected = new[] { "1", "Журавка" }, row = new[] { "1", "с. Журавка" }, reason = "переименование" })).EnsureSuccessStatusCode();
        Assert.All((await Rows("Улицы")).Where(row => row[1] != "Школьная"), row => Assert.Equal("с. Журавка", row[0]));
        Assert.All(await Rows("Дома"), row => Assert.Equal("с. Журавка", row[1]));
        Assert.Equal("с. Журавка", (await Rows("Квартиры"))[0][1]);

        // переименование улицы — только в своём городе
        (await client.PostAsJsonAsync("/api/directories/rows/1", new { name = "Улицы", expected = new[] { "с. Журавка", "Морозова" }, row = new[] { "с. Журавка", "ул. Морозова" }, reason = "переименование" })).EnsureSuccessStatusCode();
        Assert.Equal("ул. Морозова", (await Rows("Дома"))[1][2]);
        Assert.Equal("Центральная", (await Rows("Дома"))[0][2]);
        Assert.Equal("ул. Морозова", (await Rows("Квартиры"))[0][2]);

        // улицу, на которой есть дома, удалить нельзя
        var delete = await client.PostAsJsonAsync("/api/directories/rows/0/delete", new { name = "Улицы", expected = new[] { "с. Журавка", "Центральная" }, reason = "ошибка" });
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(3, (await Rows("Улицы")).Count);

        // неиспользуемую — можно
        (await client.PostAsJsonAsync("/api/directories/rows/2/delete", new { name = "Улицы", expected = new[] { "Степное", "Школьная" }, reason = "ошибка" })).EnsureSuccessStatusCode();
        Assert.Equal(2, (await Rows("Улицы")).Count);
    }

    [Fact]
    public async Task HouseTypes_CollectedFromAbonentsAndLinked()
    {
        var client = await LoginAs("admin");
        await AddTable("Абоненты ЖКХ", ["ЛС", "Тип"], ["1", "Многоквартирный"], ["2", "Индивидуальный"], ["3", "Многоквартирный"]);
        await using (var db = Db()) await DirectoryData.EnsureHouseTypes(db);

        Assert.Equal(new[] { "Индивидуальный", "Многоквартирный" }, (await Rows("Типы домов")).Select(row => row[1]));
        var groups = await client.GetFromJsonAsync<JsonElement>("/api/directories/groups");
        Assert.Contains(groups.EnumerateArray().SelectMany(group => group.GetProperty("items").EnumerateArray()), item => item.GetString() == "Типы домов");

        var table = await client.GetFromJsonAsync<JsonElement>($"/api/directories/table?name={Q("Абоненты ЖКХ")}");
        Assert.Equal("Типы домов", table.GetProperty("references").GetProperty("Тип").GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/directories/rows/1", new { name = "Абоненты ЖКХ", expected = new[] { "2", "Индивидуальный" }, row = new[] { "2", "Дворец" }, reason = "тест" })).StatusCode);

        // уже заведённый справочник второй раз не пересобирается
        (await client.PostAsJsonAsync("/api/directories/rows", new { name = "Типы домов", row = new[] { "3", "Нежилой" }, reason = "новый тип" })).EnsureSuccessStatusCode();
        await using (var db = Db()) await DirectoryData.EnsureHouseTypes(db);
        Assert.Equal(3, (await Rows("Типы домов")).Count);
    }

    private async Task Categories()
    {
        await AddTable("Категории домов", ["ID", "Название", "Норма теплопотребления"], ["1", "1-этажные", ".01875"], ["2", "2-этажные", ".01725"], ["3", "Новая", ""]);
        await using var db = Db();
        foreach (var id in new[] { "a1", "a2" })
        {
            var abonent = await db.Abonents.FirstAsync(item => item.Id == id);
            abonent.HouseCategory = "1-этажные";
            abonent.HouseHeatNorm = 0.01875m;
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task HouseCategory_OfAbonentComesFromDirectory()
    {
        var oper = await LoginAs("oper1");
        await Categories();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync("/api/abonents/a1/house-category", new { category = "Дворец", reason = "акт" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await oper.PostAsJsonAsync("/api/abonents/a1/house-category", new { category = "2-этажные", reason = "" })).StatusCode);

        // смена категории берёт норму категории
        (await oper.PostAsJsonAsync("/api/abonents/a1/house-category", new { category = "2-этажные", reason = "акт обследования" })).EnsureSuccessStatusCode();
        var details = await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a1/details");
        Assert.Equal("2-этажные", details.GetProperty("houseCategory").GetString());
        Assert.Equal(0.01725m, details.GetProperty("houseHeatNorm").GetDecimal());

        // у категории без нормы прежняя норма абонента остаётся
        (await oper.PostAsJsonAsync("/api/abonents/a1/house-category", new { category = "Новая", reason = "акт" })).EnsureSuccessStatusCode();
        Assert.Equal(0.01725m, (await oper.GetFromJsonAsync<JsonElement>("/api/abonents/a1/details")).GetProperty("houseHeatNorm").GetDecimal());

        var cashier = await LoginAs("kassa1");
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/abonents/a1/house-category", new { category = "1-этажные", reason = "акт" })).StatusCode);
    }

    [Fact]
    public async Task HouseCategory_DirectoryIsTheListForHeatNorms()
    {
        var buh = await LoginAs("buh1");
        await Categories();

        // в нормах — все категории справочника, даже без абонентов, с нормой из справочника
        var norms = await buh.GetFromJsonAsync<JsonElement>("/api/heat-norms");
        var rows = norms.GetProperty("categories").EnumerateArray().ToDictionary(row => row.GetProperty("category").GetString()!);
        Assert.Equal(new[] { "1-этажные", "2-этажные", "Новая" }, rows.Keys.Order(StringComparer.CurrentCulture));
        Assert.Equal(2, rows["1-этажные"].GetProperty("abonents").GetInt32());
        Assert.Equal(0.01725m, rows["2-этажные"].GetProperty("norm").GetDecimal());

        // норма, изменённая в справочнике бухгалтером, проставляется абонентам категории
        (await buh.PostAsJsonAsync("/api/directories/rows/0", new { name = "Категории домов", expected = new[] { "1", "1-этажные", ".01875" }, row = new[] { "1", "1-этажные", "0,019" }, reason = "приказ 5" })).EnsureSuccessStatusCode();
        await using (var db = Db())
            Assert.All(await db.Abonents.Where(item => item.HouseCategory == "1-этажные").ToListAsync(), abonent => Assert.Equal(0.019m, abonent.HouseHeatNorm));

        // абонентский отдел норму в справочнике не меняет
        var oper = await LoginAs("oper1");
        Assert.Equal(HttpStatusCode.Forbidden, (await oper.PostAsJsonAsync("/api/directories/rows/1", new { name = "Категории домов", expected = new[] { "2", "2-этажные", ".01725" }, row = new[] { "2", "2-этажные", "0,02" }, reason = "x" })).StatusCode);

        // переименование категории переносится абонентам
        (await buh.PostAsJsonAsync("/api/directories/rows/0", new { name = "Категории домов", expected = new[] { "1", "1-этажные", "0,019" }, row = new[] { "1", "1-этажные дома", "0,019" }, reason = "уточнение" })).EnsureSuccessStatusCode();
        await using (var db = Db())
        {
            Assert.Equal(2, await db.Abonents.CountAsync(item => item.HouseCategory == "1-этажные дома"));
            Assert.Equal(0, await db.Abonents.CountAsync(item => item.HouseCategory == "1-этажные"));
        }

        // категорию, которая стоит у абонентов, удалить нельзя
        Assert.Equal(HttpStatusCode.Conflict, (await buh.PostAsJsonAsync("/api/directories/rows/0/delete", new { name = "Категории домов", expected = new[] { "1", "1-этажные дома", "0,019" }, reason = "x" })).StatusCode);
    }
}
