using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record AbonentFactDto(string FromPeriod, decimal Area, int Residents, string Who, string When, string? Reason);
public record ResidentDto(int Id, string Name, string? FromDate, string? ToDate);
public record AbonentDetailsDto(string? Note, bool? Consent, string? ConsentWhen, string? OwnershipDocument, string? Phone, string OpenPeriod, bool Reopened, List<AbonentFactDto> Facts, List<ResidentDto> Residents, decimal? HouseHeatNorm, string? HouseCategory = null,
    string? Email = null, string? BirthDate = null, string? Kpp = null, bool HasPassport = false);
public record SetNoteRequest(string? Note);
public record SetHeatNormRequest(decimal? Norm, string Reason);
public record SetHouseCategoryRequest(string? Category, string Reason);
public record SetMeterUnitRequest(string? Unit);
public record SetMeterRequest(string? Number, decimal LastReading, string? VerifiedUntil, decimal? Average, string? Unit);
public record SetHouseMeterRequest(string? HouseMeterId);
public record SetContactRequest(string? Phone);
public record SetLegalRequest(bool? Consent, string? OwnershipDocument);
public record SetFactRequest(string FromPeriod, decimal Area, int Residents, string Reason);
public record AddResidentRequest(string Name, DateOnly? FromDate);
public record EndResidentRequest(DateOnly ToDate);

public static class AbonentDetailEndpoints
{
    private const int MaxNote = 2000;

