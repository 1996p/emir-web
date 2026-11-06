using System.Security.Claims;
using System.Text.Json;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record DocumentRowDto(int Id, string Kind, string KindName, string Number, int Revision, string Period, string PeriodTitle, string Date, string AbonentId, string Abonent, long Total, string Status, bool Stale, string? Reason);
public record DocumentsDto(string Period, string Title, List<CheckMonthDto> Months, bool CanGenerate, string? CannotGenerate, List<DocumentRowDto> Documents);
public record PrintDocumentDto(int Id, string Kind, string Status, JsonElement Data);
public record ProfileRequest(string Name, string Inn, string? Kpp, string Address, string? BankName, string? Bik, string? CorrAccount, string? Account, string? Director, string? ChiefAccountant, decimal? VatRate, int HeatSeasonStartMonth = 10, int HeatSeasonEndMonth = 4);
public record GenerateRequest(string Period);
public record ReissueRequest(string Reason);
public record ReissueStaleRequest(string Period, string Reason);
public record CounterDto(int Year, string Kind, string KindName, int Last);
public record SetCounterRequest(int Year, string Kind, int Last);

public static class DocumentEndpoints
{
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static void MapDocuments(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/documents").RequireAuthorization();

        group.MapGet("/", async (string? period, string? kind, string? abonentId, bool? history, EmirDb db) =>
        {
            var months = await db.AccountingMonths.OrderByDescending(month => month.Period).ToListAsync();
            var closed = months.Where(month => month.ClosedAtUtc != null).ToList();
            var chosen = period ?? closed.FirstOrDefault()?.Period ?? months.First().Period;
            var query = db.BillingDocuments.AsQueryable();
            // Для карточки абонента нужны все его месяцы, для списка выбранный
            if (abonentId is null) query = query.Where(document => document.Period == chosen);
            else query = query.Where(document => document.AbonentId == abonentId);
            if (!string.IsNullOrEmpty(kind)) query = query.Where(document => document.Kind == kind);
            if (history != true) query = query.Where(document => document.Status == "active");
            var documents = await query.OrderByDescending(document => document.Period).ThenBy(document => document.Kind).ThenBy(document => document.Id).ToListAsync();
            var names = await db.Abonents.Select(item => new { item.Id, item.ShortName }).ToDictionaryAsync(item => item.Id, item => item.ShortName);

            var month = months.First(item => item.Period == chosen);
            var why = month.ClosedAtUtc is null ? "Документы формируются после закрытия месяца" : month.ReopenedAtUtc is not null ? "Месяц исправляется. Сначала завершите перезакрытие" : null;
            return new DocumentsDto(chosen, Periods.Title(chosen), months.Select(item => new CheckMonthDto(item.Period, Periods.Title(item.Period))).ToList(), why is null, why,
                documents.Select(document => new DocumentRowDto(document.Id, document.Kind, DocumentService.KindName(document.Kind), document.Number, document.Revision, document.Period, Periods.Title(document.Period),
                    PenaltyEngine.FirstDay(document.Period).AddMonths(1).AddDays(-1).ToString("dd.MM.yyyy"), document.AbonentId, names.GetValueOrDefault(document.AbonentId, document.AbonentId), document.Total, document.Status, document.Stale, document.Reason)).ToList());
        });

        // Несколько документов одной страницей: пакетная печать
        group.MapGet("/print", async (string ids, EmirDb db) =>
        {
            var parsed = ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(item => int.TryParse(item, out var id) ? id : throw new ApiError(422, "Неверный список документов")).Distinct().ToList();
            if (parsed.Count is 0 or > 1000) throw new ApiError(422, "Выберите от 1 до 1000 документов");
            var documents = await db.BillingDocuments.Where(document => parsed.Contains(document.Id)).OrderBy(document => document.AbonentId).ThenBy(document => document.Kind).ToListAsync();
            return documents.Select(document => new PrintDocumentDto(document.Id, document.Kind, document.Status, JsonSerializer.Deserialize<JsonElement>(document.Data)));
        });

        group.MapPost("/generate", async (GenerateRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            await using var transaction = await db.BeginMoneyTransaction();
            var result = await DocumentService.Generate(db, request.Period, user.Who());
            db.AddJournal(null, "Документы", user.Who(), $"{Periods.Label(request.Period)}: создано {result.Created}, уже было {result.Existing}");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return result;
        }).Roles(Roles.Accountant, Roles.Admin);

        group.MapPost("/{id:int}/reissue", async (int id, ReissueRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите причину");
            await using var transaction = await db.BeginMoneyTransaction();
            var document = await DocumentService.Reissue(db, id, user.Who(), request.Reason.Trim());
            db.AddJournal(document.AbonentId, "Перевыпуск документа", user.Who(), $"{DocumentService.KindName(document.Kind)} № {document.Number}, исправление {document.Revision - 1}, {Periods.Label(document.Period)}", request.Reason.Trim());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.Ok(new { id = document.Id });
        }).Roles(Roles.Accountant, Roles.Admin);

        group.MapPost("/reissue-stale", async (ReissueStaleRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiError(422, "Укажите причину");
            await using var transaction = await db.BeginMoneyTransaction();
            var ids = await db.BillingDocuments.Where(document => document.Period == request.Period && document.Status == "active" && document.Stale).OrderBy(document => document.Id).Select(document => document.Id).ToListAsync();
            foreach (var id in ids)
            {
                var document = await DocumentService.Reissue(db, id, user.Who(), request.Reason.Trim());
                db.AddJournal(document.AbonentId, "Перевыпуск документа", user.Who(), $"{DocumentService.KindName(document.Kind)} № {document.Number}, исправление {document.Revision - 1}, {Periods.Label(document.Period)}", request.Reason.Trim());
            }
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new { reissued = ids.Count };
        }).Roles(Roles.Accountant, Roles.Admin);

        group.MapGet("/profile", async (EmirDb db) => await db.OrganizationProfiles.FirstOrDefaultAsync() is { } profile
            ? new ProfileRequest(profile.Name, profile.Inn, profile.Kpp, profile.Address, profile.BankName, profile.Bik, profile.CorrAccount, profile.Account, profile.Director, profile.ChiefAccountant, profile.VatRate, profile.HeatSeasonStartMonth, profile.HeatSeasonEndMonth)
            : new ProfileRequest("", "", null, "", null, null, null, null, null, null, null));

        group.MapPost("/profile", async (ProfileRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            await using var transaction = await db.BeginMoneyTransaction();
            var profile = await db.OrganizationProfiles.FirstOrDefaultAsync();
            if (profile is null)
            {
                profile = new OrganizationProfile { Name = "", Inn = "", Address = "" };
                db.OrganizationProfiles.Add(profile);
            }
            profile.Name = request.Name?.Trim() ?? "";
            profile.Inn = request.Inn?.Trim() ?? "";
            profile.Kpp = Blank(request.Kpp);
            profile.Address = request.Address?.Trim() ?? "";
            profile.BankName = Blank(request.BankName);
            profile.Bik = Blank(request.Bik);
            profile.CorrAccount = Blank(request.CorrAccount);
            profile.Account = Blank(request.Account);
            profile.Director = Blank(request.Director);
            profile.ChiefAccountant = Blank(request.ChiefAccountant);
            profile.VatRate = request.VatRate;
            profile.HeatSeasonStartMonth = request.HeatSeasonStartMonth;
            profile.HeatSeasonEndMonth = request.HeatSeasonEndMonth;
            DocumentService.Validate(profile);
            db.AddJournal(null, "Реквизиты организации", user.Who(), $"{profile.Name}, ИНН {profile.Inn}, НДС {(profile.VatRate is { } rate ? $"{rate:0.##} %" : "нет")}");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        }).Roles(Roles.Accountant, Roles.Admin);

        group.MapGet("/counters", async (EmirDb db) =>
        {
            var counters = await db.DocumentCounters.OrderByDescending(item => item.Year).ThenBy(item => item.Kind).ToListAsync();
            return counters.Select(item => new CounterDto(item.Year, item.Kind, DocumentService.KindName(item.Kind), item.Last));
        }).Roles(Roles.Accountant, Roles.Admin);

        // Как в старой системе номер задаётся по году. Назад нельзя: номера повторились бы
        group.MapPost("/counters", async (SetCounterRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            if (request.Kind is not (DocumentService.Bill or DocumentService.Act or DocumentService.Facture)) throw new ApiError(422, "Нумеруются счета, акты и счета-фактуры");
            if (request.Year is < 2000 or > 2100 || request.Last < 0) throw new ApiError(422, "Неверный год или номер");
            await using var transaction = await db.BeginMoneyTransaction();
            var counter = await db.DocumentCounters.FirstOrDefaultAsync(item => item.Year == request.Year && item.Kind == request.Kind);
            if (counter is null)
            {
                counter = new DocumentCounter { Year = request.Year, Kind = request.Kind };
                db.DocumentCounters.Add(counter);
            }
            else if (request.Last < counter.Last) throw new ApiError(409, $"Номер не может быть меньше уже выданного: {counter.Last}");
            db.AddJournal(null, "Нумерация документов", user.Who(), $"{DocumentService.KindName(request.Kind)}, {request.Year}: последний номер {counter.Last}, теперь {request.Last}");
            counter.Last = request.Last;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        }).Roles(Roles.Accountant, Roles.Admin);
    }
}
