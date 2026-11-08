using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

/// <summary>Tariff: цена открытого месяца в копейках, может быть дробной.</summary>
/// Unit: единица показаний; null — в единицах тарифа.
public record MeterDto(string Number, decimal LastReading, decimal Tariff, string VerifiedUntil, string? Unit);
public record ServiceLineDto(string Id, string Name, string State, string TariffName, decimal? Price, string ChargeStatus, long Debt, MeterDto? Meter, string? HouseMeterId = null, string? TariffId = null);
/// <summary>Street — улица и дом одной строкой. StreetName и House — части из справочников (нет у адресов, перенесённых строкой). HouseKey — дом на экране показаний.</summary>
public record AbonentDto(string Id, string Kind, string Name, string ShortName, string Place, string Street, string? Flat, string Account, string? Phone, string? Inn, int Residents, decimal Area, long Credit, List<ServiceLineDto> Services,
    string? StreetName = null, string? House = null, string? HouseKey = null);
public record PaymentDto(string Id, string AbonentId, string Date, string Source, string Cashier, long Amount, string Status);
public record JournalDto(string Id, string? AbonentId, string When, string Action, string Who, string Details, string? Reason);
/// <summary>Адрес: населённый пункт, улица и дом из справочников, квартира руками.</summary>
/// <summary>Регистрация: адрес из справочников, контакты, паспорт (только физлицо), КПП (только юрлицо), услуги. AddResident: записать человека в проживающие.</summary>
public record RegisterRequest(string Kind, string Name, string? Place, string? Street, string? House, string? Flat, string? Phone, string? Inn,
    string? Kpp = null, string? Email = null, DateOnly? BirthDate = null, PassportData? Passport = null, bool? AddResident = null, RegistrationServices? Services = null);
public record ChangeAddressRequest(string? Place, string? Street, string? House, string? Flat);
public record AddServiceRequest(string ServiceName);
public record LedgerRowDto(long Id, string When, string Kind, string KindName, string? Period, string Service, long DebtDelta, long CreditDelta, long BalanceAfter, string Who, string? Reference);
public record ChargeEntryDto(string Period, string Service, decimal Volume, string Scheme, decimal? Price, long Amount);

public static class AbonentEndpoints
{
    public static AbonentDto ToDto(this Abonent abonent, IReadOnlyCollection<TariffPrice> prices, string period) => new(
        abonent.Id, abonent.Kind, abonent.Name, abonent.ShortName, abonent.Place, abonent.Street, abonent.Flat, abonent.Account, abonent.Phone, abonent.Inn, abonent.Residents, abonent.Area, abonent.Credit,
        abonent.Services.OrderBy(line => line.Position).Select(line => new ServiceLineDto(
            line.ServiceId, line.Name, line.State, line.TariffName, line.TariffId is null ? null : TariffHistory.PriceOn(prices, line.TariffId, period)?.Price, line.ChargeStatus, line.Debt,
            line.MeterNumber is null ? null : new MeterDto(line.MeterNumber, line.MeterLastReading ?? 0, (line.TariffId is null ? 0 : TariffHistory.PriceOn(prices, line.TariffId, period)?.Price ?? 0) * 100m, line.MeterVerifiedUntil ?? "", line.MeterUnit), line.HouseMeterId, line.TariffId)).ToList(),
        abonent.StreetName, abonent.House, Addresses.HouseKey(abonent.Place, abonent.Street));

    public static PaymentDto ToDto(this Payment payment) => new(payment.Id, payment.AbonentId, payment.AtUtc.ToLocalTime().ToString("dd.MM.yyyy"), payment.Source, payment.Cashier, payment.Amount, payment.Status);

