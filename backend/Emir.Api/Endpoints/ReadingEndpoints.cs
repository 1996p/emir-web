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
