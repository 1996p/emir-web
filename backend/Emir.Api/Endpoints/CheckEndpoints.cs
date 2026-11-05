using System.Text;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record CheckMonthDto(string Period, string Title);
public record ChecksDto(string Period, string Title, List<CheckMonthDto> Months, List<CheckGroup> Groups);

public static class CheckEndpoints
{
    private static async Task<string> Resolve(EmirDb db, string? period)
    {
        if (string.IsNullOrEmpty(period)) return await Periods.WorkPeriod(db);
        if (!await db.AccountingMonths.AnyAsync(month => month.Period == period)) throw new ApiError(404, "Такого месяца нет");
        return period;
    }

    public static void MapChecks(this RouteGroupBuilder api)
    {
        api.MapGet("/checks", async (string? period, EmirDb db) =>
        {
            var result = await ChecksService.Run(db, await Resolve(db, period));
            var months = await db.AccountingMonths.OrderByDescending(month => month.Period).Select(month => month.Period).ToListAsync();
            return new ChecksDto(result.Period, result.Title, months.Select(month => new CheckMonthDto(month, Periods.Title(month))).ToList(), result.Groups);
        }).Roles(Roles.Accountant, Roles.Admin);

        api.MapGet("/checks/{id}/export.csv", async (string id, string? period, EmirDb db) =>
        {
            var result = await ChecksService.Run(db, await Resolve(db, period));
            var group = result.Groups.FirstOrDefault(item => item.Id == id) ?? throw new ApiError(404, "Такой сверки нет");
            var text = new StringBuilder();
            text.AppendLine(string.Join(';', group.Columns.Select(ReportEndpoints.Cell)));
            foreach (var row in group.Rows) text.AppendLine(string.Join(';', row.Cells.Select(ReportEndpoints.Cell)));
            var bom = Encoding.UTF8.GetPreamble();
            return Results.File(bom.Concat(Encoding.UTF8.GetBytes(text.ToString())).ToArray(), "text/csv; charset=utf-8", $"{id}-{result.Period}.csv");
        }).Roles(Roles.Accountant, Roles.Admin);
    }
}
