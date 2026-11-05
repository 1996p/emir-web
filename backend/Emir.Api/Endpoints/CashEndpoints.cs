using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record PayRequest(string AbonentId, string Source, List<Allocation> Allocations, decimal? Reading);
public record PayResult(string PaymentId, long Total, long NewBalance);
public record RefundRequest(string AbonentId, string PaymentId, string Reason);
public record ShiftEntryDto(string Id, string Time, string Name, long Amount);
public record ShiftDto(List<ShiftEntryDto> Entries, int Count, long Total);

public static class CashEndpoints
{
    private static readonly Dictionary<string, string> Sources = new()
    {
        ["cash-person"] = "Касса ФЛ", ["cash-org"] = "Касса ЮЛ", ["bank"] = "Банковские платежи",
    };

    public static void MapCash(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/cash");

        group.MapPost("/payments", async (PayRequest request, HttpContext context, EmirDb db, ClaimsPrincipal user) =>
        {
            if (!Sources.TryGetValue(request.Source, out var sourceName)) throw new ApiError(422, "Неизвестный источник платежа");
            var key = context.Request.Headers["Idempotency-Key"].FirstOrDefault();

            await using var transaction = await db.BeginMoneyTransaction();
            // Повтор запроса с тем же ключом не создаёт второй платёж
            if (!string.IsNullOrEmpty(key) && await db.Payments.FirstOrDefaultAsync(payment => payment.IdempotencyKey == key) is { } existing)
            {
                var same = await db.GetAbonent(existing.AbonentId);
                return new PayResult(existing.Id, existing.Amount, same.Balance);
            }

            var abonent = await db.GetAbonent(request.AbonentId);
            if (request.Reading is { } reading)
            {
                var meter = abonent.Services.FirstOrDefault(line => line.MeterNumber is not null)
                    ?? throw new ApiError(422, "У абонента нет счётчика");
                if (reading < meter.MeterLastReading) throw new ApiError(422, $"Показание {reading:0.####} меньше предыдущего {meter.MeterLastReading:0.####}");
                await db.BeginOperation("reading", user.Who(), meter.MeterNumber);
                Billing.ApplyReading(db, abonent, meter, reading);
                // Отдельным движением: начисление по показанию и оплата в журнале не должны схлопнуться в разницу
                await db.SaveChangesAsync();
            }

            await db.BeginOperation("payment", user.Who(), sourceName);

            var total = Billing.ApplyAllocations(abonent, request.Allocations ?? []);
            var now = DateTime.UtcNow;
            var id = Guid.NewGuid().ToString("N");
            db.Payments.Add(new Payment { Id = id, AbonentId = abonent.Id, AtUtc = now, Source = sourceName, Cashier = user.Who(), Amount = total, IdempotencyKey = string.IsNullOrEmpty(key) ? null : key });
            db.CashOperations.Add(new CashOperation { AtUtc = now, Name = abonent.ShortName, Amount = total });
            db.AddJournal(abonent.Id, "Платёж", user.Who(), $"Принято {EndpointHelpers.Money(total)} ₽, {sourceName}");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new PayResult(id, total, abonent.Balance);
        }).Roles(Roles.Cashier, Roles.Admin);

        group.MapPost("/refunds", async (RefundRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите причину");
            await using var transaction = await db.BeginMoneyTransaction();
            var abonent = await db.GetAbonent(request.AbonentId);
            var payment = await db.Payments.FirstOrDefaultAsync(item => item.Id == request.PaymentId && item.AbonentId == abonent.Id)
                ?? throw new ApiError(404, "Платёж не найден");
            if (payment.Status != "Проведена") throw new ApiError(409, "Этот платёж уже отменён");

            await db.BeginOperation("refund", user.Who(), request.Reason.Trim());
            Billing.TakeFromAbonent(abonent, payment.Amount);
            payment.Status = "Отменена";
            db.CashOperations.Add(new CashOperation { AtUtc = DateTime.UtcNow, Name = abonent.ShortName, Amount = -payment.Amount });
            db.AddJournal(abonent.Id, "Возврат из кассы", user.Who(), $"Выдано {EndpointHelpers.Money(payment.Amount)} ₽", request.Reason.Trim());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new { amount = payment.Amount, newBalance = abonent.Balance };
        }).Roles(Roles.Cashier, Roles.Admin);

        group.MapGet("/shift", async (EmirDb db) =>
        {
            var start = DateTime.Today.ToUniversalTime();
            var operations = await db.CashOperations.Where(operation => operation.AtUtc >= start).OrderByDescending(operation => operation.Id).ToListAsync();
            var entries = operations.Select(operation => new ShiftEntryDto(operation.Id.ToString(), operation.AtUtc.ToLocalTime().ToString("HH:mm"), operation.Name, operation.Amount)).ToList();
            return new ShiftDto(entries, entries.Count, operations.Sum(operation => operation.Amount));
        }).Roles(Roles.Cashier, Roles.Accountant, Roles.Admin);
    }
}
