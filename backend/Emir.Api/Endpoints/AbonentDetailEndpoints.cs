using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record AbonentFactDto(string FromPeriod, decimal Area, int Residents, string Who, string When, string? Reason);
public record ResidentDto(int Id, string Name, string? FromDate, string? ToDate);
public record AbonentDetailsDto(string? Note, bool? Consent, string? ConsentWhen, string? OwnershipDocument, string? Phone, string OpenPeriod, bool Reopened, List<AbonentFactDto> Facts, List<ResidentDto> Residents);
public record SetNoteRequest(string? Note);
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
                residents.Select(resident => new ResidentDto(resident.Id, resident.Name, resident.FromDate?.ToString("dd.MM.yyyy"), resident.ToDate?.ToString("dd.MM.yyyy"))).ToList());
        });

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
            var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
            if (phone?.Length > 50) throw new ApiError(422, "Телефон слишком длинный");
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
