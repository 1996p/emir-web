using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record LoginRequest(string Login, string Password);
public record UserDto(string Login, string DisplayName, string Role, bool MustChangePassword);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public static class EndpointHelpers
{
    public static RouteHandlerBuilder Roles(this RouteHandlerBuilder builder, params string[] roles) =>
        builder.RequireAuthorization(policy => policy.RequireRole(roles));

    public static string Who(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.GivenName) ?? user.Identity?.Name ?? "Система";

    public static void AddJournal(this EmirDb db, string? abonentId, string action, string who, string details, string? reason = null) =>
        db.Journal.Add(new JournalRecord { AbonentId = abonentId, AtUtc = DateTime.UtcNow, Action = action, Who = who, Details = details, Reason = reason });

    public static async Task<Abonent> GetAbonent(this EmirDb db, string id) =>
        await db.Abonents.Include(abonent => abonent.Services).FirstOrDefaultAsync(abonent => abonent.Id == id)
            ?? throw new ApiError(404, "Абонент не найден");

    public static string Money(long kopecks) => (kopecks / 100m).ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
}

public static class AuthEndpoints
{
    public static void MapAuth(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/auth");

        group.MapPost("/login", async (LoginRequest request, EmirDb db, HttpContext context, IConfiguration configuration) =>
        {
            var login = Accounts.NormalizeLogin(request.Login);
            var user = await db.Users.FirstOrDefaultAsync(item => item.Login == login);

            // Ответ один на всё: нельзя узнать, существует ли логин, отключён он или заблокирован
            var failed = new ApiError(401, "Неверный логин или пароль. После нескольких ошибок вход блокируется на время");
            if (user is null)
            {
                Accounts.BurnTimeForUnknownLogin(request.Password);
                throw failed;
            }
            var right = Accounts.CheckPassword(user, request.Password);
            if (!user.IsActive || Accounts.IsLocked(user)) throw failed;
            if (!right)
            {
                user.FailedLogins++;
                if (user.FailedLogins >= configuration.GetValue("Auth:LockoutAttempts", 5))
                {
                    user.LockedUntilUtc = DateTime.UtcNow.AddMinutes(configuration.GetValue("Auth:LockoutMinutes", 15));
                    user.FailedLogins = 0;
                    db.AddJournal(null, "Блокировка входа", "Система", user.Login);
                }
                await db.SaveChangesAsync();
                throw failed;
            }

            user.FailedLogins = 0;
            user.LockedUntilUtc = null;
            user.LastLoginUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await context.SignInAsync(Accounts.Principal(user));
            return new UserDto(user.Login, user.DisplayName, user.Role, user.MustChangePassword);
        }).RequireRateLimiting("login");

        group.MapPost("/password", async (ChangePasswordRequest request, EmirDb db, HttpContext context, ClaimsPrincipal principal) =>
        {
            var user = await db.Users.FirstAsync(item => item.Login == principal.Identity!.Name);
            if (!Accounts.CheckPassword(user, request.CurrentPassword)) throw new ApiError(422, "Текущий пароль указан неверно");
            Accounts.ValidatePassword(user.Login, request.NewPassword);
            if (request.NewPassword == request.CurrentPassword) throw new ApiError(422, "Новый пароль должен отличаться от текущего");
            Accounts.SetPassword(user, request.NewPassword, mustChange: false);
            db.AddJournal(null, "Смена пароля", user.DisplayName, user.Login);
            await db.SaveChangesAsync();
            // Эта сессия продолжается с новой отметкой, все остальные завершаются
            await context.SignInAsync(Accounts.Principal(user));
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("login");

        group.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync();
            return Results.NoContent();
        });

        group.MapGet("/me", (ClaimsPrincipal user) => new UserDto(user.Identity!.Name!, user.Who(), user.FindFirstValue(ClaimTypes.Role)!, user.FindFirstValue(Accounts.MustChangeClaim) == "1")).RequireAuthorization();
    }
}
