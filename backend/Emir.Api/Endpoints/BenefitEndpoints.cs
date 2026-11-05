using System.Globalization;
using System.Security.Claims;
using System.Text;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record BenefitCategoryDto(int Id, string Name, bool IsActive, int Beneficiaries);
public record SaveCategoryRequest(string Name, bool IsActive = true);
public record BeneficiaryDto(int Id, string AbonentId, string Abonent, string Account, string Address, int CategoryId, string Category, string FullName, string? FromDate, string? ToDate, bool Active, long Debt, bool Debtor, string Uuid);
public record AddBeneficiaryRequest(string AbonentId, int CategoryId, string FullName, DateOnly? FromDate);
public record EndBeneficiaryRequest(DateOnly ToDate, string Reason);
public record ImportLineDto(int Line, string Account, string FullName, string Category, string? FromDate, string State);
public record ImportResultDto(bool Applied, int Added, List<ImportLineDto> Lines);

/// <summary>
/// Реестр льготников и обмен с соцзащитой. Льгота не меняет начисление: в старой системе у категории только название,
/// а льготник связан с запросами и ответами соцзащиты и отметкой о долге.
/// Категория льготы может раскрывать состояние здоровья, поэтому кассе реестр недоступен, а в журнале абонента её названия нет.
/// </summary>
public static class BenefitEndpoints
{
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private static string Address(Abonent abonent) => string.Join(", ", new[] { abonent.Place, abonent.Street + (abonent.Flat is null ? "" : $"-{abonent.Flat}") }.Where(item => !string.IsNullOrWhiteSpace(item)));

    private static bool IsActive(Beneficiary item, DateOnly on) => (item.FromDate is null || item.FromDate <= on) && (item.ToDate is null || item.ToDate > on);

    private static BeneficiaryDto ToDto(Beneficiary item, Abonent abonent, string category, DateOnly today)
    {
        var debt = abonent.Services.Sum(line => line.Debt);
        return new BeneficiaryDto(item.Id, abonent.Id, abonent.ShortName, abonent.Account, Address(abonent), item.CategoryId, category, item.FullName, item.FromDate?.ToString("dd.MM.yyyy"), item.ToDate?.ToString("dd.MM.yyyy"), IsActive(item, today), debt, debt > abonent.Credit, item.Uuid);
    }

