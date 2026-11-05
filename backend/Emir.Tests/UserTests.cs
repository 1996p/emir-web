using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Emir.Tests;

public sealed class UserTests : IDisposable
{
    private const string Good = "new-Long-password-1";
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private static Task<HttpResponseMessage> PostLogin(HttpClient client, string login, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { login, password });

    private async Task<HttpClient> Login(string login = "admin", string password = "test-password")
    {
        var client = NewClient();
        (await PostLogin(client, login, password)).EnsureSuccessStatusCode();
        return client;
    }

    private EmirDb Db() => new(new DbContextOptionsBuilder<EmirDb>().UseNpgsql(_factory.ConnectionString).Options);

    private static async Task<int> UserId(HttpClient admin, string login)
    {
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        return users.EnumerateArray().First(user => user.GetProperty("login").GetString() == login).GetProperty("id").GetInt32();
    }

    private static async Task<string> Create(HttpClient admin, string login, string role = "cashier")
    {
        var response = await admin.PostAsJsonAsync("/api/admin/users", new { login, displayName = "Новый Сотрудник", role });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("temporaryPassword").GetString()!;
    }

    private static Task<HttpResponseMessage> ChangePassword(HttpClient client, string current, string next) =>
        client.PostAsJsonAsync("/api/auth/password", new { currentPassword = current, newPassword = next });

