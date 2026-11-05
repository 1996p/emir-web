using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Data;

/// <summary>Команды обслуживания, которые запускаются из командной строки, а не через API.</summary>
public static class AdminTools
{
    /// <summary>Задаёт первый расчётный месяц новой базы.</summary>
    public static async Task StartPeriod(EmirDb db, string period)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(period ?? "", @"^\d{4}-(0[1-9]|1[0-2])$")) throw new ApiError(422, "Укажите месяц в формате ГГГГ-ММ");
        if (await db.AccountingMonths.AnyAsync()) throw new ApiError(409, "Расчётный месяц уже задан");
        db.AccountingMonths.Add(new AccountingMonth { Period = period! });
        db.PeriodSteps.AddRange(Periods.NewSteps());
        db.Journal.Add(new JournalRecord { AtUtc = DateTime.UtcNow, Action = "Расчётный месяц", Who = "Командная строка", Details = $"Первый месяц: {period}" });
        await db.SaveChangesAsync();
    }

    public static async Task CreateAdmin(EmirDb db, string login, string displayName, string password)
    {
        login = Accounts.NormalizeLogin(login);
        Accounts.ValidateLogin(login);
        Accounts.ValidatePassword(login, password);
        if (await db.Users.AnyAsync(user => user.Login == login)) throw new ApiError(409, $"Пользователь {login} уже существует");

        var admin = new AppUser { Login = login, DisplayName = displayName, Role = Roles.Admin, PasswordHash = "" };
        Accounts.SetPassword(admin, password, mustChange: false);
        db.Users.Add(admin);
        db.Journal.Add(new JournalRecord { AtUtc = DateTime.UtcNow, Action = "Создан администратор", Who = "Командная строка", Details = login });
        await db.SaveChangesAsync();
    }
}
