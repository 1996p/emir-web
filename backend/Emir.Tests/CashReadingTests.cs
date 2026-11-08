using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Emir.Tests;

/// <summary>Показания, принятые в кассе: каждое уходит в счётчик своей услуги.</summary>
public sealed class CashReadingTests : IDisposable
{
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> Login(string login)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = "test-password" })).EnsureSuccessStatusCode();
        return client;
    }

    private static decimal Last(JsonElement abonent, string serviceId) =>
        abonent.GetProperty("services").EnumerateArray().First(line => line.GetProperty("id").GetString() == serviceId).GetProperty("meter").GetProperty("lastReading").GetDecimal();

    [Fact]
    public async Task TwoMeters_EachReadingGoesToItsService()
    {
        // У a1 есть счётчик воды; ставим второй — на тепло
        var oper = await Login("oper1");
        (await oper.PostAsJsonAsync("/api/abonents/a1/services/heat/meter", new { number = "ТП-1", lastReading = 10m, unit = "Гкал" })).EnsureSuccessStatusCode();
        var kassa = await Login("kassa1");

        // Одно показание без услуги при двух счётчиках — неясно, куда его записать
        var ambiguous = await kassa.PostAsJsonAsync("/api/cash/payments", new { abonentId = "a1", source = "cash-person", reading = 420m, allocations = new[] { new { serviceId = "water", amount = 1000L } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ambiguous.StatusCode);

        var response = await kassa.PostAsJsonAsync("/api/cash/payments", new
        {
            abonentId = "a1", source = "cash-person", allocations = new[] { new { serviceId = "water", amount = 1000L } },
            readings = new[] { new { serviceId = "water", value = 415m }, new { serviceId = "heat", value = 12m } },
        });
        response.EnsureSuccessStatusCode();
        var abonent = await kassa.GetFromJsonAsync<JsonElement>("/api/abonents/a1");
        Assert.Equal(415m, Last(abonent, "water"));
        Assert.Equal(12m, Last(abonent, "heat"));
    }
}