    [Fact]
    public async Task NewUser_MustChangeTemporaryPassword_BeforeAnythingElse()
    {
        var admin = await Login();
        var temporary = await Create(admin, "new.cashier");
        Assert.Equal(16, temporary.Length);

        var client = await Login("new.cashier", temporary);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.True(me.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/abonents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a1" })).StatusCode);

        (await ChangePassword(client, temporary, Good)).EnsureSuccessStatusCode();
        // Сессия, из которой сменили пароль, продолжает работать
        (await client.GetAsync("/api/abonents")).EnsureSuccessStatusCode();
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("mustChangePassword").GetBoolean());

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostLogin(NewClient(), "new.cashier", temporary)).StatusCode);
        await Login("new.cashier", Good);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("xx-kassa1-xx-long")]
    public async Task WeakPassword_IsRejected(string password)
    {
        var client = await Login("kassa1");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ChangePassword(client, "test-password", password)).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_ChecksCurrent_RefusesSame_AndEndsOtherSessions()
    {
        var first = await Login("kassa1");
        var second = await Login("kassa1");
        (await second.GetAsync("/api/auth/me")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ChangePassword(first, "wrong-password-1", Good)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ChangePassword(first, "test-password", "test-password")).StatusCode);

        (await ChangePassword(first, "test-password", Good)).EnsureSuccessStatusCode();
        (await first.GetAsync("/api/auth/me")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task DisabledUser_LosesAccessImmediately_AndCannotLogin()
    {
        var admin = await Login();
        var cashier = await Login("kassa1");
        var id = await UserId(admin, "kassa1");

        (await admin.PostAsJsonAsync($"/api/admin/users/{id}/active", new { active = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.GetAsync("/api/abonents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostLogin(NewClient(), "kassa1", "test-password")).StatusCode);

        (await admin.PostAsJsonAsync($"/api/admin/users/{id}/active", new { active = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.GetAsync("/api/abonents")).StatusCode);
        await Login("kassa1");
    }

    [Fact]
    public async Task RoleChange_AppliesWithoutNewLogin()
    {
        var admin = await Login();
        var client = await Login("kassa1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/checks")).StatusCode);
        var id = await UserId(admin, "kassa1");
        (await admin.PostAsJsonAsync($"/api/admin/users/{id}", new { displayName = "Новое Имя", role = "accountant" })).EnsureSuccessStatusCode();

        (await client.GetAsync("/api/checks")).EnsureSuccessStatusCode();
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("accountant", me.GetProperty("role").GetString());
        Assert.Equal("Новое Имя", me.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task ResetPassword_EndsSessions_AndRequiresChange()
    {
        var admin = await Login();
        var cashier = await Login("kassa1");
        var response = await admin.PostAsync($"/api/admin/users/{await UserId(admin, "kassa1")}/reset-password", null);
        response.EnsureSuccessStatusCode();
        var temporary = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("temporaryPassword").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.GetAsync("/api/abonents")).StatusCode);
        var client = await Login("kassa1", temporary);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/abonents")).StatusCode);
    }

    [Fact]
    public async Task Lockout_AfterFiveFailures_EvenWithRightPassword_AndUnlocksByAdmin()
    {
        var client = NewClient();
        for (var attempt = 0; attempt < 5; attempt++) Assert.Equal(HttpStatusCode.Unauthorized, (await PostLogin(client, "kassa1", "wrong-password")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostLogin(client, "kassa1", "test-password")).StatusCode);

        var admin = await Login();
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        Assert.True(users.EnumerateArray().First(user => user.GetProperty("login").GetString() == "kassa1").GetProperty("locked").GetBoolean());
        (await admin.PostAsync($"/api/admin/users/{await UserId(admin, "kassa1")}/unlock", null)).EnsureSuccessStatusCode();
        await Login("kassa1");
    }

    [Fact]
    public async Task SuccessfulLogin_ResetsFailureCounter()
    {
        var client = NewClient();
        for (var round = 0; round < 3; round++)
        {
            for (var attempt = 0; attempt < 4; attempt++) await PostLogin(client, "kassa1", "wrong-password");
            await Login("kassa1");
        }
    }

    [Fact]
    public async Task LoginFailures_LookTheSame_ForUnknownDisabledAndLocked()
    {
        var unknown = await PostLogin(NewClient(), "nobody", "wrong-password");
        var wrong = await PostLogin(NewClient(), "kassa1", "wrong-password");
        Assert.Equal(unknown.StatusCode, wrong.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_IgnoresCaseAndSpaces()
    {
        await Login(" KASSA1 ");
    }

    [Fact]
    public async Task Admin_CannotLockThemselvesOut()
    {
        var admin = await Login();
        var self = await UserId(admin, "admin");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/admin/users/{self}/active", new { active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/admin/users/{self}", new { displayName = "Я", role = "cashier" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/users/{self}/reset-password", null)).StatusCode);
        (await admin.PostAsJsonAsync($"/api/admin/users/{self}", new { displayName = "Новое имя", role = "admin" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ParallelAdmins_CannotRemoveEachOther_LeavingNoAdmin()
    {
        var admin = await Login();
        var temporary = await Create(admin, "second.admin", "admin");
        var second = await Login("second.admin", temporary);
        (await ChangePassword(second, temporary, Good)).EnsureSuccessStatusCode();
        var firstId = await UserId(admin, "admin");
        var secondId = await UserId(admin, "second.admin");

        await Task.WhenAll(
            admin.PostAsJsonAsync($"/api/admin/users/{secondId}/active", new { active = false }),
            second.PostAsJsonAsync($"/api/admin/users/{firstId}/active", new { active = false }));

        await using var db = Db();
        Assert.True(await db.Users.CountAsync(user => user.Role == Roles.Admin && user.IsActive) >= 1);
    }

    [Theory]
    [InlineData("ab", "Имя", "cashier")]
    [InlineData("плохой логин", "Имя", "cashier")]
    [InlineData("good.login", " ", "cashier")]
    [InlineData("good.login", "Имя", "boss")]
    public async Task Create_ValidatesInput(string login, string displayName, string role)
    {
        var admin = await Login();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/admin/users", new { login, displayName, role })).StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateLoginIsRefused_AndLoginIsNormalized()
    {
        var admin = await Login();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/users", new { login = "KASSA1", displayName = "Дубль", role = "cashier" })).StatusCode);
        var created = await admin.PostAsJsonAsync("/api/admin/users", new { login = " New.User ", displayName = "Имя", role = "cashier" });
        Assert.Equal("new.user", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("login").GetString());
    }

    [Fact]
    public async Task OnlyAdminManagesUsers_AndPasswordNeverReachesJournalOrList()
    {
        foreach (var login in new[] { "kassa1", "oper1", "buh1" })
        {
            var client = await Login(login);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/users", new { login = "x.y.z", displayName = "Х", role = "admin" })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().GetAsync("/api/admin/users")).StatusCode);

        var admin = await Login();
        var temporary = await Create(admin, "secret.user");
        var list = await admin.GetStringAsync("/api/admin/users");
        Assert.DoesNotContain(temporary, list);
        Assert.DoesNotContain("passwordHash", list, StringComparison.OrdinalIgnoreCase);
        await using var db = Db();
        Assert.DoesNotContain(await db.Journal.ToListAsync(), item => item.Details.Contains(temporary));
        Assert.Contains(await db.Journal.ToListAsync(), item => item.Action == "Новый сотрудник" && item.Details.Contains("secret.user"));
    }

    [Fact]
    public async Task OldSession_WithoutStamp_IsRejected_WhenUserDeleted()
    {
        var cashier = await Login("kassa1");
        await using (var db = Db())
        {
            await db.Users.Where(user => user.Login == "kassa1").ExecuteUpdateAsync(setters => setters.SetProperty(user => user.SessionStamp, "other"));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.GetAsync("/api/auth/me")).StatusCode);
    }
}
