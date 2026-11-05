using System.Globalization;
using System.Security.Claims;
using System.Text;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record ExchangeLineDto(string Id, int Line, string Key, long Amount, string State);
public record ExchangeBatchDto(string Id, string Partner, string When, string Title, string FileName, string KeyColumn, int Total, long? Sum, bool Posted, List<ExchangeLineDto> Lines);

public static class ExchangeEndpoints
{
    private const long MaxFileBytes = 5 * 1024 * 1024;

    public static ExchangeBatchDto ToDto(this ExchangeBatch batch) => new(
        batch.Id, batch.Partner, batch.AtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), batch.Title, batch.FileName, batch.KeyColumn, batch.Lines.Count,
        batch.IsPayments ? batch.Lines.Where(line => line.State == "ok").Sum(line => line.Amount) : null, batch.Posted,
        batch.Lines.OrderBy(line => line.Line).Select(line => new ExchangeLineDto(line.Id.ToString(), line.Line, line.Key, line.Amount, line.State)).ToList());

    public static void MapExchange(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/exchange");

        group.MapGet("/batches", async (EmirDb db) =>
        {
            var batches = await db.ExchangeBatches.Include(batch => batch.Lines).ToListAsync();
            return batches.OrderByDescending(batch => batch.AtUtc).Select(batch => batch.ToDto());
        }).Roles(Roles.Accountant, Roles.Admin);

        group.MapGet("/operations", (string partner) => Catalog.ExchangeOperations.GetValueOrDefault(partner, [])).Roles(Roles.Accountant, Roles.Admin);

        group.MapPost("/upload", async (IFormFile file, EmirDb db, ClaimsPrincipal user) =>
        {
            if (file.Length == 0) throw new ApiError(422, "Файл пустой");
            if (file.Length > MaxFileBytes) throw new ApiError(422, "Файл больше 5 МБ");
            var fileName = Path.GetFileName(file.FileName);
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (extension is not (".csv" or ".txt")) throw new ApiError(422, "Пока поддерживаются файлы CSV и TXT в формате «лицевой счёт;сумма»");

            using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
            var content = await reader.ReadToEndAsync();
            var accounts = (await db.Abonents.Select(abonent => abonent.Account).ToListAsync()).ToHashSet();
            var lines = ParseLines(content, accounts);
            if (lines.Count == 0) throw new ApiError(422, "В файле нет строк «лицевой счёт;сумма»");

            var id = Guid.NewGuid().ToString("N");
            lines.ForEach(line => line.BatchId = id);
            var batch = new ExchangeBatch { Id = id, Partner = DetectPartner(fileName), AtUtc = DateTime.UtcNow, Title = "Реестр оплат", FileName = fileName, KeyColumn = "Лицевой счёт", IsPayments = true, Lines = lines };
            db.ExchangeBatches.Add(batch);
            db.AddJournal(null, "Загрузка файла", user.Who(), $"{fileName}, строк: {lines.Count}");
            await db.SaveChangesAsync();
            return batch.ToDto();
        }).DisableAntiforgery().Roles(Roles.Accountant, Roles.Admin);

        group.MapPost("/batches/{id}/post", async (string id, EmirDb db, ClaimsPrincipal user) =>
        {
            await using var transaction = await db.BeginMoneyTransaction();
            var batch = await db.ExchangeBatches.Include(item => item.Lines).FirstOrDefaultAsync(item => item.Id == id) ?? throw new ApiError(404, "Загрузка не найдена");
            if (batch.Posted) throw new ApiError(409, "Эта загрузка уже проведена");

            await db.BeginOperation("exchange", user.Who(), batch.FileName);
            var posted = 0;
            if (batch.IsPayments)
            {
                foreach (var line in batch.Lines.Where(line => line.State == "ok"))
                {
                    var abonent = await db.Abonents.Include(item => item.Services).FirstOrDefaultAsync(item => item.Account == line.Key);
                    if (abonent is null) continue;
                    Billing.GiveToAbonent(abonent, line.Amount);
                    db.Payments.Add(new Payment { Id = Guid.NewGuid().ToString("N"), AbonentId = abonent.Id, AtUtc = DateTime.UtcNow, Source = "Банковские платежи", Cashier = batch.Partner, Amount = line.Amount });
                    db.AddJournal(abonent.Id, "Платёж из реестра", user.Who(), $"{EndpointHelpers.Money(line.Amount)} ₽, {batch.FileName}");
                    posted++;
                }
            }
            batch.Posted = true;
            db.AddJournal(null, "Проведение загрузки", user.Who(), $"{batch.FileName}, проведено строк: {posted}");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return batch.ToDto();
        }).Roles(Roles.Accountant, Roles.Admin);
    }

    private static string DetectPartner(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        if (name.Contains("sber")) return "Сбербанк";
        if (name.Contains("plato")) return "Платосфера";
        if (name.Contains("gorod")) return "Город";
        if (name.Contains("1c") || name.Contains("1с")) return "1С";
        return "Сбербанк";
    }

    public static List<ExchangeLine> ParseLines(string content, HashSet<string> accounts)
    {
        var result = new List<ExchangeLine>();
        var seen = new HashSet<string>();
        var rows = content.Split('\n');
        for (var index = 0; index < rows.Length; index++)
        {
            var parts = rows[index].Trim().Split(';');
            if (parts.Length < 2) continue;
            var key = parts[0].Trim();
            var raw = parts[1].Trim().Replace(" ", "").Replace('.', ',');
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.GetCultureInfo("ru-RU"), out var rubles) || rubles <= 0) continue;
            var amount = (long)Math.Round(rubles * 100, MidpointRounding.AwayFromZero);
            var fingerprint = $"{key}|{amount}";
            var state = !accounts.Contains(key) ? "not-found" : !seen.Add(fingerprint) ? "duplicate" : "ok";
            result.Add(new ExchangeLine { BatchId = "", Line = index + 1, Key = key, Amount = amount, State = state });
        }
        return result;
    }
}
