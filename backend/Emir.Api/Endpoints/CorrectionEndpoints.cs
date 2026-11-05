using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record ApplyCorrectionRequest(string AbonentId, string TypeId, long Amount, string Reason, string? PaymentId);

public static class CorrectionEndpoints
{
    public static void MapCorrections(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/corrections");

        group.MapGet("/types", () => Catalog.Corrections).Roles(Roles.Cashier, Roles.Accountant, Roles.Admin);

        group.MapPost("/", async (ApplyCorrectionRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var type = Catalog.Corrections.FirstOrDefault(item => item.Id == request.TypeId) ?? throw new ApiError(422, "Неизвестная операция");
            // По матрице ролей кассиру доступны только операции с платежами
            if (user.IsInRole(Roles.Cashier) && type.Group != "Платежи") throw new ApiError(403, "Эта операция доступна только бухгалтеру");
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите причину");

            await using var transaction = await db.BeginMoneyTransaction();
            var abonent = await db.GetAbonent(request.AbonentId);
            Payment? payment = null;
            if (type.NeedsPayment)
            {
                payment = await db.Payments.FirstOrDefaultAsync(item => item.Id == request.PaymentId && item.AbonentId == abonent.Id)
                    ?? throw new ApiError(422, "Выберите платёж");
                if (payment.Status != "Проведена") throw new ApiError(409, "Этот платёж уже отменён");
            }

            // Сумму отмены определяет сервер, а не клиент
            var amount = type.Id == "cancel-payment" ? payment!.Amount : request.Amount;
            if (amount <= 0) throw new ApiError(422, "Укажите сумму");
            if (payment is not null && amount > payment.Amount) throw new ApiError(422, "Сумма больше платежа");

            await db.BeginOperation("correction", user.Who(), $"{type.Name}: {request.Reason.Trim()}");
            if (type.BalanceEffect < 0) Billing.TakeFromAbonent(abonent, amount);
            if (type.BalanceEffect > 0) Billing.GiveToAbonent(abonent, amount);

            if (payment is not null)
            {
                if (amount == payment.Amount) payment.Status = "Отменена";
                else payment.Amount -= amount;
            }

            db.AddJournal(abonent.Id, type.Name, user.Who(), $"{EndpointHelpers.Money(amount)} ₽", request.Reason.Trim());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return abonent.ToDto(await db.TariffPrices.ToListAsync(), await Periods.WorkPeriod(db));
        }).Roles(Roles.Cashier, Roles.Accountant, Roles.Admin);
    }
}
