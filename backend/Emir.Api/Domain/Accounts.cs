using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Emir.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Domain;

/// <summary>Правила учётных записей: пароли, блокировка входа, проверка сессии.</summary>
public static partial class Accounts
{
    public const int MinPasswordLength = 12;
    public const string StampClaim = "emir:stamp";
    public const string MustChangeClaim = "emir:must-change";

    // Буквы и цифры, которые не спутать при передаче устно и на бумаге
    private const string TemporaryAlphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private static readonly PasswordHasher<AppUser> Hasher = new();

    /// <summary>Хеш для несуществующего логина: ответ по времени не должен выдавать, есть ли такой пользователь.</summary>
    private static readonly string DummyHash = Hasher.HashPassword(new AppUser { Login = "", DisplayName = "", Role = "", PasswordHash = "" }, Guid.NewGuid().ToString());

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{2,49}$")]
    private static partial Regex LoginFormat();

    public static string NormalizeLogin(string? login) => (login ?? "").Trim().ToLowerInvariant();

    public static void ValidateLogin(string login)
    {
        if (!LoginFormat().IsMatch(login)) throw new ApiError(422, "Логин: от 3 до 50 символов, латинские буквы, цифры, точка, дефис или подчёркивание");
    }

    public static void ValidatePassword(string login, string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength) throw new ApiError(422, $"Пароль должен быть не короче {MinPasswordLength} символов");
        if (password.Contains(login, StringComparison.OrdinalIgnoreCase)) throw new ApiError(422, "Пароль не должен содержать логин");
        if (password.Distinct().Count() < 4) throw new ApiError(422, "Пароль слишком простой: в нём почти нет разных символов");
    }

    public static void SetPassword(AppUser user, string password, bool mustChange)
    {
        user.PasswordHash = Hasher.HashPassword(user, password);
        user.MustChangePassword = mustChange;
        // Все сессии с прежним паролем перестают действовать
        user.SessionStamp = Guid.NewGuid().ToString("N");
        user.FailedLogins = 0;
        user.LockedUntilUtc = null;
    }

    public static bool CheckPassword(AppUser user, string? password) =>
        Hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "") != PasswordVerificationResult.Failed;

    public static void BurnTimeForUnknownLogin(string? password) =>
        Hasher.VerifyHashedPassword(new AppUser { Login = "", DisplayName = "", Role = "", PasswordHash = "" }, DummyHash, password ?? "");

    public static string GenerateTemporaryPassword() => RandomNumberGenerator.GetString(TemporaryAlphabet, 16);

    public static bool IsLocked(AppUser user) => user.LockedUntilUtc is { } until && until > DateTime.UtcNow;

    public static ClaimsPrincipal Principal(AppUser user) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.Name, user.Login),
        new Claim(ClaimTypes.GivenName, user.DisplayName),
        new Claim(ClaimTypes.Role, user.Role),
        new Claim(StampClaim, user.SessionStamp),
        new Claim(MustChangeClaim, user.MustChangePassword ? "1" : "0"),
    ], CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>
    /// Каждый запрос сверяется с базой: уволенный сотрудник и старый пароль теряют доступ сразу,
    /// а новая роль начинает действовать без повторного входа.
    /// </summary>
    public static async Task ValidateSession(CookieValidatePrincipalContext context)
    {
        var login = context.Principal?.Identity?.Name;
        var stamp = context.Principal?.FindFirstValue(StampClaim);
        var db = context.HttpContext.RequestServices.GetRequiredService<EmirDb>();
        var user = login is null ? null : await db.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Login == login);
        if (user is not { IsActive: true } || user.SessionStamp != stamp)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        context.ReplacePrincipal(Principal(user));
    }

    /// <summary>Защита от потери доступа: хотя бы один действующий администратор должен остаться.</summary>
    public static async Task EnsureAdminRemains(EmirDb db)
    {
        db.ChangeTracker.DetectChanges();
        var saved = await db.Users.AsNoTracking().Where(user => user.Role == Roles.Admin && user.IsActive).Select(user => user.Id).ToListAsync();
        var changed = db.ChangeTracker.Entries<AppUser>().ToList();
        var left = saved.Count(id => changed.All(entry => entry.Entity.Id != id)) + changed.Count(entry => entry.Entity.Role == Roles.Admin && entry.Entity.IsActive);
        if (left == 0) throw new ApiError(409, "Должен остаться хотя бы один действующий администратор");
    }
}
