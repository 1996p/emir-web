using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record HouseProgressDto(string Id, string Name, int Done, int Total);
public record ReadingRowDto(string Id, string House, string Flat, string MeterNumber, string Service, decimal Previous, decimal Average, string Value);
public record SaveReadingRequest(string AbonentId, string ServiceId, decimal Value);

public static class ReadingEndpoints
{
    /// <summary>«ул. Школьная, 12» и «Школьная ул. 12» в одном посёлке — один дом: адреса старой системы записаны по-разному.</summary>
    private static string HouseKey(Abonent abonent) => Addresses.HouseKey(abonent.Place, abonent.Street);

    /// <summary>Название дома в списке: посёлок нужен, иначе одинаковые улицы разных сёл не различить.</summary>
    private static string HouseName(Abonent abonent) => abonent.Place.Trim() == "" ? abonent.Street : $"{abonent.Street}, {abonent.Place}";

    public static void MapReadings(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/readings").RequireAuthorization();

        group.MapGet("/house-meters", async (EmirDb db) =>
        {
            var period = await Periods.WorkPeriod(db);
            var meters = await db.HouseMeters.OrderBy(meter => meter.Place).ThenBy(meter => meter.Street).ToListAsync();
            var readings = await db.HouseMeterReadings.ToListAsync();
            var lines = await db.ServiceLines.Where(line => line.HouseMeterId != null).GroupBy(line => line.HouseMeterId!).Select(items => new { items.Key, Count = items.Count() }).ToDictionaryAsync(item => item.Key, item => item.Count);
            return new
            {
                period,
                rows = meters.Select(meter =>
                {
                    var previous = readings.Where(item => item.HouseMeterId == meter.Id && string.CompareOrdinal(item.Period, period) < 0).OrderByDescending(item => item.Period).FirstOrDefault();
                    var current = readings.FirstOrDefault(item => item.HouseMeterId == meter.Id && item.Period == period);
                    return new { meter.Id, meter.Place, meter.Street, meter.Number, meter.Unit, meter.UnitFactor, Previous = previous?.Value, Value = current?.Value, Lines = lines.GetValueOrDefault(meter.Id) };
                }),
            };
        });

        group.MapPost("/house-meters/new", async (SaveHouseMeterRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var meter = new HouseMeter { Id = "hm-" + Guid.NewGuid().ToString("N")[..12], Street = "" };
            var details = ApplyMeter(meter, request);
            db.HouseMeters.Add(meter);
            db.AddJournal(null, "Новый общедомовой счётчик", user.Who(), details);
            await db.SaveChangesAsync();
            return Results.Created($"/api/readings/house-meters/{meter.Id}", new { meter.Id });
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Адрес, номер и единица показаний. Единицу нельзя сменить, если по счётчику уже начислен открытый месяц
        group.MapPost("/house-meters/{id}/edit", async (string id, SaveHouseMeterRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var meter = await db.HouseMeters.FirstOrDefaultAsync(item => item.Id == id) ?? throw new ApiError(404, "Счётчик не найден");
            var before = DescribeMeter(meter);
            var oldUnit = meter.Unit;
            ApplyMeter(meter, request);
            if (meter.Unit != oldUnit)
            {
                var period = await Periods.WorkPeriod(db);
                var charged = await db.ServiceLines.Where(line => line.HouseMeterId == id)
                    .Join(db.ChargeEntries.Where(entry => entry.Period == period), line => new { line.AbonentId, line.ServiceId }, entry => new { entry.AbonentId, entry.ServiceId }, (line, entry) => entry).AnyAsync();
                if (charged) throw new ApiError(409, "По этому счётчику уже есть начисления месяца. Сначала отмените их, потом меняйте единицу");
            }
            var after = DescribeMeter(meter);
            if (before == after) return Results.NoContent();
            db.AddJournal(null, "Общедомовой счётчик изменён", user.Who(), $"Было: {before}. Стало: {after}");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapPost("/house-meters/{id}", async (string id, HouseMeterReadingRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var period = await Periods.WorkPeriod(db);
            if (!await db.HouseMeters.AnyAsync(meter => meter.Id == id)) throw new ApiError(404, "Счётчик не найден");
            var charged = await db.ServiceLines.Where(line => line.HouseMeterId == id)
                .Join(db.ChargeEntries.Where(entry => entry.Period == period), line => new { line.AbonentId, line.ServiceId }, entry => new { entry.AbonentId, entry.ServiceId }, (line, entry) => entry).AnyAsync();
            if (charged) throw new ApiError(409, "По этому счётчику уже есть начисления месяца. Сначала отмените их");
            var previous = await db.HouseMeterReadings.Where(item => item.HouseMeterId == id && string.Compare(item.Period, period) < 0).OrderByDescending(item => item.Period).FirstOrDefaultAsync();
            if (request.Value < 0 || (previous is not null && request.Value < previous.Value)) throw new ApiError(400, "Показание меньше предыдущего");
            var reading = await db.HouseMeterReadings.FirstOrDefaultAsync(item => item.HouseMeterId == id && item.Period == period);
            if (reading is null) db.HouseMeterReadings.Add(reading = new HouseMeterReading { HouseMeterId = id, Period = period });
            reading.Value = request.Value;
            reading.Who = user.Identity?.Name ?? "";
            reading.AtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Какие квартирные услуги можно привязать к этому счётчику: услуги схемы MeterHouse по его адресу плюс уже привязанные к нему
        group.MapGet("/house-meters/{id}/candidates", async (string id, EmirDb db) =>
        {
            var meter = await db.HouseMeters.FirstOrDefaultAsync(item => item.Id == id) ?? throw new ApiError(404, "Счётчик не найден");
            var period = await Periods.WorkPeriod(db);
            var meters = await db.HouseMeters.ToDictionaryAsync(item => item.Id);
            var tariffs = await db.Tariffs.ToDictionaryAsync(item => item.Id);
            var abonents = await db.Abonents.Include(abonent => abonent.Services).ToListAsync();
            var charged = await db.ServiceLines.Where(line => line.HouseMeterId == id)
                .Join(db.ChargeEntries.Where(entry => entry.Period == period), line => new { line.AbonentId, line.ServiceId }, entry => new { entry.AbonentId, entry.ServiceId }, (line, entry) => entry).AnyAsync();

            var candidates = abonents.SelectMany(abonent => abonent.Services.Select(line => (abonent, line)))
                // Адрес дома у абонента часто с суффиксом квартиры («ул. Пархоменко 1-1»), у счётчика — без него («ул. Пархоменко 1»)
                .Where(pair => pair.line.HouseMeterId == id
                    || (pair.abonent.Place == meter.Place
                        && (pair.abonent.Street == meter.Street || pair.abonent.Street.StartsWith(meter.Street + "-", StringComparison.Ordinal))
                        && pair.line.TariffId is not null && tariffs.TryGetValue(pair.line.TariffId, out var tariff) && tariff.Scheme == "MeterHouse"))
                .OrderBy(pair => pair.abonent.Flat, StringComparer.CurrentCulture).ThenBy(pair => pair.line.Name)
                .Select(pair => new LinkCandidateDto(
                    $"{pair.abonent.Id}:{pair.line.ServiceId}", pair.abonent.ShortName, pair.abonent.Flat ?? "—", pair.line.Name,
                    pair.line.HouseMeterId,
                    pair.line.HouseMeterId is { } linked && linked != id && meters.TryGetValue(linked, out var other) ? DescribeMeter(other) : null))
                .ToList();
            return new HouseMeterCandidatesDto(DescribeMeter(meter), charged, candidates);
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Массовая привязка/отвязка квартирных услуг к общедомовому счётчику. Меняет деление расхода, поэтому закрыта при начислениях месяца
        group.MapPost("/house-meters/{id}/link", async (string id, LinkLinesRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите основание");
            if (request.Keys.Count == 0) throw new ApiError(422, "Не выбрано ни одной услуги");
            var meter = await db.HouseMeters.FirstOrDefaultAsync(item => item.Id == id) ?? throw new ApiError(404, "Счётчик не найден");
            var period = await Periods.WorkPeriod(db);

            await using var transaction = await db.BeginMoneyTransaction();
            var charged = await db.ServiceLines.Where(line => line.HouseMeterId == id)
                .Join(db.ChargeEntries.Where(entry => entry.Period == period), line => new { line.AbonentId, line.ServiceId }, entry => new { entry.AbonentId, entry.ServiceId }, (line, entry) => entry).AnyAsync();
            if (charged) throw new ApiError(409, "По этому счётчику уже есть начисления месяца. Сначала отмените их, потом меняйте привязку");

            var wanted = request.Keys.Select(key => key.Split(':', 2)).Where(parts => parts.Length == 2).Select(parts => (AbonentId: parts[0], ServiceId: parts[1])).ToList();
            var changed = 0;
            foreach (var (abonentId, serviceId) in wanted)
            {
                var line = await db.ServiceLines.FirstOrDefaultAsync(item => item.AbonentId == abonentId && item.ServiceId == serviceId);
                if (line is null) continue;
                var target = request.Link ? id : null;
                if (line.HouseMeterId == target) continue;
                line.HouseMeterId = target;
                line.ChargeStatus = "Ждёт начисления";
                changed++;
            }
            if (changed == 0) throw new ApiError(409, request.Link ? "Выбранные услуги уже привязаны к этому счётчику" : "Выбранные услуги и так не привязаны");
            db.AddJournal(null, request.Link ? "Привязка к общедомовому счётчику" : "Отвязка от общедомового счётчика", user.Who(),
                $"{DescribeMeter(meter)}: {(request.Link ? "привязано" : "отвязано")} услуг: {changed}", request.Reason.Trim());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new { Changed = changed };
        }).Roles(Roles.Accountant, Roles.Admin);

        group.MapGet("/houses", async (EmirDb db) =>
        {
            var abonents = await db.Abonents.Include(abonent => abonent.Services).ToListAsync();
            var open = await Periods.WorkPeriod(db);
            var saved = await db.PeriodReadings.Where(reading => reading.Period == open).Select(reading => reading.AbonentId + "|" + reading.ServiceId).ToListAsync();
            return abonents.Where(abonent => abonent.Services.Any(line => line.MeterNumber is not null))
                .GroupBy(HouseKey)
                .Select(house =>
                {
                    var meters = house.SelectMany(abonent => abonent.Services.Where(line => line.MeterNumber is not null)).ToList();
                    return new HouseProgressDto(house.Key, HouseName(house.First()), meters.Count(line => saved.Contains(line.AbonentId + "|" + line.ServiceId)), meters.Count);
                })
                .OrderBy(house => house.Name)
                .ToList();
        });

        group.MapGet("/rows", async (string house, EmirDb db) =>
        {
            var abonents = (await db.Abonents.Include(abonent => abonent.Services).ToListAsync()).Where(abonent => HouseKey(abonent) == house).ToList();
            var open = await Periods.WorkPeriod(db);
            var saved = await db.PeriodReadings.Where(reading => reading.Period == open).ToListAsync();
            return abonents.SelectMany(abonent => abonent.Services.Where(line => line.MeterNumber is not null).Select(line =>
            {
                var reading = saved.FirstOrDefault(item => item.AbonentId == line.AbonentId && item.ServiceId == line.ServiceId);
                return new ReadingRowDto($"{line.AbonentId}:{line.ServiceId}", abonent.Street, abonent.Flat ?? "—", line.MeterNumber!, line.Name,
                    reading?.StartValue ?? line.MeterLastReading ?? 0, line.MeterAverage, reading?.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "");
            })).ToList();
        });

        group.MapPost("/import", async (IFormFile file, bool? apply, bool? includeHigh, int? expected, int? accountColumn, int? meterColumn, int? valueColumn, int? dateColumn, string? separator, EmirDb db, ClaimsPrincipal user) =>
        {
            if (file.Length == 0) throw new ApiError(422, "Файл пустой");
            if (file.Length > 5 * 1024 * 1024) throw new ApiError(422, "Файл больше 5 МБ");
            var fileName = Path.GetFileName(file.FileName);
            if (Path.GetExtension(fileName).ToLowerInvariant() is not (".csv" or ".txt")) throw new ApiError(422, "Поддерживаются файлы CSV и TXT");
            using var memory = new MemoryStream();
            await file.CopyToAsync(memory);
            var columns = new ReadingColumns(accountColumn ?? 1, meterColumn ?? 2, valueColumn ?? 3, dateColumn ?? 0, separator switch { "tab" => '\t', "comma" => ',', _ => ';' });

            await using var transaction = await db.BeginMoneyTransaction();
            var result = await ReadingImport.Run(db, ReadingImport.Decode(memory.ToArray()), columns, apply == true, includeHigh == true, expected, user.Who(), fileName);
            if (result.Applied)
            {
                db.AddJournal(null, "Загрузка показаний", user.Who(), $"{fileName}: записано {result.Saved} из {result.Lines.Count}, {result.Title}");
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            return result;
        }).DisableAntiforgery().Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapPost("/", async (SaveReadingRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            await using var transaction = await db.BeginMoneyTransaction();
            var abonent = await db.GetAbonent(request.AbonentId);
            var line = abonent.Services.FirstOrDefault(item => item.ServiceId == request.ServiceId && item.MeterNumber is not null)
                ?? throw new ApiError(404, "Счётчик не найден");
            await db.BeginOperation("reading", user.Who(), line.MeterNumber);
            Billing.ApplyReading(db, abonent, line, request.Value);
            db.AddJournal(abonent.Id, "Показание счётчика", user.Who(), $"{line.MeterNumber}: {request.Value:0.####}");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        }).Roles(Roles.Cashier, Roles.Service, Roles.Admin);
    }

    private static string DescribeMeter(HouseMeter meter) => $"{meter.Place}, {meter.Street}, № {(meter.Number == "" ? "—" : meter.Number)}, {meter.Unit}";

    private static string ApplyMeter(HouseMeter meter, SaveHouseMeterRequest request)
    {
        var street = request.Street?.Trim();
        if (string.IsNullOrEmpty(street) || street.Length > 200) throw new ApiError(422, "Укажите улицу и дом");
        var place = request.Place?.Trim() ?? "";
        var number = request.Number?.Trim() ?? "";
        if (place.Length > 200 || number.Length > 100) throw new ApiError(422, "Слишком длинное значение");
        var unit = MeterUnits.Parse(request.Unit) ?? MeterUnits.Gcal;
        meter.Place = place;
        meter.Street = street;
        meter.Number = number;
        meter.Unit = unit;
        meter.UnitFactor = MeterUnits.Factor(unit);
        return DescribeMeter(meter);
    }
}

public record HouseMeterReadingRequest(decimal Value);
public record SaveHouseMeterRequest(string? Place, string Street, string? Number, string? Unit);
/// <summary>LinkedToId — счётчик, к которому услуга привязана сейчас (null — ни к какому); LinkedToName — его описание, если это другой счётчик.</summary>
public record LinkCandidateDto(string Key, string Abonent, string Flat, string Service, string? LinkedToId, string? LinkedToName);
public record HouseMeterCandidatesDto(string Meter, bool ChargedThisMonth, List<LinkCandidateDto> Candidates);
public record LinkLinesRequest(List<string> Keys, bool Link, string Reason);