    private static void ValidateName(string? name, string what)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) throw new ApiError(422, $"Укажите: {what}");
    }

    public static void MapBenefits(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/benefits");

        group.MapGet("/categories", async (EmirDb db) =>
        {
            var categories = await db.BenefitCategories.OrderBy(item => item.Name).ToListAsync();
            var counts = await db.Beneficiaries.GroupBy(item => item.CategoryId).Select(items => new { items.Key, Count = items.Count() }).ToDictionaryAsync(item => item.Key, item => item.Count);
            return categories.Select(item => new BenefitCategoryDto(item.Id, item.Name, item.IsActive, counts.GetValueOrDefault(item.Id)));
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapPost("/categories", async (SaveCategoryRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            ValidateName(request.Name, "название категории");
            var name = request.Name.Trim();
            if (await db.BenefitCategories.AnyAsync(item => item.Name == name)) throw new ApiError(409, "Такая категория уже есть");
            db.BenefitCategories.Add(new BenefitCategory { Name = name });
            db.AddJournal(null, "Категория льгот", user.Who(), $"Добавлена: {name}");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Accountant, Roles.Admin);

        // Категории не удаляются: на них ссылается история льготников
        group.MapPost("/categories/{id:int}", async (int id, SaveCategoryRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            ValidateName(request.Name, "название категории");
            var category = await db.BenefitCategories.FirstOrDefaultAsync(item => item.Id == id) ?? throw new ApiError(404, "Категория не найдена");
            var name = request.Name.Trim();
            if (await db.BenefitCategories.AnyAsync(item => item.Name == name && item.Id != id)) throw new ApiError(409, "Такая категория уже есть");
            db.AddJournal(null, "Категория льгот", user.Who(), $"{category.Name}, теперь {name}{(request.IsActive ? "" : ", не используется")}");
            category.Name = name;
            category.IsActive = request.IsActive;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Accountant, Roles.Admin);

        group.MapGet("/", async (string? abonentId, EmirDb db) =>
        {
            var query = db.Beneficiaries.AsQueryable();
            if (!string.IsNullOrEmpty(abonentId)) query = query.Where(item => item.AbonentId == abonentId);
            var items = await query.OrderBy(item => item.FullName).ToListAsync();
            var abonentIds = items.Select(item => item.AbonentId).Distinct().ToList();
            var abonents = await db.Abonents.Include(item => item.Services).Where(item => abonentIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id);
            var categories = await db.BenefitCategories.ToDictionaryAsync(item => item.Id, item => item.Name);
            var today = DateOnly.FromDateTime(DateTime.Today);
            return items.Select(item => ToDto(item, abonents[item.AbonentId], categories.GetValueOrDefault(item.CategoryId, ""), today));
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapPost("/", async (AddBeneficiaryRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            ValidateName(request.FullName, "ФИО льготника");
            var abonent = await db.GetAbonent(request.AbonentId);
            var category = await db.BenefitCategories.FirstOrDefaultAsync(item => item.Id == request.CategoryId && item.IsActive) ?? throw new ApiError(422, "Выберите категорию");
            var name = request.FullName.Trim();
            var today = DateOnly.FromDateTime(DateTime.Today);
            var existing = await db.Beneficiaries.Where(item => item.AbonentId == abonent.Id && item.CategoryId == category.Id).ToListAsync();
            if (existing.Any(item => item.ToDate is null && string.Equals(item.FullName, name, StringComparison.OrdinalIgnoreCase))) throw new ApiError(409, "Этот льготник уже есть в этой категории");
            db.Beneficiaries.Add(new Beneficiary { AbonentId = abonent.Id, CategoryId = category.Id, FullName = name, FromDate = request.FromDate, Who = user.Who(), AtUtc = DateTime.UtcNow });
            db.AddJournal(abonent.Id, "Льготник добавлен", user.Who(), name);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Льгота закрывается датой, а не удаляется: история нужна для ответов соцзащите за прошлые месяцы
        group.MapPost("/{id:int}/end", async (int id, EndBeneficiaryRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите причину");
            var item = await db.Beneficiaries.FirstOrDefaultAsync(row => row.Id == id) ?? throw new ApiError(404, "Льготник не найден");
            if (item.ToDate is not null) throw new ApiError(409, "Льгота уже прекращена");
            if (item.FromDate is { } from && request.ToDate < from) throw new ApiError(422, "Дата окончания раньше даты начала");
            item.ToDate = request.ToDate;
            db.AddJournal(item.AbonentId, "Льгота прекращена", user.Who(), $"{item.FullName}, {request.ToDate:dd.MM.yyyy}", request.Reason.Trim());
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        // Без apply только проверка: бухгалтер видит результат каждой строки до записи в базу
        group.MapPost("/import", async (IFormFile file, bool? apply, EmirDb db, ClaimsPrincipal user) =>
        {
            if (file.Length == 0) throw new ApiError(422, "Файл пустой");
            if (file.Length > MaxFileBytes) throw new ApiError(422, "Файл больше 5 МБ");
            if (Path.GetExtension(file.FileName).ToLowerInvariant() is not (".csv" or ".txt")) throw new ApiError(422, "Поддерживаются файлы CSV и TXT в формате «лицевой счёт;ФИО;категория;дата начала»");
            using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
            var content = await reader.ReadToEndAsync();

            await using var transaction = await db.BeginMoneyTransaction();
            var abonents = await db.Abonents.ToDictionaryAsync(item => item.Account);
            var categories = (await db.BenefitCategories.Where(item => item.IsActive).ToListAsync()).ToDictionary(item => item.Name.Trim().ToLowerInvariant());
            var existing = await db.Beneficiaries.Where(item => item.ToDate == null).ToListAsync();
            var seen = existing.Select(item => $"{item.AbonentId}|{item.CategoryId}|{item.FullName.ToLowerInvariant()}").ToHashSet();

            var lines = new List<ImportLineDto>();
            var rows = content.Split('\n');
            var added = 0;
            for (var index = 0; index < rows.Length; index++)
            {
                var parts = rows[index].Trim().Trim('﻿').Split(';');
                if (parts.Length < 3 || !parts[0].Trim().All(char.IsDigit) || parts[0].Trim().Length == 0) continue;
                var (account, name, categoryName) = (parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
                DateOnly? from = null;
                var state = "ok";
                if (parts.Length > 3 && parts[3].Trim() != "")
                {
                    if (DateOnly.TryParseExact(parts[3].Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) from = parsed;
                    else state = "bad-date";
                }
                if (state == "ok" && name.Length is 0 or > 200) state = "bad-name";
                if (state == "ok" && !abonents.ContainsKey(account)) state = "not-found";
                if (state == "ok" && !categories.ContainsKey(categoryName.ToLowerInvariant())) state = "unknown-category";
                if (state == "ok")
                {
                    var abonent = abonents[account];
                    var category = categories[categoryName.ToLowerInvariant()];
                    if (!seen.Add($"{abonent.Id}|{category.Id}|{name.ToLowerInvariant()}")) state = "duplicate";
                    else if (apply == true)
                    {
                        db.Beneficiaries.Add(new Beneficiary { AbonentId = abonent.Id, CategoryId = category.Id, FullName = name, FromDate = from, Who = user.Who(), AtUtc = DateTime.UtcNow });
                        db.AddJournal(abonent.Id, "Льготник добавлен", user.Who(), $"{name}, из файла");
                        added++;
                    }
                }
                lines.Add(new ImportLineDto(index + 1, account, name, categoryName, from?.ToString("dd.MM.yyyy"), state));
            }
            if (lines.Count == 0) throw new ApiError(422, "В файле нет строк «лицевой счёт;ФИО;категория;дата начала»");
            if (apply == true) db.AddJournal(null, "Загрузка льготников", user.Who(), $"{Path.GetFileName(file.FileName)}: добавлено {added} из {lines.Count}");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new ImportResultDto(apply == true, added, lines);
        }).DisableAntiforgery().Roles(Roles.Accountant, Roles.Admin);

        // Выгрузка персональных данных за пределы системы: каждая остаётся в журнале
        group.MapGet("/response.csv", async (string period, EmirDb db, ClaimsPrincipal user) =>
        {
            if (!await db.AccountingMonths.AnyAsync(month => month.Period == period)) throw new ApiError(404, "Такого месяца нет");
            var first = PenaltyEngine.FirstDay(period);
            var last = first.AddMonths(1).AddDays(-1);
            var items = (await db.Beneficiaries.OrderBy(item => item.FullName).ToListAsync())
                .Where(item => (item.FromDate is null || item.FromDate <= last) && (item.ToDate is null || item.ToDate > first)).ToList();
            var ids = items.Select(item => item.AbonentId).Distinct().ToList();
            var abonents = await db.Abonents.Include(item => item.Services).Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id);
            var categories = await db.BenefitCategories.ToDictionaryAsync(item => item.Id, item => item.Name);
            var entries = await db.ChargeEntries.Where(entry => entry.Period == period && ids.Contains(entry.AbonentId) && entry.Scheme != "Opening").ToListAsync();
            var ledger = await db.LedgerEntries.Where(entry => ids.Contains(entry.AbonentId) && (entry.Kind == "payment" || entry.Kind == "exchange" || entry.Kind == "refund") && entry.AtUtc >= first.ToDateTime(TimeOnly.MinValue).ToUniversalTime() && entry.AtUtc < last.AddDays(1).ToDateTime(TimeOnly.MinValue).ToUniversalTime()).ToListAsync();
            static string Money(long value) => (value / 100m).ToString("0.00", CultureInfo.GetCultureInfo("ru-RU"));

            var text = new StringBuilder();
            text.AppendLine(string.Join(';', new[] { "№", "год", "месяц", "Тип запроса", "населённый пункт", "улица", "квартира", "лицевой счет у поставщика ЖКУ", "услуга", "ФИО", "категория", "UUID", "начислено, ₽", "оплачено за месяц, ₽", "долг на дату выгрузки, ₽" }.Select(ReportEndpoints.Cell)));
            var number = 0;
            foreach (var item in items)
            {
                var abonent = abonents[item.AbonentId];
                foreach (var line in abonent.Services.Where(service => service.ServiceId != "penalty").OrderBy(service => service.Position))
                {
                    var charged = entries.Where(entry => entry.AbonentId == abonent.Id && entry.ServiceId == line.ServiceId).Sum(entry => entry.Amount);
                    var paid = -ledger.Where(entry => entry.AbonentId == abonent.Id && entry.ServiceId == line.ServiceId).Sum(entry => entry.DebtDelta);
                    number++;
                    text.AppendLine(string.Join(';', new[] { number.ToString(), period[..4], period[5..], "ответ", abonent.Place, abonent.Street, abonent.Flat ?? "", abonent.Account, line.Name, item.FullName, categories.GetValueOrDefault(item.CategoryId, ""), item.Uuid, Money(charged), Money(paid), Money(line.Debt) }.Select(ReportEndpoints.Cell)));
                }
            }
            db.AddJournal(null, "Выгрузка для соцзащиты", user.Who(), $"{Periods.Label(period)}: льготников {items.Count}, строк {number}");
            await db.SaveChangesAsync();
            var bom = Encoding.UTF8.GetPreamble();
            return Results.File(bom.Concat(Encoding.UTF8.GetBytes(text.ToString())).ToArray(), "text/csv; charset=utf-8", $"socprotection-{period}.csv");
        }).Roles(Roles.Accountant, Roles.Admin);
    }
}
