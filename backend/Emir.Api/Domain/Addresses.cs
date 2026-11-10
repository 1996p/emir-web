using System.Security.Claims;
using System.Text.RegularExpressions;
using Emir.Api.Data;
using Emir.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

/// <summary>Адрес абонента из справочников: город из «Городов», улица из «Улиц» этого города, дом из «Домов» этой улицы. Квартира вводится руками.</summary>
public record AddressParts(string Place, string Street, string House, string? Flat)
{
    /// <summary>Улица и дом одной строкой, как в старой системе: «ул. Школьная 12».</summary>
    public string StreetLine => Addresses.Compose(Street, House);
}

/// <summary>Адресная цепочка Город → Улица → Дом. Значения хранятся в справочниках старой системы (ReferenceTables), абонент ссылается на них текстом.</summary>
public static partial class Addresses
{
    public const string Cities = "Города", CityName = "Наименование";
    public const string Streets = "Улицы", StreetCity = "Населённый пункт", StreetName = "Улица";
    public const string Houses = "Дома", HouseCity = "Город", HouseStreet = "Улица", HouseNumber = "Дом";
    private const int MaxName = 200, MaxHouse = 20, MaxFlat = 20;

    public static string Compose(string street, string house) => house == "" ? street : $"{street} {house}";

    /// <summary>«ул. Школьная, 12» и «Гагарина ул. 29» → улица и номер дома. Без номера в конце: вся строка — улица.</summary>
    public static (string Street, string House) Split(string? line)
    {
        var text = (line ?? "").Trim();
        var match = TrailingHouse().Match(text);
        return match.Success ? (match.Groups[1].Value.Trim().TrimEnd(','), match.Groups[2].Value) : (text, "");
    }

    [GeneratedRegex(@"^(.*?\S)[,\s]+(\d[^\s,]*)$")]
    private static partial Regex TrailingHouse();

    /// <summary>Номер дома: начинается с цифры, дальше цифры, буквы, «/» и «-»: 12, 12а, 16/1, 3-5.
    /// Как в старой системе, допускается одна литера или корпус через пробел: «48 А», «50 б».</summary>
    [GeneratedRegex(@"^\d[\p{L}\d/\-]*( [\p{L}\d/\-]{1,5})?$")]
    private static partial Regex HousePattern();