    public static JournalDto ToDto(this JournalRecord record) => new(record.Id.ToString(), record.AbonentId, record.AtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), record.Action, record.Who, record.Details, record.Reason);

    public static void MapAbonents(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/abonents").RequireAuthorization();

        group.MapGet("/", async (EmirDb db) =>
        {
            var list = await db.Abonents.Include(abonent => abonent.Services).OrderBy(abonent => abonent.Account).ToListAsync();
            var prices = await db.TariffPrices.ToListAsync();
            var period = await Periods.WorkPeriod(db);
            return list.Select(abonent => abonent.ToDto(prices, period));
        });

        group.MapGet("/{id}", async (string id, EmirDb db) => (await db.GetAbonent(id)).ToDto(await db.TariffPrices.ToListAsync(), await Periods.WorkPeriod(db)));

        group.MapGet("/{id}/payments", async (string id, EmirDb db) =>
        {
            var payments = await db.Payments.Where(payment => payment.AbonentId == id).OrderByDescending(payment => payment.AtUtc).ToListAsync();
            return payments.Select(payment => payment.ToDto());
        });

        group.MapGet("/{id}/journal", async (string id, EmirDb db) =>
        {
            var records = await db.Journal.Where(record => record.AbonentId == id).OrderByDescending(record => record.AtUtc).ToListAsync();
            return records.Select(record => record.ToDto());
        });

        group.MapPost("/", async (RegisterRequest request, EmirDb db, ClaimsPrincipal user, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider protection) =>
        {
            if (request.Kind is not ("person" or "org")) throw new ApiError(422, "Неизвестный тип абонента");
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name)) throw new ApiError(422, "Заполните имя и адрес");
            if (name.Length is < 2 or > 200) throw new ApiError(422, "Имя должно быть от 2 до 200 символов");
            if (!name.Any(char.IsLetter)) throw new ApiError(422, "В имени должны быть буквы");
            // Город, улица и дом только из справочников: так адреса одного дома всегда записаны одинаково
            var address = await Addresses.Validate(db, request.Place, request.Street, request.House, request.Flat);
            var person = request.Kind == "person";
            var phone = Registration.Phone(request.Phone);
            var email = Registration.Email(request.Email);
            // Дата рождения и паспорт — у человека, КПП — у организации
            var birthDate = person ? Registration.BirthDate(request.BirthDate) : null;
            var passport = person ? Registration.Passport(request.Passport) : null;
            var kpp = person ? null : Registration.Kpp(request.Kpp);
            var addResident = person && (request.AddResident ?? true);

            var maxAccount = (await db.Abonents.Select(abonent => abonent.Account).ToListAsync()).Select(long.Parse).DefaultIfEmpty(40030000).Max();
            var id = Guid.NewGuid().ToString("N");
            var (services, serviceNotes) = Registration.Services(id, request.Services);
            var abonent = new Abonent
            {
                Id = id, Kind = request.Kind, Name = name, ShortName = name, Account = (maxAccount + 1).ToString(),
                Place = address.Place, Street = address.StreetLine, StreetName = address.Street, House = address.House, Flat = address.Flat,
                Phone = phone, Inn = string.IsNullOrWhiteSpace(request.Inn) ? null : request.Inn.Trim(),
                Kpp = kpp, Email = email, BirthDate = birthDate, PassportProtected = passport is null ? null : Registration.Protect(protection, passport),
                Residents = addResident ? 1 : 0,
                Services = services,
            };
            db.Abonents.Add(abonent);
            db.AbonentFacts.Add(new AbonentFact { AbonentId = id, FromPeriod = await Periods.WorkPeriod(db), Area = abonent.Area, Residents = abonent.Residents, Who = user.Who(), AtUtc = DateTime.UtcNow, Reason = "Регистрация абонента" });
            foreach (var line in services)
                db.ServiceTerms.Add(new ServiceTerm { AbonentId = id, ServiceId = line.ServiceId, FromPeriod = await Periods.WorkPeriod(db), State = TariffHistory.Working, Who = user.Who(), AtUtc = DateTime.UtcNow, Reason = "Регистрация абонента" });
            if (addResident) db.Residents.Add(new Resident { AbonentId = id, Name = name, FromDate = DateOnly.FromDateTime(DateTime.Today) });
            // Паспорт в журнал не пишем: только отметку, что он заполнен
            var notes = new List<string> { name };
            notes.AddRange(serviceNotes);
            if (services.Count == 0) notes.Add("без услуг");
            if (addResident) notes.Add("записан в проживающие");
            if (passport is not null) notes.Add("паспорт заполнен");
            db.AddJournal(id, "Регистрация абонента", user.Who(), string.Join("; ", notes));
            await db.SaveChangesAsync();
            return Results.Created($"/api/abonents/{id}", abonent.ToDto(await db.TariffPrices.ToListAsync(), await Periods.WorkPeriod(db)));
        }).Roles(Roles.Service, Roles.Admin);

        group.MapPost("/{id}/recharge", async (string id, EmirDb db, ClaimsPrincipal user) =>
        {
            await db.GetAbonent(id);
            await using var transaction = await db.BeginMoneyTransaction();
            await db.BeginOperation("charge", user.Who(), "Пересчёт абонента");
            var result = await ChargingService.Run(db, id);
            db.AddJournal(id, "Начисление абоненту", user.Who(), $"Начислено {EndpointHelpers.Money(result.Total)} ₽ за {await Periods.WorkPeriod(db)}");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return result;
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapGet("/{id}/charges", async (string id, EmirDb db) =>
        {
            var abonent = await db.GetAbonent(id);
            var entries = await db.ChargeEntries.Where(entry => entry.AbonentId == id).OrderByDescending(entry => entry.Period).ToListAsync();
            return entries.Select(entry => new ChargeEntryDto(entry.Period, abonent.Services.FirstOrDefault(line => line.ServiceId == entry.ServiceId)?.Name ?? entry.ServiceId, entry.Volume, entry.Scheme, entry.Price, entry.Amount));
        });

        group.MapGet("/{id}/ledger", async (string id, EmirDb db) =>
        {
            var abonent = await db.GetAbonent(id);
            var entries = await db.LedgerEntries.Where(entry => entry.AbonentId == id).OrderBy(entry => entry.Id).ToListAsync();
            long balance = 0;
            var rows = new List<LedgerRowDto>(entries.Count);
            foreach (var entry in entries)
            {
                balance += entry.CreditDelta - entry.DebtDelta;
                rows.Add(new LedgerRowDto(entry.Id, entry.AtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), entry.Kind, LedgerKinds.Name(entry.Kind), entry.Period,
                    entry.ServiceId is null ? "Переплата" : abonent.Services.FirstOrDefault(line => line.ServiceId == entry.ServiceId)?.Name ?? entry.ServiceId, entry.DebtDelta, entry.CreditDelta, balance, entry.Who, entry.Reference));
            }
            rows.Reverse();
            return rows;
        });

        group.MapPost("/{id}/address", async (string id, ChangeAddressRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var address = await Addresses.Validate(db, request.Place, request.Street, request.House, request.Flat);
            var abonent = await db.GetAbonent(id);
            static string Full(string place, string street, string? flat) => string.Join(", ", new[] { place, street + (flat is null ? "" : $"-{flat}") }.Where(part => !string.IsNullOrWhiteSpace(part)));
            var old = Full(abonent.Place, abonent.Street, abonent.Flat);
            var updated = Full(address.Place, address.StreetLine, address.Flat);
            if (old == updated && abonent.StreetName == address.Street && abonent.House == address.House) throw new ApiError(422, "Новый адрес совпадает с нынешним");
            abonent.Place = address.Place;
            abonent.Street = address.StreetLine;
            abonent.StreetName = address.Street;
            abonent.House = address.House;
            abonent.Flat = address.Flat;
            db.AddJournal(id, "Смена адреса", user.Who(), $"{old}, теперь {updated}");
            await db.SaveChangesAsync();
            return abonent.ToDto(await db.TariffPrices.ToListAsync(), await Periods.WorkPeriod(db));
        }).Roles(Roles.Cashier, Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapPost("/{id}/services", async (string id, AddServiceRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var name = request.ServiceName?.Trim();
            if (string.IsNullOrEmpty(name)) throw new ApiError(422, "Укажите услугу");
            var abonent = await db.GetAbonent(id);
            if (abonent.Services.Any(line => line.Name == name)) throw new ApiError(409, "Такая услуга уже подключена");
            var serviceId = $"s{Guid.NewGuid():N}"[..12];
            db.ServiceLines.Add(new ServiceLine { AbonentId = id, ServiceId = serviceId, Position = abonent.Services.Count, Name = name, TariffName = "Не выбран", ChargeStatus = "Ждёт начисления" });
            db.ServiceTerms.Add(new ServiceTerm { AbonentId = id, ServiceId = serviceId, FromPeriod = await Periods.WorkPeriod(db), State = TariffHistory.Working, Who = user.Who(), AtUtc = DateTime.UtcNow, Reason = "Услуга добавлена" });
            db.AddJournal(id, "Добавлена услуга", user.Who(), name);
            await db.SaveChangesAsync();
            return abonent.ToDto(await db.TariffPrices.ToListAsync(), await Periods.WorkPeriod(db));
        }).Roles(Roles.Service, Roles.Admin);
    }
}
