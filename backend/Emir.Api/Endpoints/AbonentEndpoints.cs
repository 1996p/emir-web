using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

/// <summary>Tariff: цена открытого месяца в копейках, может быть дробной.</summary>
public record MeterDto(string Number, decimal LastReading, decimal Tariff, string VerifiedUntil);
public record ServiceLineDto(string Id, string Name, string State, string TariffName, decimal? Price, string ChargeStatus, long Debt, MeterDto? Meter);
public record AbonentDto(string Id, string Kind, string Name, string ShortName, string Place, string Street, string? Flat, string Account, string? Phone, string? Inn, int Residents, decimal Area, long Credit, List<ServiceLineDto> Services);
public record PaymentDto(string Id, string AbonentId, string Date, string Source, string Cashier, long Amount, string Status);
public record JournalDto(string Id, string? AbonentId, string When, string Action, string Who, string Details, string? Reason);
public record RegisterRequest(string Kind, string Name, string Address, string? Phone, string? Inn);
public record ChangeAddressRequest(string Address);
public record AddServiceRequest(string ServiceName);
public record LedgerRowDto(long Id, string When, string Kind, string KindName, string? Period, string Service, long DebtDelta, long CreditDelta, long BalanceAfter, string Who, string? Reference);
public record ChargeEntryDto(string Period, string Service, decimal Volume, string Scheme, decimal? Price, long Amount);

public static class AbonentEndpoints
{
    public static AbonentDto ToDto(this Abonent abonent, IReadOnlyCollection<TariffPrice> prices, string period) => new(
        abonent.Id, abonent.Kind, abonent.Name, abonent.ShortName, abonent.Place, abonent.Street, abonent.Flat, abonent.Account, abonent.Phone, abonent.Inn, abonent.Residents, abonent.Area, abonent.Credit,
        abonent.Services.OrderBy(line => line.Position).Select(line => new ServiceLineDto(
            line.ServiceId, line.Name, line.State, line.TariffName, line.TariffId is null ? null : TariffHistory.PriceOn(prices, line.TariffId, period)?.Price, line.ChargeStatus, line.Debt,
            line.MeterNumber is null ? null : new MeterDto(line.MeterNumber, line.MeterLastReading ?? 0, (line.TariffId is null ? 0 : TariffHistory.PriceOn(prices, line.TariffId, period)?.Price ?? 0) * 100m, line.MeterVerifiedUntil ?? ""))).ToList());

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

        group.MapPost("/", async (RegisterRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (request.Kind is not ("person" or "org")) throw new ApiError(422, "Неизвестный тип абонента");
            var name = request.Name?.Trim();
            var address = request.Address?.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(address)) throw new ApiError(422, "Заполните имя и адрес");

            var maxAccount = (await db.Abonents.Select(abonent => abonent.Account).ToListAsync()).Select(long.Parse).DefaultIfEmpty(40030000).Max();
            var id = Guid.NewGuid().ToString("N");
            var abonent = new Abonent
            {
                Id = id, Kind = request.Kind, Name = name, ShortName = name, Street = address, Account = (maxAccount + 1).ToString(),
                Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(), Inn = string.IsNullOrWhiteSpace(request.Inn) ? null : request.Inn.Trim(),
                Residents = request.Kind == "person" ? 1 : 0,
                Services = [new ServiceLine { AbonentId = id, ServiceId = "heat", Name = "Теплоснабжение", TariffName = "По нормативу", ChargeStatus = "Ждёт начисления" }],
            };
            db.Abonents.Add(abonent);
            db.AbonentFacts.Add(new AbonentFact { AbonentId = id, FromPeriod = await Periods.WorkPeriod(db), Area = abonent.Area, Residents = abonent.Residents, Who = user.Who(), AtUtc = DateTime.UtcNow, Reason = "Регистрация абонента" });
            db.ServiceTerms.Add(new ServiceTerm { AbonentId = id, ServiceId = "heat", FromPeriod = await Periods.WorkPeriod(db), State = TariffHistory.Working, Who = user.Who(), AtUtc = DateTime.UtcNow, Reason = "Регистрация абонента" });
            db.AddJournal(id, "Регистрация абонента", user.Who(), name);
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
            var address = request.Address?.Trim();
            if (string.IsNullOrEmpty(address)) throw new ApiError(422, "Укажите новый адрес");
            var abonent = await db.GetAbonent(id);
            var old = abonent.Street;
            abonent.Street = address;
            abonent.Place = "";
            abonent.Flat = null;
            db.AddJournal(id, "Смена адреса", user.Who(), $"{old}, теперь {address}");
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
