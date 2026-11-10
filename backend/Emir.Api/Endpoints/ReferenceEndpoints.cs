using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

/// <summary>Source — справочник-источник выпадающего списка, Label — его колонка со значениями.
/// Where — каскад: колонка источника → колонка этой записи, по значению которой фильтруется список (улицы выбранного города).</summary>
public record DirectoryRefDto(string Source, string Label, Dictionary<string, string>? Where = null);
public record DirectoryTableDto(List<string> Columns, List<List<string>> Rows, bool Sensitive = false, bool Placeholder = false, bool Editable = false, Dictionary<string, DirectoryRefDto>? References = null);
/// <summary>Expected: строка, какой её видел пользователь. Если её уже изменили, правка отклоняется.</summary>
public record DirectoryRowRequest(string Name, List<string>? Row, List<string>? Expected, string? Reason);
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
                    var stored = await db.ReferenceTables.FirstOrDefaultAsync(item => item.Name == name);
                    if (stored is null) return new DirectoryTableDto(["ID", "Название", "Описание"], [], Placeholder: true);
                    var references = Catalog.References.Where(item => item.Table == name).ToDictionary(item => item.Column, item => new DirectoryRefDto(item.Source, item.Label, item.Where?.ToDictionary(pair => pair.From, pair => pair.To)));
                    return new DirectoryTableDto(
                        System.Text.Json.JsonSerializer.Deserialize<List<string>>(stored.ColumnsJson) ?? [],
                        System.Text.Json.JsonSerializer.Deserialize<List<List<string>>>(stored.RowsJson) ?? [],
                        stored.Sensitive, Editable: true, References: references.Count > 0 ? references : null);
            }
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Значения для выпадающего списка: уникальные непустые значения колонки справочника-источника
        // where — JSON {"колонка источника": "значение"}: для каскада Город → Улица → Дом
        api.MapGet("/directories/options", async (string name, string column, string? where, EmirDb db) =>
        {
            Dictionary<string, string>? filter = null;
            if (!string.IsNullOrWhiteSpace(where))
            {
                try { filter = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(where); }
                catch (System.Text.Json.JsonException) { throw new ApiError(422, "Неверный фильтр списка"); }
            }
            return await DirectoryOptions(db, name, column, filter);
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Справочники старой системы хранятся таблицей целиком. Строку правят по номеру, сверяя с тем, что видел пользователь
        api.MapPost("/directories/rows", (DirectoryRowRequest request, EmirDb db, ClaimsPrincipal user) =>
            EditRow(db, user, request, null, "add")).Roles(Roles.Service, Roles.Accountant, Roles.Admin);
        api.MapPost("/directories/rows/{index:int}", (int index, DirectoryRowRequest request, EmirDb db, ClaimsPrincipal user) =>
            EditRow(db, user, request, index, "edit")).Roles(Roles.Service, Roles.Accountant, Roles.Admin);
        api.MapPost("/directories/rows/{index:int}/delete", (int index, DirectoryRowRequest request, EmirDb db, ClaimsPrincipal user) =>
            EditRow(db, user, request, index, "delete")).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Каждый просмотр паспорта попадает в журнал, даже если данных нет
        api.MapPost("/directories/people/{account}/passport", async (string account, EmirDb db, ClaimsPrincipal user, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider protection) =>
        {
            var abonent = await db.Abonents.FirstOrDefaultAsync(item => item.Account == account) ?? throw new ApiError(404, "Человек не найден");
            db.AddJournal(abonent.Id, "Просмотр паспорта", user.Who(), $"ЛС {account}");
            await db.SaveChangesAsync();
            var passport = Registration.Unprotect(protection, abonent.PassportProtected);
            return new { value = passport is null ? "Паспортные данные не заполнены" : Registration.Describe(passport) };
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

    private const int MaxCell = 1000;

    /// <summary>Уникальные непустые значения колонки справочника-источника, по алфавиту.</summary>
    private static async Task<List<string>> DirectoryOptions(EmirDb db, string name, string column, IReadOnlyDictionary<string, string>? where = null)
    {
        var table = await db.ReferenceTables.AsNoTracking().FirstOrDefaultAsync(item => item.Name == name) ?? throw new ApiError(404, "Справочник-источник не найден");
        var columns = DirectoryData.Columns(table);
        if (!columns.Contains(column)) throw new ApiError(404, $"В справочнике «{name}» нет колонки «{column}»");
        foreach (var key in where?.Keys ?? []) if (!columns.Contains(key)) throw new ApiError(404, $"В справочнике «{name}» нет колонки «{key}»");
        return DirectoryData.Options(columns, DirectoryData.Rows(table), column, where);
    }

    private static async Task<IResult> EditRow(EmirDb db, ClaimsPrincipal user, DirectoryRowRequest request, int? index, string action)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason)) throw new ApiError(422, "Укажите причину");
        // Таблица хранится целиком, поэтому две одновременные правки не должны затереть друг друга
        await using var transaction = await db.Database.BeginTransactionAsync();
        var table = await db.ReferenceTables.FromSqlInterpolated($"SELECT * FROM \"ReferenceTables\" WHERE \"Name\" = {request.Name} FOR UPDATE").FirstOrDefaultAsync()
            ?? throw new ApiError(404, "Справочник не найден или его нельзя менять");
        if (table.Sensitive && !user.IsInRole(Roles.Admin)) throw new ApiError(403, "Справочник с персональными данными меняет только администратор");
        var columns = System.Text.Json.JsonSerializer.Deserialize<List<string>>(table.ColumnsJson) ?? [];
        var rows = System.Text.Json.JsonSerializer.Deserialize<List<List<string>>>(table.RowsJson) ?? [];

        List<string> Clean(List<string>? row)
        {
            var cells = (row ?? []).Select(cell => (cell ?? "").Trim()).ToList();
            if (cells.Count != columns.Count) throw new ApiError(422, $"Нужно {columns.Count} полей, пришло {cells.Count}");
            if (cells.All(cell => cell == "")) throw new ApiError(422, "Заполните хотя бы одно поле");
            if (cells.Any(cell => cell.Length > MaxCell)) throw new ApiError(422, $"Поле длиннее {MaxCell} символов");
            return cells;
        }
        string Text(List<string> row) => string.Join("; ", columns.Zip(row).Where(pair => pair.Second != "").Select(pair => $"{pair.First}: {pair.Second}"));

        // Колонки-ссылки на другие справочники: новое значение должно быть из списка источника (старое «грязное» значение не трогаем).
        // Каскад: улица проверяется по улицам выбранного города, поэтому при смене города улица проверяется заново
        var refs = Catalog.References.Where(item => item.Table == request.Name).ToList();
        var optionsCache = new Dictionary<string, HashSet<string>>();
        async Task ValidateRefs(List<string> row, List<string>? old)
        {
            foreach (var reference in refs)
            {
                var at = columns.IndexOf(reference.Column);
                if (at < 0 || at >= row.Count) continue;
                var value = row[at];
                var where = (reference.Where ?? []).Select(pair => (pair.From, at: columns.IndexOf(pair.To), pair.To)).Where(pair => pair.at >= 0).ToList();
                bool Same(int i) => old is not null && i < old.Count && old[i] == row[i];
                if (value == "" || (Same(at) && where.All(pair => Same(pair.at)))) continue;
                var parentEmpty = where.FirstOrDefault(pair => row[pair.at] == "");
                if (parentEmpty.To is not null) throw new ApiError(422, $"Сначала заполните «{parentEmpty.To}», потом «{reference.Column}»");
                var filter = where.ToDictionary(pair => pair.From, pair => row[pair.at]);
                var key = reference.Source + "|" + reference.Label + "|" + string.Join("|", filter.Select(pair => pair.Key + "=" + pair.Value));
                if (!optionsCache.TryGetValue(key, out var allowed))
                    optionsCache[key] = allowed = (await DirectoryOptions(db, reference.Source, reference.Label, filter)).ToHashSet();
                if (!allowed.Contains(value))
                    throw new ApiError(422, filter.Count == 0
                        ? $"«{value}» нет в справочнике «{reference.Source}». Выберите значение из списка."
                        : $"«{value}» нет в справочнике «{reference.Source}» для {string.Join(", ", where.Select(pair => $"{pair.To} «{row[pair.at]}»"))}. Выберите значение из списка.");
            }
        }

        string details;
        if (index is null)
        {
            var row = Clean(request.Row);
            await ValidateRefs(row, null);
            rows.Add(row);
            details = $"Добавлено: {Text(row)}";
        }
        else
        {
            if (index < 0 || index >= rows.Count) throw new ApiError(404, "Запись не найдена");
            if (request.Expected is null || !rows[index.Value].SequenceEqual(request.Expected)) throw new ApiError(409, "Запись уже изменил кто-то другой. Обновите страницу");
            var old = rows[index.Value];
            if (action == "delete")
            {
                rows.RemoveAt(index.Value);
                details = $"Удалено: {Text(old)}";
            }
            else
            {
                var row = Clean(request.Row);
                await ValidateRefs(row, old);
                rows[index.Value] = row;
                var changes = columns.Select((column, i) => (column, before: i < old.Count ? old[i] : "", after: row[i])).Where(item => item.before != item.after).Select(item => $"{item.column}: «{item.before}» → «{item.after}»").ToList();
                if (changes.Count == 0) return Results.NoContent();
                details = string.Join("; ", changes);
            }
        }

        // Запись — источник для других справочников: переименование расходится по ссылкам, удаление используемого значения запрещено
        if (index is not null)
        {
            var old = request.Expected!;
            var updated = action == "delete" ? null : rows[index.Value];
            var linked = await DirectoryLinks.Propagate(db, user, table.Name, columns, rows, old, updated);
            if (linked != "") details += ". " + linked;
        }

        DirectoryData.SetRows(table, rows);
        db.AddJournal(null, $"Справочник «{table.Name}»", user.Who(), details, reason);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Results.NoContent();
    }
}
