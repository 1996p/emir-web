using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Emir.Api.Endpoints;

public record ManagedUserDto(int Id, string Login, string DisplayName, string Role, bool IsActive, bool MustChangePassword, bool Locked, string? LastLogin, bool IsSelf);
public record CreateUserRequest(string Login, string DisplayName, string Role);
public record UpdateUserRequest(string DisplayName, string Role);
public record SetActiveRequest(bool Active);
public record TemporaryPasswordDto(string Login, string TemporaryPassword);

public static class UserEndpoints
{
    private static readonly string[] AllRoles = [Roles.Cashier, Roles.Service, Roles.Accountant, Roles.Admin];

    private static void Validate(string? displayName, string role)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ApiError(422, "Укажите имя сотрудника");
        if (!AllRoles.Contains(role)) throw new ApiError(422, "Неизвестная роль");
    }

    private static async Task<AppUser> Find(EmirDb db, int id) => await db.Users.FirstOrDefaultAsync(user => user.Id == id) ?? throw new ApiError(404, "Сотрудник не найден");

    private static void EnsureNotSelf(AppUser target, ClaimsPrincipal user, string message)
    {
        if (target.Login == user.Identity!.Name) throw new ApiError(409, message);
    }

    public static void MapUsers(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/admin/users");

        group.MapGet("/", async (EmirDb db, ClaimsPrincipal user) =>
        {
            var users = await db.Users.OrderBy(item => item.Login).ToListAsync();
            return users.Select(item => new ManagedUserDto(item.Id, item.Login, item.DisplayName, item.Role, item.IsActive, item.MustChangePassword, Accounts.IsLocked(item), item.LastLoginUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), item.Login == user.Identity!.Name));
        }).Roles(Roles.Admin);

        group.MapPost("/", async (CreateUserRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            var login = Accounts.NormalizeLogin(request.Login);
            Accounts.ValidateLogin(login);
            Validate(request.DisplayName, request.Role);
            await using var transaction = await db.BeginMoneyTransaction();
            if (await db.Users.AnyAsync(item => item.Login == login)) throw new ApiError(409, $"Логин {login} уже занят");

            var created = new AppUser { Login = login, DisplayName = request.DisplayName.Trim(), Role = request.Role, PasswordHash = "" };
            var temporary = Accounts.GenerateTemporaryPassword();
            Accounts.SetPassword(created, temporary, mustChange: true);
            db.Users.Add(created);
            db.AddJournal(null, "Новый сотрудник", user.Who(), $"{login}, {created.DisplayName}, роль {created.Role}");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new TemporaryPasswordDto(login, temporary);
        }).Roles(Roles.Admin);

        group.MapPost("/{id:int}", async (int id, UpdateUserRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            Validate(request.DisplayName, request.Role);
            await using var transaction = await db.BeginMoneyTransaction();
            var target = await Find(db, id);
            if (target.Role != request.Role) EnsureNotSelf(target, user, "Свою роль менять нельзя: так можно случайно потерять доступ");
            var details = $"{target.Login}: {target.DisplayName}, роль {target.Role}, теперь {request.DisplayName.Trim()}, роль {request.Role}";
            target.DisplayName = request.DisplayName.Trim();
            target.Role = request.Role;
            await Accounts.EnsureAdminRemains(db);
            db.AddJournal(null, "Изменён сотрудник", user.Who(), details);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        }).Roles(Roles.Admin);

        group.MapPost("/{id:int}/active", async (int id, SetActiveRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            await using var transaction = await db.BeginMoneyTransaction();
            var target = await Find(db, id);
            if (!request.Active) EnsureNotSelf(target, user, "Себя отключить нельзя");
            target.IsActive = request.Active;
            // Вернувшийся сотрудник входит заново: прежние сессии не оживают
            target.SessionStamp = Guid.NewGuid().ToString("N");
            await Accounts.EnsureAdminRemains(db);
            db.AddJournal(null, request.Active ? "Сотрудник включён" : "Сотрудник отключён", user.Who(), target.Login);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        }).Roles(Roles.Admin);

        group.MapPost("/{id:int}/reset-password", async (int id, EmirDb db, ClaimsPrincipal user) =>
        {
            await using var transaction = await db.BeginMoneyTransaction();
            var target = await Find(db, id);
            EnsureNotSelf(target, user, "Свой пароль меняйте в разделе «Мой профиль»");
            var temporary = Accounts.GenerateTemporaryPassword();
            Accounts.SetPassword(target, temporary, mustChange: true);
            db.AddJournal(null, "Сброс пароля", user.Who(), target.Login);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new TemporaryPasswordDto(target.Login, temporary);
        }).Roles(Roles.Admin);

        group.MapPost("/{id:int}/unlock", async (int id, EmirDb db, ClaimsPrincipal user) =>
        {
            await using var transaction = await db.BeginMoneyTransaction();
            var target = await Find(db, id);
            target.FailedLogins = 0;
            target.LockedUntilUtc = null;
            db.AddJournal(null, "Снята блокировка входа", user.Who(), target.Login);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        }).Roles(Roles.Admin);
    }
}