    public static void MapAbonentDetails(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/abonents/{id}").RequireAuthorization();

        group.MapGet("/details", async (string id, EmirDb db) =>
        {
            var abonent = await db.GetAbonent(id);
            var facts = await db.AbonentFacts.Where(fact => fact.AbonentId == id).ToListAsync();
            var residents = await db.Residents.Where(resident => resident.AbonentId == id).OrderBy(resident => resident.ToDate != null).ThenBy(resident => resident.Name).ToListAsync();
            var work = await Periods.Work(db);
            return new AbonentDetailsDto(abonent.Note, abonent.Consent, abonent.ConsentAtUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), abonent.OwnershipDocument, abonent.Phone, work.Period, work.ReopenedAtUtc is not null,
                facts.OrderByDescending(fact => fact.FromPeriod, StringComparer.Ordinal).Select(fact => new AbonentFactDto(fact.FromPeriod, fact.Area, fact.Residents, fact.Who, fact.AtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), fact.Reason)).ToList(),
                residents.Select(resident => new ResidentDto(resident.Id, resident.Name, resident.FromDate?.ToString("dd.MM.yyyy"), resident.ToDate?.ToString("dd.MM.yyyy"))).ToList(), abonent.HouseHeatNorm, abonent.HouseCategory,
                abonent.Email, abonent.BirthDate?.ToString("dd.MM.yyyy"), abonent.Kpp, abonent.PassportProtected is not null);
        });

        // Норма теплопотребления дома (Гкал/м²) для тарифов «по норме дома»: объём = площадь × норма.
        // История не ведётся: новое значение учтётся при следующем начислении открытого месяца
        group.MapPost("/heat-norm", async (string id, SetHeatNormRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите причину");
            if (request.Norm is { } norm && (norm <= 0 || norm > 10 || norm != Math.Round(norm, 6))) throw new ApiError(422, "Норматив указан неверно: число больше нуля, не больше 6 знаков после запятой");
            var abonent = await db.GetAbonent(id);
            var old = abonent.HouseHeatNorm;
            abonent.HouseHeatNorm = request.Norm;
            db.AddJournal(id, "Норма теплопотребления", user.Who(), $"{old?.ToString("0.######") ?? "нет"}, теперь {request.Norm?.ToString("0.######") ?? "нет"} Гкал/м²", request.Reason.Trim());
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Категория дома — из справочника «Категории домов». Норма теплопотребления берётся у категории, если она там задана
        group.MapPost("/house-category", async (string id, SetHouseCategoryRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите причину");
            var category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
            var item = category is null ? null : (await DirectoryData.HouseCategories(db)).FirstOrDefault(entry => entry.Name == category)
                ?? throw new ApiError(422, $"«{category}» нет в справочнике «{Catalog.HouseCategories}». Выберите категорию из списка.");
            var abonent = await db.GetAbonent(id);
            if (abonent.HouseCategory == category) return Results.NoContent();
            var old = abonent.HouseCategory;
            var oldNorm = abonent.HouseHeatNorm;
            abonent.HouseCategory = category;
            if (item?.Norm is { } norm) abonent.HouseHeatNorm = norm;
            var normText = abonent.HouseHeatNorm == oldNorm ? "" : $". Норма {oldNorm?.ToString("0.######") ?? "нет"}, теперь {abonent.HouseHeatNorm?.ToString("0.######") ?? "нет"} Гкал/м²";
            db.AddJournal(id, "Категория дома", user.Who(), $"{old ?? "нет"}, теперь {category ?? "нет"}{normText}", request.Reason.Trim());
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Заметка рабочая: её пишут все, кто работает с абонентом, включая кассу
        group.MapPost("/note", async (string id, SetNoteRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
            if (note?.Length > MaxNote) throw new ApiError(422, $"Заметка длиннее {MaxNote} символов");
            var abonent = await db.GetAbonent(id);
            abonent.Note = note;
            db.AddJournal(id, "Заметка", user.Who(), note ?? "Заметка удалена");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Cashier, Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapPost("/contact", async (string id, SetContactRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var phone = Registration.Phone(request.Phone);
            var abonent = await db.GetAbonent(id);
            var old = abonent.Phone;
            abonent.Phone = phone;
            db.AddJournal(id, "Телефон", user.Who(), $"{old ?? "нет"}, теперь {phone ?? "нет"}");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapPost("/legal", async (string id, SetLegalRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var document = string.IsNullOrWhiteSpace(request.OwnershipDocument) ? null : request.OwnershipDocument.Trim();
            if (document?.Length > MaxNote) throw new ApiError(422, $"Текст длиннее {MaxNote} символов");
            var abonent = await db.GetAbonent(id);
            var details = new List<string>();
            if (abonent.Consent != request.Consent)
            {
                abonent.Consent = request.Consent;
                abonent.ConsentAtUtc = DateTime.UtcNow;
                details.Add($"Согласие на передачу данных: {(request.Consent switch { true => "да", false => "нет", null => "не указано" })}");
            }
            if (abonent.OwnershipDocument != document)
            {
                abonent.OwnershipDocument = document;
                details.Add("Изменён документ о праве собственности");
            }
            if (details.Count > 0) db.AddJournal(id, "Данные абонента", user.Who(), string.Join(". ", details));
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapPost("/facts", async (string id, SetFactRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите причину");
            if (request.Area < 0 || request.Area > 1_000_000 || request.Area != Math.Round(request.Area, 4)) throw new ApiError(422, "Площадь указана неверно");
            if (request.Residents is < 0 or > 1000) throw new ApiError(422, "Число жильцов указано неверно");

            await using var transaction = await db.BeginMoneyTransaction();
            var abonent = await db.GetAbonent(id);
            await TariffHistory.EnsureCanChange(db, request.FromPeriod, abonent.Services.Where(line => line.ServiceId != "penalty").Select(line => (line.AbonentId, line.ServiceId)).ToList());

            var fact = await db.AbonentFacts.FirstOrDefaultAsync(item => item.AbonentId == id && item.FromPeriod == request.FromPeriod);
            if (fact is null)
            {
                fact = new AbonentFact { AbonentId = id, FromPeriod = request.FromPeriod, Who = user.Who() };
                db.AbonentFacts.Add(fact);
            }
            fact.Area = request.Area;
            fact.Residents = request.Residents;
            fact.Who = user.Who();
            fact.AtUtc = DateTime.UtcNow;
            fact.Reason = request.Reason.Trim();

            if (request.FromPeriod == await Periods.WorkPeriod(db))
            {
                abonent.Area = request.Area;
                abonent.Residents = request.Residents;
            }
            db.AddJournal(id, "Площадь и жильцы", user.Who(), $"С {request.FromPeriod}: {request.Area:0.####} м², жильцов {request.Residents}", request.Reason.Trim());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Установка или замена квартирного счётчика у услуги. Нельзя менять, если показание открытого месяца уже внесено
        group.MapPost("/services/{serviceId}/meter", async (string id, string serviceId, SetMeterRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var number = request.Number?.Trim();
            if (string.IsNullOrEmpty(number) || number.Length > 100) throw new ApiError(422, "Укажите номер счётчика");
            if (request.LastReading < 0 || request.LastReading > 100_000_000) throw new ApiError(422, "Начальное показание указано неверно");
            if (request.Average is < 0 or > 1_000_000) throw new ApiError(422, "Среднее указано неверно");
            var unit = MeterUnits.Parse(request.Unit);
            var abonent = await db.GetAbonent(id);
            var line = abonent.Services.FirstOrDefault(item => item.ServiceId == serviceId) ?? throw new ApiError(404, "Услуга не найдена");
            var period = await Periods.WorkPeriod(db);
            if (await db.PeriodReadings.AnyAsync(item => item.AbonentId == id && item.ServiceId == serviceId && item.Period == period))
                throw new ApiError(409, "За открытый месяц уже внесено показание. Сначала отмените его или используйте корректировку");
            var was = line.MeterNumber;
            line.MeterNumber = number;
            line.MeterLastReading = request.LastReading;
            line.MeterVerifiedUntil = string.IsNullOrWhiteSpace(request.VerifiedUntil) ? null : request.VerifiedUntil.Trim();
            line.MeterAverage = request.Average ?? line.MeterAverage;
            line.MeterUnit = unit;
            db.AddJournal(id, was is null ? "Счётчик установлен" : "Счётчик заменён", user.Who(),
                $"{line.Name}: {(was is null ? "" : was + " → ")}{number}, старт {request.LastReading:0.####}{(unit is null ? "" : ", " + unit)}");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Снятие квартирного счётчика: услуга переходит на расчёт по нормативу
        group.MapPost("/services/{serviceId}/meter/remove", async (string id, string serviceId, EmirDb db, ClaimsPrincipal user) =>
        {
            var abonent = await db.GetAbonent(id);
            var line = abonent.Services.FirstOrDefault(item => item.ServiceId == serviceId) ?? throw new ApiError(404, "Услуга не найдена");
            if (line.MeterNumber is null) return Results.NoContent();
            var period = await Periods.WorkPeriod(db);
            if (await db.PeriodReadings.AnyAsync(item => item.AbonentId == id && item.ServiceId == serviceId && item.Period == period))
                throw new ApiError(409, "За открытый месяц уже внесено показание. Сначала отмените его или используйте корректировку");
            var was = line.MeterNumber;
            line.MeterNumber = null;
            line.MeterLastReading = null;
            line.MeterVerifiedUntil = null;
            line.MeterUnit = null;
            db.AddJournal(id, "Счётчик снят", user.Who(), $"{line.Name}: снят счётчик {was}");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Единица показаний квартирного счётчика. Если показание открытого месяца уже внесено, оно пересчитывается в новой единице
        group.MapPost("/services/{serviceId}/meter-unit", async (string id, string serviceId, SetMeterUnitRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var unit = MeterUnits.Parse(request.Unit);
            await using var transaction = await db.BeginMoneyTransaction();
            var abonent = await db.GetAbonent(id);
            var line = abonent.Services.FirstOrDefault(item => item.ServiceId == serviceId) ?? throw new ApiError(404, "Услуга не найдена");
            if (line.MeterNumber is null) throw new ApiError(422, $"У услуги «{line.Name}» нет счётчика");
            if (line.MeterUnit == unit) return Results.NoContent();
            var old = line.MeterUnit;
            line.MeterUnit = unit;
            var period = await Periods.WorkPeriod(db);
            var reading = await db.PeriodReadings.FirstOrDefaultAsync(item => item.AbonentId == id && item.ServiceId == serviceId && item.Period == period);
            if (reading is not null)
            {
                // Пересчёт меняет долг: проводка в денежный журнал идёт как операция с показанием
                await db.BeginOperation("reading", user.Who(), line.MeterNumber);
                Billing.ApplyReading(db, abonent, line, reading.Value);
            }
            db.AddJournal(id, "Единица счётчика", user.Who(), $"{line.Name}, счётчик {line.MeterNumber}: {old ?? "единицы тарифа"}, теперь {unit ?? "единицы тарифа"}");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Привязка услуги к общедомовому счётчику: нужна тарифам «по общедомовому счётчику»
        group.MapPost("/services/{serviceId}/house-meter", async (string id, string serviceId, SetHouseMeterRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var abonent = await db.GetAbonent(id);
            var line = abonent.Services.FirstOrDefault(item => item.ServiceId == serviceId) ?? throw new ApiError(404, "Услуга не найдена");
            var meterId = string.IsNullOrWhiteSpace(request.HouseMeterId) ? null : request.HouseMeterId;
            var meter = meterId is null ? null : await db.HouseMeters.FirstOrDefaultAsync(item => item.Id == meterId) ?? throw new ApiError(404, "Общедомовой счётчик не найден");
            if (line.HouseMeterId == meterId) return Results.NoContent();
            line.HouseMeterId = meterId;
            db.AddJournal(id, "Общедомовой счётчик услуги", user.Who(), $"{line.Name}: {(meter is null ? "отвязана от счётчика" : $"{meter.Place}, {meter.Street}, № {meter.Number}")}");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapPost("/residents", async (string id, AddResidentRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 200) throw new ApiError(422, "Укажите ФИО");
            await db.GetAbonent(id);
            db.Residents.Add(new Resident { AbonentId = id, Name = name, FromDate = request.FromDate });
            db.AddJournal(id, "Проживающий добавлен", user.Who(), $"{name}{(request.FromDate is { } from ? $", с {from:dd.MM.yyyy}" : "")}");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Запись закрывается датой, а не удаляется: история проживания нужна для расчётов и справок
        group.MapPost("/residents/{residentId:int}/end", async (string id, int residentId, EndResidentRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var resident = await db.Residents.FirstOrDefaultAsync(item => item.Id == residentId && item.AbonentId == id) ?? throw new ApiError(404, "Запись не найдена");
            if (resident.ToDate is not null) throw new ApiError(409, "Проживание уже завершено");
            if (resident.FromDate is { } from && request.ToDate < from) throw new ApiError(422, "Дата выбытия раньше даты вселения");
            resident.ToDate = request.ToDate;
            db.AddJournal(id, "Проживающий выбыл", user.Who(), $"{resident.Name}, {request.ToDate:dd.MM.yyyy}");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);
    }
}
