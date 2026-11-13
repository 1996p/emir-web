using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

/// <summary>OperationDate — дата документа-основания (по умолчанию сегодня). Деньги проводятся в открытом месяце, дата пишется в журнал и основание.</summary>
/// <summary>TargetAbonentId — верный абонент для переноса между адресами. FromServiceId и ToServiceId — услуги для переноса между услугами, «credit» означает переплату.</summary>
public record ApplyCorrectionRequest(string AbonentId, string TypeId, long Amount, string Reason, string? PaymentId, DateOnly? OperationDate = null,
    string? TargetAbonentId = null, string? FromServiceId = null, string? ToServiceId = null);

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
            var today = DateOnly.FromDateTime(DateTime.Today);
            var date = request.OperationDate ?? today;
            if (date > today) throw new ApiError(422, "Дата операции не может быть в будущем");
            if (date < today.AddYears(-5)) throw new ApiError(422, "Дата операции старше пяти лет");

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

            await db.BeginOperation("correction", user.Who(), $"{type.Name} от {date:dd.MM.yyyy}: {request.Reason.Trim()}");
            var details = $"{EndpointHelpers.Money(amount)} ₽, дата операции {date:dd.MM.yyyy}";
            if (type.Id == "move-address")
            {
                var target = await MoveToAddress(db, abonent, payment!, request.TargetAbonentId, amount);
                db.AddJournal(target.Id, type.Name, user.Who(), $"{details}, с ЛС {abonent.Account}", request.Reason.Trim());
                details += $", на ЛС {target.Account}";
            }
            else if (type.Id == "move-service")
            {
                details += ", " + MoveBetweenServices(abonent, request.FromServiceId, request.ToServiceId, amount);
            }
            else
            {
                if (type.BalanceEffect < 0) Billing.TakeFromAbonent(abonent, amount);
                if (type.BalanceEffect > 0) Billing.GiveToAbonent(abonent, amount);
                if (payment is not null)
                {
                    if (amount == payment.Amount) payment.Status = "Отменена";
                    else payment.Amount -= amount;
                }
            }

            db.AddJournal(abonent.Id, type.Name, user.Who(), details, request.Reason.Trim());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return abonent.ToDto(await db.TariffPrices.ToListAsync(), await Periods.WorkPeriod(db));
        }).Roles(Roles.Cashier, Roles.Accountant, Roles.Admin);
    }

    /// <summary>
    /// Платёж попал не тому абоненту: деньги снимаются с него и приходят верному. Сам платёж (касса, дата, офис) переезжает
    /// к верному абоненту целиком или частью, поэтому кассовые отчёты не меняются.
    /// </summary>
    private static async Task<Abonent> MoveToAddress(EmirDb db, Abonent abonent, Payment payment, string? targetId, long amount)
    {
        if (string.IsNullOrWhiteSpace(targetId)) throw new ApiError(422, "Выберите верного абонента");
        if (targetId == abonent.Id) throw new ApiError(422, "Верный абонент совпадает с текущим");
        var target = await db.GetAbonent(targetId);
        Billing.TakeFromAbonent(abonent, amount);
        Billing.GiveToAbonent(target, amount);
        if (amount == payment.Amount)
        {
            payment.AbonentId = target.Id;
        }
        else
        {
            payment.Amount -= amount;
            db.Payments.Add(new Payment
            {
                Id = Guid.NewGuid().ToString("N"), AbonentId = target.Id, AtUtc = payment.AtUtc, Source = payment.Source, Cashier = payment.Cashier,
                Amount = amount, Office = payment.Office, Period = payment.Period,
            });
        }
        return target;
    }

    /// <summary>Перенос денег между услугами одного абонента. С услуги деньги уходят как долг, на услугу гасят долг, излишек — в переплату.</summary>
    private static string MoveBetweenServices(Abonent abonent, string? fromId, string? toId, long amount)
    {
        if (string.IsNullOrWhiteSpace(fromId) || string.IsNullOrWhiteSpace(toId)) throw new ApiError(422, "Выберите, откуда и куда перенести деньги");
        if (fromId == toId) throw new ApiError(422, "Услуги «откуда» и «куда» совпадают");
        ServiceLine? Line(string id) => id == "credit" ? null : abonent.Services.FirstOrDefault(item => item.ServiceId == id) ?? throw new ApiError(422, $"У абонента нет услуги {id}");
        var from = Line(fromId);
        var to = Line(toId);
        if (from is null)
        {
            if (abonent.Credit < amount) throw new ApiError(422, $"Переплата {EndpointHelpers.Money(abonent.Credit)} ₽ меньше суммы переноса");
            abonent.Credit -= amount;
        }
        else
        {
            from.Debt += amount;
        }
        if (to is null)
        {
            abonent.Credit += amount;
        }
        else
        {
            var applied = Math.Min(to.Debt, amount);
            to.Debt -= applied;
            abonent.Credit += amount - applied;
        }
        return $"{from?.Name ?? "переплата"} → {to?.Name ?? "переплата"}";
    }
}
