using System.Globalization;
using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record PeriodIssue(string Title, string Details, string Action, string To, bool Warn = false);
public record PeriodStat(string Label, string Value, bool Warn = false);
public record PeriodStepDto(string Id, string Name, string Status, string State, string Description, List<PeriodStat> Stats, List<PeriodIssue> Issues, string Action, string? ConfirmTitle, string? ConfirmText, string? ConfirmAction);
public record RunStepResult(string Message);
public record SettingDto(string Title, string Details);
public record CurrentMonthDto(string Period, string Title, string Status, string? NextTitle, string? ReopenReason);
public record ClosedMonthDto(string Period, string Month, string ClosedAt, string? ClosedBy, int ReclosedCount, string? ReclosedAt, bool Reopened, string? CannotReopen);
public record ReasonRequest(string Reason);
public record RecloseRequest(string Reason, long ExpectedDelta);
public record CancelRequest(string? Kind, string? Place, string? Query, List<string> Services, string Reason, long ExpectedTotal, int ExpectedLines);

public static class PeriodEndpoints
{
    private record StepInfo(string Name, string Description, string Action);

    private static readonly Dictionary<string, StepInfo> Steps = new()
    {
        ["readings"] = new("Показания", "Всё, что влияет на расчёт по счётчикам: ручной ввод и файлы.", "Перейти к начислениям"),
        ["charges"] = new("Начисления", "Расчёт по всем активным услугам месяца по схемам тарифов.", "Запустить начисление"),
        ["checks"] = new("Проверка", "Сверка результата перед закрытием.", "Отметить проверки"),
        ["penalty"] = new("Пени", "Начисление пени на просроченные долги по настройкам пени.", "Начислить пени"),
        ["close"] = new("Закрытие", "Закрытие фиксирует обороты месяца для финансовой отчётности.", "Закрыть месяц"),
        ["docs"] = new("Документы", "Акты, счета и счета-фактуры по закрытому месяцу.", "Сформировать документы"),
    };

