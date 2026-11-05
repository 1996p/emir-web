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
    private static string HouseKey(Abonent abonent) => $"{abonent.Place}|{abonent.Street}";

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
                    return new { meter.Id, meter.Place, meter.Street, meter.Number, meter.Unit, Previous = previous?.Value, Value = current?.Value, Lines = lines.GetValueOrDefault(meter.Id) };
                }),
            };
        });

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
                    return new HouseProgressDto(house.Key, house.First().Street.Replace("ул. ", ""), meters.Count(line => saved.Contains(line.AbonentId + "|" + line.ServiceId)), meters.Count);
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
}

public record HouseMeterReadingRequest(decimal Value);
