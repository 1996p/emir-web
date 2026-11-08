using System.Threading.RateLimiting;
using Emir.Api.Data;
using Emir.Api.Domain;
using Emir.Api.Endpoints;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<EmirDb>(options => options.UseNpgsql(connectionString));

// Ключи cookie должны переживать перезапуск, иначе всех пользователей выбросит из системы
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrEmpty(keysPath))
    builder.Services.AddDataProtection().SetApplicationName("emir").PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "emir.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    // API возвращает коды ответа и не перенаправляет на страницу входа
    options.Events.OnValidatePrincipal = Accounts.ValidateSession;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
// Ответы API сжимаются: список абонентов весит мегабайты и медленно идёт по сети и через туннели.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(options => options.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(options => options.Level = System.IO.Compression.CompressionLevel.Fastest);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("Auth:LoginAttemptsPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EmirDb>();

    // Команды обслуживания: dotnet Emir.Api.dll migrate | create-admin <логин> <имя> | start-period <ГГГГ-ММ> | import-data <папка> <ГГГГ-ММ> [--apply]
    var command = args.FirstOrDefault(arg => !arg.StartsWith('-') && !arg.Contains('='));
    if (command == "migrate")
    {
        await db.Database.MigrateAsync();
        Console.WriteLine("Миграции применены");
        return;
    }
    if (command == "update-abonent-attributes")
    {
        var file = args.SkipWhile(arg => arg != "update-abonent-attributes").Skip(1).FirstOrDefault();
        if (file is null) { Console.Error.WriteLine("Использование: dotnet Emir.Api.dll update-abonent-attributes <abonents.csv>"); Environment.ExitCode = 1; return; }
        var (updated, missing) = await DataMigration.UpdateAbonentAttributes(db, file);
        Console.WriteLine($"Обновлено абонентов: {updated}, не найдено по ЛС: {missing}");
        return;
    }
    if (command == "import-references")
    {
        var file = args.SkipWhile(arg => arg != "import-references").Skip(1).FirstOrDefault();
        if (file is null) { Console.Error.WriteLine("Использование: dotnet Emir.Api.dll import-references <references.json>"); Environment.ExitCode = 1; return; }
        Console.WriteLine($"Справочников загружено: {await DataMigration.ImportReferences(db, file)}");
        await DirectoryData.EnsureHouseTypes(db);
        return;
    }
    if (command == "create-admin")
    {
        var values = args.SkipWhile(arg => arg != "create-admin").Skip(1).ToArray();
        var password = Environment.GetEnvironmentVariable("EMIR_ADMIN_PASSWORD");
        if (values.Length < 2 || string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("Использование: EMIR_ADMIN_PASSWORD=... dotnet Emir.Api.dll create-admin <логин> <имя>");
            Environment.ExitCode = 1;
            return;
        }
        try
        {
            await AdminTools.CreateAdmin(db, values[0], values[1], password);
            Console.WriteLine($"Администратор {values[0]} создан");
        }
        catch (ApiError error)
        {
            Console.Error.WriteLine(error.Message);
            Environment.ExitCode = 1;
        }
        return;
    }

    if (command == "import-data")
    {
        // Пробный прогон по умолчанию: запись только с явным --apply
        var values = args.SkipWhile(arg => arg != "import-data").Skip(1).Where(arg => !arg.StartsWith("--")).ToArray();
        if (values.Length < 2)
        {
            Console.Error.WriteLine("Использование: dotnet Emir.Api.dll import-data <папка> <ГГГГ-ММ> [--apply]");
            Environment.ExitCode = 1;
            return;
        }
        var report = await DataMigration.Run(db, values[0], values[1], args.Contains("--apply"));
        Console.WriteLine(DataMigration.Format(report));
        if (!report.Ok) Environment.ExitCode = 1;
        return;
    }

    if (command == "start-period")
    {
        try
        {
            var value = args.SkipWhile(arg => arg != "start-period").Skip(1).FirstOrDefault() ?? "";
            await AdminTools.StartPeriod(db, value);
            Console.WriteLine($"Расчётный месяц {value} открыт");
        }
        catch (ApiError error)
        {
            Console.Error.WriteLine(error.Message);
            Environment.ExitCode = 1;
        }
        return;
    }

    if (app.Environment.IsProduction())
    {
        // В боевой среде схема меняется только осознанно, отдельной командой
        var pending = await db.Database.GetPendingMigrationsAsync();
        if (pending.Any()) throw new InvalidOperationException("База не обновлена. Сначала выполните команду migrate");
    }
    else
    {
        await db.Database.MigrateAsync();
        var demoPassword = app.Configuration["Seed:DemoPassword"];
        if (!string.IsNullOrEmpty(demoPassword)) Seed.Run(db, demoPassword);
    }
    // Справочники, которых не было в старой системе, заводятся сами (схему не меняет)
    if (await db.ReferenceTables.AnyAsync()) await DirectoryData.EnsureHouseTypes(db);
}

app.UseResponseCompression();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (ApiError error)
    {
        context.Response.StatusCode = error.Status;
        await context.Response.WriteAsJsonAsync(new { detail = error.Message });
    }
    catch (Exception error) when (DbConflict.IsConflict(error))
    {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { detail = "Эти данные одновременно изменил другой пользователь. Повторите операцию" });
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
        app.Logger.LogError(error, "Необработанная ошибка");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { detail = "Внутренняя ошибка сервера" });
    }
});

app.UseRateLimiter();
app.UseAuthentication();

// Запрет на сервере, а не только на экране: с временным паролем нельзя ничего, кроме его смены
app.Use(async (context, next) =>
{
    if (context.User.FindFirst(Accounts.MustChangeClaim)?.Value == "1" && !context.Request.Path.StartsWithSegments("/api/auth"))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { detail = "Сначала смените временный пароль" });
        return;
    }
    await next();
});

app.UseAuthorization();

var api = app.MapGroup("/api");
api.MapAuth();
api.MapAbonents();
api.MapCash();
api.MapReadings();
api.MapPeriod();
api.MapCorrections();
api.MapReports();
api.MapExchange();
api.MapReference();
api.MapTariffs();
api.MapChecks();
api.MapDocuments();
api.MapUsers();
api.MapAbonentDetails();
api.MapBenefits();
api.MapHeatNorms();

app.Run();

/// <summary>Конфликты одновременной работы: сериализация, взаимоблокировка, повтор уникального значения.</summary>
public static class DbConflict
{
    public static bool IsConflict(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: "40001" or "40P01" or "23505" }) return true;
        return false;
    }
}

public partial class Program;
