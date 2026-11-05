using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record DirectoryTableDto(List<string> Columns, List<List<string>> Rows, bool Sensitive = false, bool Placeholder = false);
public record AdminSectionDto(string Id, string Group, string Title, string Description, List<string> Columns, List<List<string>> Rows, int? Badge = null);
public record TodayTaskDto(string Title, string Details, string Value, bool Warn, string To);
public record TodayActionDto(string Label, string To);
public record TodayMessageDto(string Title, string Details);
public record TodayDto(List<TodayTaskDto> Tasks, List<TodayActionDto> Actions, List<TodayMessageDto> Messages);

public static class ReferenceEndpoints
{
    public static void MapReference(this RouteGroupBuilder api)
    {
        api.MapGet("/directories/groups", () => Catalog.Directories).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        api.MapGet("/directories/table", async (string name, EmirDb db) =>
        {
            var abonents = await db.Abonents.Include(abonent => abonent.Services).OrderBy(abonent => abonent.Account).ToListAsync();
            static string Address(Abonent abonent) => abonent.Street + (abonent.Flat is null ? "" : $"-{abonent.Flat}");
            switch (name)
            {
                case "Люди":
                    return new DirectoryTableDto(["ЛС", "ФИО", "Телефон", "Адрес"], abonents.Where(abonent => abonent.Kind == "person").Select(abonent => new List<string> { abonent.Account, abonent.Name, abonent.Phone ?? "нет", Address(abonent) }).ToList(), Sensitive: true);
                case "Организации":
                    return new DirectoryTableDto(["ЛС", "Название", "ИНН", "Адрес"], abonents.Where(abonent => abonent.Kind == "org").Select(abonent => new List<string> { abonent.Account, abonent.Name, abonent.Inn ?? "—", Address(abonent) }).ToList());
                case "Лицевые счета":
                    return new DirectoryTableDto(["ЛС", "Абонент", "Адрес", "Баланс, ₽"], abonents.Select(abonent => new List<string> { abonent.Account, abonent.ShortName, Address(abonent), EndpointHelpers.Money(abonent.Balance) }).ToList());
                case "Услуги":
                    return new DirectoryTableDto(["Код", "Услуга", "Тариф"], abonents.SelectMany(abonent => abonent.Services).GroupBy(line => line.Name).Select(service => new List<string> { service.First().ServiceId, service.Key, service.First().TariffName }).ToList());
                case "Квартирные счётчики":
                    return new DirectoryTableDto(["Номер", "Адрес", "Услуга", "Показание"], abonents.SelectMany(abonent => abonent.Services.Where(line => line.MeterNumber is not null).Select(line => new List<string> { line.MeterNumber!, Address(abonent), line.Name, (line.MeterLastReading ?? 0).ToString("0.####") })).ToList());
                default:
                    return new DirectoryTableDto(["ID", "Название", "Описание"], [], Placeholder: true);
            }
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Каждый просмотр паспорта попадает в журнал, даже если данных нет
        api.MapPost("/directories/people/{account}/passport", async (string account, EmirDb db, ClaimsPrincipal user) =>
        {
            var abonent = await db.Abonents.FirstOrDefaultAsync(item => item.Account == account) ?? throw new ApiError(404, "Человек не найден");
            db.AddJournal(abonent.Id, "Просмотр паспорта", user.Who(), $"ЛС {account}");
            await db.SaveChangesAsync();
            return new { value = "Паспортные данные не заполнены" };
        }).Roles(Roles.Admin);

        api.MapGet("/admin/sections", async (EmirDb db) =>
        {
            var users = await db.Users.OrderBy(user => user.Login).ToListAsync();
            var journal = await db.Journal.OrderByDescending(record => record.AtUtc).Take(200).ToListAsync();
            return new[]
            {
                new AdminSectionDto("roles", "Доступы", "Роли и доступы", "Что видит и может каждая роль.", ["Раздел", "Абонентский отдел", "Кассир", "Бухгалтер", "Администратор"], Catalog.RoleMatrix.Select(row => row.ToList()).ToList()),
                new AdminSectionDto("users", "Доступы", "Пользователи", "Сотрудники и их роли.", ["Сотрудник", "Логин", "Роль"], users.Select(user => new List<string> { user.DisplayName, user.Login, RoleName(user.Role) }).ToList()),
                new AdminSectionDto("audit", "Контроль", "Журнал действий", "Кто что менял: операции с деньгами и просмотр паспортов.", ["Когда", "Кто", "Действие", "Что изменилось", "Причина"], journal.Select(record => new List<string> { record.AtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), record.Who, record.Action, record.Details, record.Reason ?? "—" }).ToList()),
            };
        }).Roles(Roles.Admin);

        api.MapGet("/today", async (EmirDb db, ClaimsPrincipal user) =>
        {
            var abonents = await db.Abonents.Include(abonent => abonent.Services).ToListAsync();
            var open = await Periods.WorkPeriod(db);
            var saved = await db.PeriodReadings.CountAsync(reading => reading.Period == open);
            var meters = abonents.Sum(abonent => abonent.Services.Count(line => line.MeterNumber is not null));
            var noPhone = abonents.Count(abonent => abonent.Phone is null);
            var debtors = abonents.Count(abonent => abonent.Balance < 0);
            var unresolved = await db.ExchangeLines.CountAsync(line => line.State != "ok");
            var step = await db.PeriodSteps.OrderBy(item => item.Position).FirstOrDefaultAsync(item => item.State == "current");
            var today = DateTime.Today.ToUniversalTime();
            var shiftCount = await db.CashOperations.CountAsync(operation => operation.AtUtc >= today);
            var messages = (await db.Messages.ToListAsync()).Select(message => new TodayMessageDto(message.Title, message.Details)).ToList();

            var noReadings = new TodayTaskDto("Счётчики без показаний", "За открытый месяц", (meters - saved).ToString(), meters > saved, "/readings");
            var batchLines = new TodayTaskDto("Строки загрузок без решения", "Обмен данными", unresolved.ToString(), unresolved > 0, "/exchange");
            var monthStep = new TodayTaskDto("Расчётный месяц", step is null ? "Все шаги выполнены" : "Есть незавершённый шаг", step is null ? "готово" : "открыть", false, "/period");

            if (user.IsInRole(Roles.Cashier))
                return new TodayDto([new("Операций за смену", "Сегодня", shiftCount.ToString(), false, "/cash"), noReadings], [new("Принять оплату", "/cash"), new("Внести показания", "/readings"), new("Итоги смены", "/reports?report=Реестр по кассиру"), new("Должники", "/reports?report=Должники")], messages);
            if (user.IsInRole(Roles.Service))
                return new TodayDto([noReadings, new("Абоненты без телефона", "Заполнить при обращении", noPhone.ToString(), false, "/abonents"), new("Абоненты с долгом", "Всего", debtors.ToString(), debtors > 0, "/reports?report=Должники")], [new("Новый абонент", "/abonents/new"), new("Найти абонента", "/abonents"), new("Внести показания", "/readings"), new("Отчёт по должникам", "/reports?report=Должники")], messages);
            if (user.IsInRole(Roles.Accountant))
                return new TodayDto([batchLines, noReadings, monthStep], [new("Продолжить месяц", "/period"), new("Загрузить реестр", "/exchange"), new("Новая корректировка", "/corrections"), new("Отчёт по должникам", "/reports?report=Должники")], messages);
            return new TodayDto([batchLines, noReadings, monthStep], [new("Роли и доступы", "/admin"), new("Журнал действий", "/admin"), new("Справочники", "/directories"), new("Продолжить месяц", "/period")], messages);
        }).RequireAuthorization();
    }

    private static string RoleName(string role) => role switch
    {
        Roles.Cashier => "Кассир",
        Roles.Service => "Абонентский отдел",
        Roles.Accountant => "Бухгалтер",
        _ => "Администратор",
    };
}