    public static void MapPeriod(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/period").RequireAuthorization();

        group.MapGet("/steps", async (EmirDb db) =>
        {
            var abonents = await db.Abonents.Include(abonent => abonent.Services).ToListAsync();
            var states = await db.PeriodSteps.OrderBy(step => step.Position).ToListAsync();
            var open = await Periods.WorkPeriod(db);
            var label = Periods.Month(open);
            var saved = await db.PeriodReadings.CountAsync(reading => reading.Period == open);
            var meters = abonents.Sum(abonent => abonent.Services.Count(line => line.MeterNumber is not null));
            var missing = meters - saved;
            var serviceCount = abonents.Sum(abonent => abonent.Services.Count(line => line.ServiceId != "penalty"));
            var debtors = abonents.Count(abonent => abonent.Balance < 0);
            var documents = await db.BillingDocuments.CountAsync(document => document.Period == open && document.Status == "active");
            var doneBeforeClose = states.Count(step => step.Position < 4 && step.State == "done");
            var charged = await db.ChargeEntries.Where(entry => entry.Period == open && entry.ServiceId != "penalty").ToListAsync();
            var penaltyTotal = (await db.ChargeEntries.Where(entry => entry.Period == open && entry.ServiceId == "penalty").ToListAsync()).Sum(entry => entry.Amount);

            return states.Select(step =>
            {
                var info = Steps[step.Id];
                List<PeriodStat> stats;
                List<PeriodIssue> issues;
                string? confirmTitle = null, confirmText = null, confirmAction = null;
                switch (step.Id)
                {
                    case "readings":
                        stats = [new("Счётчиков", meters.ToString()), new("С показаниями", saved.ToString()), new("Без показаний", missing.ToString(), missing > 0)];
                        issues = [new($"Без показаний: {missing}", "По таким счётчикам начисления не будет.", "Внести", "/readings", missing > 0), new("Файл показаний", "Загрузка с проверкой каждой строки.", "Загрузить", "/readings/import")];
                        break;
                    case "charges":
                        stats = [new("Абонентов", abonents.Count.ToString()), new("Услуг в расчёте", serviceCount.ToString()), new("Без показаний", missing.ToString(), missing > 0), new("Начислено, ₽", EndpointHelpers.Money(charged.Sum(entry => entry.Amount)))];
                        issues = [new($"Счётчиков без показаний: {missing}", "По ним начисления не будет, в отчёте они попадут в проблемы.", "Внести", "/readings", missing > 0), new("Начислить одному абоненту", "Пересчёт заменит начисления абонента за месяц.", "Найти", "/abonents")];
                        confirmTitle = $"Начислить {abonents.Count} абонентам?";
                        confirmText = "Шаг запускается один раз, затем пересчитать можно только отдельного абонента. Посмотрите пробный расчёт перед запуском.";
                        confirmAction = "Да, начислить";
                        break;
                    case "checks":
                        stats = [new("Абонентов с долгом", debtors.ToString()), new("Строк начислений", charged.Count.ToString())];
                        issues = [new("Сверки месяца", "Начисления, показания, тарифы и дубли. Откройте перед закрытием.", "Открыть", "/checks", true), new("Отчёт по должникам", "Проверьте суммы перед закрытием.", "Открыть", "/reports?report=Должники")];
                        break;
                    case "penalty":
                        stats = [new("Абонентов с долгом", debtors.ToString()), new("Начислено, ₽", EndpointHelpers.Money(penaltyTotal))];
                        issues = [new("Настройки пени", "Срок оплаты и ступени смотрите в параметрах.", "Месяц", "/period")];
                        confirmTitle = $"Начислить пени за {label}?";
                        confirmText = "Шаг запускается один раз. Проверьте суммы по должникам перед запуском.";
                        confirmAction = "Да, начислить пени";
                        break;
                    case "close":
                        stats = [new("Шагов пройдено", $"{doneBeforeClose} из 4", doneBeforeClose < 4)];
                        issues = doneBeforeClose < 4 ? [new("Сначала завершите предыдущие шаги", "Сервер не даст закрыть месяц раньше.", "К началу", "/period", true)] : [];
                        confirmTitle = $"Закрыть месяц: {Periods.Label(open)}?";
                        confirmText = "После закрытия начисления и показания за месяц меняются только через перезакрытие.";
                        confirmAction = "Да, закрыть месяц";
                        break;
                    default:
                        stats = [new("Документов", documents.ToString())];
                        issues = [new("Документы месяца", "Квитанции физлицам, счета, акты и счета-фактуры юрлицам.", "Открыть", "/documents"), new("Реквизиты и нумерация", "Проверьте перед первым выпуском.", "Открыть", "/documents")];
                        break;
                }
                return new PeriodStepDto(step.Id, info.Name, step.Status, step.State, info.Description, stats, issues, info.Action, confirmTitle, confirmText, confirmAction);
            }).ToList();
        });

        group.MapGet("/charges/preview", async (EmirDb db) => await ChargingService.Preview(db)).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapGet("/readiness", async (EmirDb db) => await ChargingService.Readiness(db)).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapPost("/steps/{id}/run", async (string id, EmirDb db, ClaimsPrincipal user) =>
        {
            if (!Steps.ContainsKey(id)) throw new ApiError(404, "Шаг не найден");
            if (id != "charges" && !user.IsInRole(Roles.Accountant) && !user.IsInRole(Roles.Admin)) throw new ApiError(403, "Этот шаг доступен только бухгалтеру");

            await using var transaction = await db.BeginMoneyTransaction();
            await Periods.EnsureNotReopened(db);
            var month = await Periods.Latest(db);
            var states = await db.PeriodSteps.OrderBy(step => step.Position).ToListAsync();
            var step = states.First(item => item.Id == id);
            if (step.State == "done") throw new ApiError(409, "Шаг уже выполнен");
            if (states.Any(item => item.Position < step.Position && item.State != "done")) throw new ApiError(409, "Сначала завершите предыдущие шаги");

            var message = "Шаг выполнен.";
            if (id == "charges")
            {
                await db.BeginOperation("charge", user.Who(), "Начисление месяца");
                var result = await ChargingService.Run(db);
                message = $"Начислено {EndpointHelpers.Money(result.Total)} ₽, абонентов: {result.Abonents}. Проблем: {result.Issues.Count}.";
                db.Messages.Add(new Message { Title = $"Отчёт о начислениях за {Periods.Month(month.Period)}", Details = result.Issues.Count == 0 ? message : $"{message} {string.Join("; ", result.Issues.Take(5).Select(issue => $"{issue.Abonent}: {issue.Problem}"))}" });
            }
            else if (id == "penalty")
            {
                await db.BeginOperation("penalty", user.Who(), "Пени месяца");
                var result = await ChargingService.RunPenalty(db);
                message = $"Пени начислены: {EndpointHelpers.Money(result.Total)} ₽, абонентов: {result.Abonents}.";
            }
            else if (id == "checks")
            {
                // Решение за бухгалтером: сверки не запрещают идти дальше, но результат остаётся в журнале
                var checks = await ChecksService.Run(db, month.Period);
                message = $"Проверки отмечены. Ошибок: {checks.Errors}, предупреждений: {checks.Warnings}.";
            }
            else if (id == "close")
            {
                month.ClosedAtUtc = DateTime.UtcNow;
                month.ClosedBy = user.Who();
                message = $"Месяц закрыт: {Periods.Label(month.Period)}.";
            }
            else if (id == "docs")
            {
                var result = await DocumentService.Generate(db, month.Period, user.Who());
                message = $"Создано документов: {result.Created}, уже было: {result.Existing}.";
            }

            step.State = "done";
            step.Status = "Выполнено";
            var next = states.FirstOrDefault(item => item.Position == step.Position + 1);
            if (next is not null)
            {
                next.State = "current";
                next.Status = "Можно запускать";
            }
            db.AddJournal(null, "Расчётный месяц", user.Who(), $"{Steps[id].Name}: {message}");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new RunStepResult(message);
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapGet("/settings", () =>
        {
            var settings = PenaltyEngine.Current;
            var tiers = string.Join(", ", settings.Tiers.Where(tier => tier.Fraction > 0).Select(tier => $"с {tier.FromDay}-го дня 1/{Math.Round(1 / tier.Fraction).ToString(CultureInfo.InvariantCulture)}"));
            var rate = PenaltyEngine.RateOn(DateOnly.FromDateTime(DateTime.Today), settings);
            return new SettingDto[]
            {
                new("Настройки пени", $"Оплата до {settings.PayDayNextMonth} числа следующего месяца, {tiers} ставки, ставка {rate * 100:0.##} %"),
                new("Настройка НДС", "5 % с января 2025 года, в начислениях пока не применяется"),
                new("Отопительный сезон", "Для сезонных схем, нужно уточнить месяцы"),
                new("Годовое распределение тепла", "В старой системе итог 106 %, нужно проверить"),
            };
        });

        group.MapGet("/current", async (EmirDb db) =>
        {
            var work = await Periods.Work(db);
            var status = work.ReopenedAtUtc is not null ? "reopened" : work.ClosedAtUtc is not null ? "closed" : "open";
            return new CurrentMonthDto(work.Period, Periods.Title(work.Period), status, status == "closed" ? Periods.Title(Periods.Next(work.Period)) : null, work.ReopenReason);
        });

        group.MapGet("/closed", async (EmirDb db) =>
        {
            var months = await db.AccountingMonths.Where(month => month.ClosedAtUtc != null).OrderByDescending(month => month.Period).ToListAsync();
            var result = new List<ClosedMonthDto>();
            foreach (var month in months)
                result.Add(new ClosedMonthDto(month.Period, Periods.Title(month.Period), month.ClosedAtUtc!.Value.ToLocalTime().ToString("dd.MM.yyyy"), month.ClosedBy, month.ReclosedCount,
                    month.ReclosedAtUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), month.ReopenedAtUtc is not null, await Periods.WhyCannotReopen(db, month)));
            return result;
        });

        group.MapPost("/open-next", async (EmirDb db, ClaimsPrincipal user) =>
        {
            await using var transaction = await db.BeginMoneyTransaction();
            var month = await Periods.OpenNext(db);
            db.AddJournal(null, "Расчётный месяц", user.Who(), $"Открыт месяц: {Periods.Label(month.Period)}");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new RunStepResult($"Открыт месяц: {Periods.Label(month.Period)}.");
        }).Roles(Roles.Accountant, Roles.Admin);

        group.MapPost("/months/{period}/reopen", async (string period, ReasonRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите причину");
            await using var transaction = await db.BeginMoneyTransaction();
            await Periods.EnsureNotReopened(db);
            var month = await db.AccountingMonths.FirstOrDefaultAsync(item => item.Period == period) ?? throw new ApiError(404, "Такого месяца нет");
            if (await Periods.WhyCannotReopen(db, month) is { } why) throw new ApiError(409, why);
            month.ReopenedAtUtc = DateTime.UtcNow;
            month.ReopenedBy = user.Who();
            month.ReopenReason = request.Reason.Trim();
            db.AddJournal(null, "Перезакрытие месяца", user.Who(), $"Открыт на исправление: {Periods.Label(period)}", request.Reason.Trim());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new RunStepResult($"Месяц открыт на исправление: {Periods.Label(period)}.");
        }).Roles(Roles.Accountant, Roles.Admin);

        group.MapGet("/cancel/preview", async (string? kind, string? place, string? query, string? services, EmirDb db) =>
            await CancellationService.Preview(db, new CancelFilter(kind, place, query, (services ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)))).Roles(Roles.Accountant, Roles.Admin);

        group.MapPost("/cancel", async (CancelRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите причину");
            await using var transaction = await db.BeginMoneyTransaction();
            await db.BeginOperation("cancel", user.Who(), request.Reason.Trim());
            var result = await CancellationService.Apply(db, new CancelFilter(request.Kind, request.Place, request.Query, request.Services ?? []), request.ExpectedTotal, request.ExpectedLines, user.Who(), request.Reason.Trim());
            var text = $"Отменено начислений: {result.Lines} на {EndpointHelpers.Money(result.Total)} ₽ за {Periods.Label(result.Period)}. Шаги месяца начаты заново.";
            db.AddJournal(null, "Отмена начислений", user.Who(), text, request.Reason.Trim());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new RunStepResult(text);
        }).Roles(Roles.Accountant, Roles.Admin);

        group.MapGet("/reclose/preview", async (EmirDb db) => await ReclosingService.Preview(db)).Roles(Roles.Accountant, Roles.Admin);

        group.MapPost("/reclose", async (RecloseRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите причину");
            await using var transaction = await db.BeginMoneyTransaction();
            await db.BeginOperation("reclose", user.Who(), request.Reason.Trim());
            var result = await ReclosingService.Apply(db, request.ExpectedDelta, user.Who(), request.Reason.Trim());
            var text = $"Месяц перезакрыт: {Periods.Label(result.Period)}. Строк изменено: {result.Changes.Count}, разница {EndpointHelpers.Money(result.Delta)} ₽.";
            db.AddJournal(null, "Перезакрытие месяца", user.Who(), text, request.Reason.Trim());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new RunStepResult(text);
        }).Roles(Roles.Accountant, Roles.Admin);
    }
}