    [GeneratedRegex(@"[\s.,]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // Сокращения типов, которые в старой системе пишутся то перед названием («ул. Мира»), то после («Мира ул.»)
    private static readonly HashSet<string> TypeWords = ["с", "п", "рп", "р", "пгт", "пос", "д", "ст", "х", "г", "ул", "пер", "пр", "пр-т", "проспект", "кв-л", "мкр", "мкр-н", "ш", "б-р", "пл", "туп", "проезд", "тер"];

    /// <summary>Название без типа, регистра, точек и запятых: «рп. Чистоозерное» и «Чистоозерное рп.» дают одно и то же.</summary>
    public static string Plain(string? text) =>
        string.Join(' ', Separators().Split((text ?? "").ToLowerInvariant().Replace('ё', 'е')).Where(word => word != "" && !TypeWords.Contains(word)));

    /// <summary>Ключ дома для группировки (показания, сверки): одинаков для «ул. Школьная, 12» и «Школьная ул. 12».</summary>
    public static string HouseKey(string place, string streetLine) => $"{Plain(place)}|{Plain(streetLine)}";

    public static bool IsHouseNumber(string house) => house.Length <= MaxHouse && HousePattern().IsMatch(house);

    private static string Clean(string? value) => Spaces().Replace(value ?? "", " ").Trim();

    public static async Task<List<string>> CityList(EmirDb db) => await Options(db, Cities, CityName, null);
    public static async Task<List<string>> StreetList(EmirDb db, string place) => await Options(db, Streets, StreetName, new() { [StreetCity] = place });
    public static async Task<List<string>> HouseList(EmirDb db, string place, string street) =>
        Sorted(await Options(db, Houses, HouseNumber, new() { [HouseCity] = place, [HouseStreet] = street }));

    /// <summary>Дома по порядку номеров: 2 раньше 10.</summary>
    private static List<string> Sorted(List<string> houses) =>
        houses.OrderBy(house => int.TryParse(new string(house.TakeWhile(char.IsDigit).ToArray()), out var number) ? number : int.MaxValue)
            .ThenBy(house => house, StringComparer.CurrentCulture).ToList();

    private static async Task<List<string>> Options(EmirDb db, string name, string column, Dictionary<string, string>? where)
    {
        var table = await db.ReferenceTables.AsNoTracking().FirstOrDefaultAsync(item => item.Name == name);
        if (table is null) return [];
        var columns = DirectoryData.Columns(table);
        if (!columns.Contains(column) || (where?.Keys.Any(key => !columns.Contains(key)) ?? false)) return [];
        return DirectoryData.Options(columns, DirectoryData.Rows(table), column, where);
    }

    /// <summary>Проверяет адрес по справочникам. Все части обязательны, кроме квартиры.</summary>
    public static async Task<AddressParts> Validate(EmirDb db, string? place, string? street, string? house, string? flat)
    {
        var city = Clean(place);
        var streetName = Clean(street);
        var number = Clean(house);
        var flatText = Clean(flat);
        if (city == "") throw new ApiError(422, "Выберите населённый пункт");
        if (streetName == "") throw new ApiError(422, "Выберите улицу");
        if (number == "") throw new ApiError(422, "Выберите дом");
        if (flatText.Length > MaxFlat) throw new ApiError(422, $"Квартира — не длиннее {MaxFlat} символов");
        if (!(await CityList(db)).Contains(city)) throw new ApiError(422, $"Населённого пункта «{city}» нет в справочнике «{Cities}»");
        if (!(await StreetList(db, city)).Contains(streetName)) throw new ApiError(422, $"Улицы «{streetName}» нет в справочнике «{Streets}» для «{city}». Сначала добавьте улицу");
        if (!(await HouseList(db, city, streetName)).Contains(number)) throw new ApiError(422, $"Дома «{number}» нет в справочнике «{Houses}» для «{city}, {streetName}». Сначала добавьте дом");
        return new AddressParts(city, streetName, number, flatText == "" ? null : flatText);
    }

    /// <summary>Добавляет улицу в справочник «Улицы» для населённого пункта.</summary>
    public static async Task AddStreet(EmirDb db, ClaimsPrincipal user, string? place, string? street, string? reason)
    {
        var city = Clean(place);
        var name = Clean(street);
        var why = Reason(reason);
        if (name == "" || name.Length > MaxName) throw new ApiError(422, "Укажите название улицы, не длиннее 200 символов");
        if (!name.Any(char.IsLetter)) throw new ApiError(422, "В названии улицы должны быть буквы");
        if (!(await CityList(db)).Contains(city)) throw new ApiError(422, $"Сначала выберите населённый пункт из справочника «{Cities}»");
        await AddRow(db, user, Streets, new() { [StreetCity] = city, [StreetName] = name }, [StreetCity, StreetName], why,
            $"Улица «{name}» в «{city}» уже есть в справочнике");
    }

    /// <summary>Добавляет дом в справочник «Дома» для улицы населённого пункта.</summary>
    public static async Task AddHouse(EmirDb db, ClaimsPrincipal user, string? place, string? street, string? house, string? reason)
    {
        var city = Clean(place);
        var streetName = Clean(street);
        var number = Clean(house);
        var why = Reason(reason);
        if (!IsHouseNumber(number)) throw new ApiError(422, "Номер дома — с цифры, например: 12, 12а, 16/1, 48 А");
        if (!(await StreetList(db, city)).Contains(streetName)) throw new ApiError(422, $"Сначала выберите улицу из справочника «{Streets}»");
        await AddRow(db, user, Houses, new() { [HouseCity] = city, [HouseStreet] = streetName, [HouseNumber] = number }, [HouseCity, HouseStreet, HouseNumber], why,
            $"Дом «{number}» на улице «{streetName}» уже есть в справочнике");
    }

    private static string Reason(string? reason)
    {
        var why = Clean(reason);
        return why == "" ? throw new ApiError(422, "Укажите причину") : why;
    }

    /// <summary>Новая строка справочника по названиям колонок. Колонка «ID» получает следующий номер. Дубль по ключевым колонкам (без учёта регистра) запрещён.</summary>
    private static async Task AddRow(EmirDb db, ClaimsPrincipal user, string name, Dictionary<string, string> values, string[] keys, string reason, string duplicate)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var table = await db.ReferenceTables.FromSqlInterpolated($"SELECT * FROM \"ReferenceTables\" WHERE \"Name\" = {name} FOR UPDATE").FirstOrDefaultAsync()
            ?? throw new ApiError(404, $"Справочник «{name}» не заведён");
        var columns = DirectoryData.Columns(table);
        if (keys.Any(key => !columns.Contains(key))) throw new ApiError(422, $"В справочнике «{name}» нет колонок {string.Join(", ", keys)}");
        var rows = DirectoryData.Rows(table);
        var at = keys.Select(key => columns.IndexOf(key)).ToList();
        if (rows.Any(row => at.Zip(keys).All(pair => string.Equals(DirectoryData.Cell(row, pair.First), values[pair.Second], StringComparison.CurrentCultureIgnoreCase))))
            throw new ApiError(409, duplicate);
        var idAt = columns.IndexOf("ID");
        var row = columns.Select(column => values.GetValueOrDefault(column, "")).ToList();
        if (idAt >= 0) row[idAt] = (rows.Select(item => long.TryParse(DirectoryData.Cell(item, idAt), out var id) ? id : 0).DefaultIfEmpty(0).Max() + 1).ToString();
        rows.Add(row);
        DirectoryData.SetRows(table, rows);
        db.AddJournal(null, $"Справочник «{name}»", user.Who(), "Добавлено: " + string.Join("; ", keys.Select(key => $"{key}: {values[key]}")), reason);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    /// <summary>Справочники адресов нужны для регистрации абонентов. Если их нет (новая база, разработка), собираем из адресов абонентов. Заведённые не трогаем.</summary>
    public static async Task EnsureTables(EmirDb db)
    {
        var existing = await db.ReferenceTables.Where(item => item.Name == Cities || item.Name == Streets || item.Name == Houses).Select(item => item.Name).ToListAsync();
        if (existing.Count == 3) return;
        var addresses = (await db.Abonents.AsNoTracking().Select(abonent => new { abonent.Place, abonent.Street, abonent.StreetName, abonent.House }).ToListAsync())
            .Where(item => item.Place.Trim() != "")
            .Select(item =>
            {
                var (street, house) = item.StreetName is null ? Split(item.Street) : (item.StreetName, item.House ?? "");
                return (Place: Clean(item.Place), Street: Clean(street), House: Clean(house));
            })
            .Where(item => item.Street != "")
            .ToList();

        static List<List<string>> Numbered(IEnumerable<List<string>> rows) => rows.Select((row, i) => (List<string>)[(i + 1).ToString(), .. row]).ToList();
        if (!existing.Contains(Cities))
            Add(db, Cities, ["ID", CityName, "Сельсовет"], Numbered(addresses.Select(item => item.Place).Distinct().Order(StringComparer.CurrentCulture).Select(place => new List<string> { place, "" })));
        if (!existing.Contains(Streets))
            Add(db, Streets, [StreetCity, StreetName], addresses.Select(item => (item.Place, item.Street)).Distinct().OrderBy(item => item.Place, StringComparer.CurrentCulture).ThenBy(item => item.Street, StringComparer.CurrentCulture).Select(item => new List<string> { item.Place, item.Street }).ToList());
        if (!existing.Contains(Houses))
            Add(db, Houses, ["ID", HouseCity, HouseStreet, HouseNumber, "Котельная", "Категория дома", "Материал стен"],
                Numbered(addresses.Where(item => item.House != "").Select(item => (item.Place, item.Street, item.House)).Distinct().OrderBy(item => item.Place, StringComparer.CurrentCulture).ThenBy(item => item.Street, StringComparer.CurrentCulture).ThenBy(item => item.House, StringComparer.CurrentCulture)
                    .Select(item => new List<string> { item.Place, item.Street, item.House, "", "", "" })));
        await db.SaveChangesAsync();
    }

    private static void Add(EmirDb db, string name, string[] columns, List<List<string>> rows)
    {
        var table = new ReferenceTable { Name = name, ColumnsJson = System.Text.Json.JsonSerializer.Serialize(columns, DirectoryData.Plain), RowsJson = "[]", LoadedAtUtc = DateTime.UtcNow };
        DirectoryData.SetRows(table, rows);
        db.ReferenceTables.Add(table);
    }

    public record ImportResult(int Lines, int CitiesAdded, int StreetsAdded, int HousesAdded, int Existing, List<string> Problems, bool Applied);

    /// <summary>Загружает адреса из CSV «Населённый пункт;Улица;Дом[;…;ГИС ЖКХ;ФИАС]» в справочники «Города», «Улицы», «Дома».
    /// Только добавляет недостающее, ничего не удаляет и не переименовывает. Без apply — пробный прогон.</summary>
    public static async Task<ImportResult> ImportCsv(EmirDb db, string file, bool apply)
    {
        await EnsureTables(db);
        var lines = (await File.ReadAllLinesAsync(file, System.Text.Encoding.UTF8)).Select(line => line.TrimStart('﻿')).ToList();
        if (lines.Count == 0) throw new ApiError(422, "Файл пустой");
        static List<string> Cells(string line) => line.Split(';').Select(cell => Clean(cell.Trim('"'))).ToList();
        var header = Cells(lines[0]);
        int placeAt = header.IndexOf("Населённый пункт"), streetAt = header.IndexOf("Улица"), houseAt = header.IndexOf("Дом");
        if (placeAt < 0 || streetAt < 0 || houseAt < 0) throw new ApiError(422, "Нужны колонки «Населённый пункт», «Улица» и «Дом»");
        int gisAt = header.IndexOf("ГИС ЖКХ"), fiasAt = header.IndexOf("ФИАС");

        var tables = await db.ReferenceTables.Where(item => item.Name == Cities || item.Name == Streets || item.Name == Houses).ToDictionaryAsync(item => item.Name);
        var cityTable = tables[Cities]; var streetTable = tables[Streets]; var houseTable = tables[Houses];
        var cityColumns = DirectoryData.Columns(cityTable); var streetColumns = DirectoryData.Columns(streetTable); var houseColumns = DirectoryData.Columns(houseTable);
        var cityRows = DirectoryData.Rows(cityTable); var streetRows = DirectoryData.Rows(streetTable); var houseRows = DirectoryData.Rows(houseTable);
        string Key(params string[] parts) => string.Join("|", parts.Select(part => part.ToLowerInvariant()));
        HashSet<string> Keys(List<string> columns, List<List<string>> rows, params string[] names) =>
            rows.Select(row => Key(names.Select(name => DirectoryData.Cell(row, columns.IndexOf(name))).ToArray())).ToHashSet();
        var cityKeys = Keys(cityColumns, cityRows, CityName);
        var streetKeys = Keys(streetColumns, streetRows, StreetCity, StreetName);
        var houseKeys = Keys(houseColumns, houseRows, HouseCity, HouseStreet, HouseNumber);
        List<string> NewRow(List<string> columns, List<List<string>> rows, Dictionary<string, string> values)
        {
            var row = columns.Select(column => values.GetValueOrDefault(column, "")).ToList();
            var idAt = columns.IndexOf("ID");
            if (idAt >= 0) row[idAt] = (rows.Select(item => long.TryParse(DirectoryData.Cell(item, idAt), out var id) ? id : 0).DefaultIfEmpty(0).Max() + 1).ToString();
            return row;
        }

        int cities = 0, streets = 0, houses = 0, existing = 0;
        var problems = new List<string>();
        for (var i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = Cells(lines[i]);
            string Cell(int at) => at >= 0 && at < cells.Count ? cells[at] : "";
            string place = Cell(placeAt), street = Cell(streetAt), house = Cell(houseAt);
            if (place == "" || street == "" || house == "") { problems.Add($"Строка {i + 1}: нет населённого пункта, улицы или дома"); continue; }
            if (!IsHouseNumber(house)) { problems.Add($"Строка {i + 1}: номер дома «{house}» не похож на номер (12, 12а, 16/1, 48 А)"); continue; }
            if (cityKeys.Add(Key(place))) { cityRows.Add(NewRow(cityColumns, cityRows, new() { [CityName] = place })); cities++; }
            if (streetKeys.Add(Key(place, street))) { streetRows.Add(NewRow(streetColumns, streetRows, new() { [StreetCity] = place, [StreetName] = street })); streets++; }
            if (houseKeys.Add(Key(place, street, house)))
            {
                var values = new Dictionary<string, string> { [HouseCity] = place, [HouseStreet] = street, [HouseNumber] = house };
                if (gisAt >= 0) values["ГИС ЖКХ"] = Cell(gisAt);
                if (fiasAt >= 0) values["ФИАС"] = Cell(fiasAt);
                houseRows.Add(NewRow(houseColumns, houseRows, values));
                houses++;
            }
            else existing++;
        }
        if (apply)
        {
            DirectoryData.SetRows(cityTable, cityRows);
            DirectoryData.SetRows(streetTable, streetRows);
            DirectoryData.SetRows(houseTable, houseRows);
            db.AddJournal(null, "Загрузка адресов", "Перенос данных", $"{Path.GetFileName(file)}: населённых пунктов +{cities}, улиц +{streets}, домов +{houses}");
            await db.SaveChangesAsync();
        }
        return new ImportResult(lines.Count - 1, cities, streets, houses, existing, problems, apply);
    }

    /// <summary>Правка справочника адресов расходится по абонентам: переименование меняет их адрес, удалить используемое значение нельзя.</summary>
    public static async Task<string?> Propagate(EmirDb db, string source, List<string> columns, List<List<string>> rows, List<string> old, List<string>? updated)
    {
        // Ключевые колонки записи: последняя — само значение, остальные — родители (город улицы, город и улица дома)
        string[] keys = source switch
        {
            Cities => [CityName],
            Streets => [StreetCity, StreetName],
            Houses => [HouseCity, HouseStreet, HouseNumber],
            _ => [],
        };
        if (keys.Length == 0) return null;
        var at = keys.Select(key => columns.IndexOf(key)).ToArray();
        if (at.Any(index => index < 0)) return null;
        var before = at.Select(index => DirectoryData.Cell(old, index)).ToArray();
        if (before.Any(value => value == "")) return null;
        var after = updated is null ? null : at.Select(index => DirectoryData.Cell(updated, index)).ToArray();
        if (after is not null && after.SequenceEqual(before)) return null;
        // Такой же адрес остался в другой записи: ссылки не осиротели
        if (rows.Any(row => at.Select(index => DirectoryData.Cell(row, index)).SequenceEqual(before))) return null;

        var query = db.Abonents.Where(abonent => abonent.Place == before[0]);
        if (source != Cities) query = query.Where(abonent => abonent.StreetName == before[1]);
        if (source == Houses) query = query.Where(abonent => abonent.House == before[2]);
        var abonents = await query.ToListAsync();
        if (abonents.Count == 0) return null;

        var label = keys.Length - 1;
        var parentsChanged = after is not null && !after.Take(label).SequenceEqual(before.Take(label));
        if (after is null || after[label] == "" || parentsChanged)
            throw new ApiError(409, $"«{string.Join(", ", before)}» — адрес {abonents.Count} абонентов. Сначала смените им адрес.");

        foreach (var abonent in abonents)
        {
            if (source == Cities) abonent.Place = after[label];
            else if (source == Streets) abonent.StreetName = after[label];
            else abonent.House = after[label];
            if (abonent.StreetName is not null) abonent.Street = Compose(abonent.StreetName, abonent.House ?? "");
        }
        return $"адрес изменён у {abonents.Count} абонентов";
    }
}
