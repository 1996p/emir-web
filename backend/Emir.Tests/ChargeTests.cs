using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emir.Api.Domain;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Emir.Tests;

public class ChargeEngineTests
{
    private static ChargeResult Calc(string scheme, decimal price = 10m, decimal area = 50m, int residents = 3, string? serviceNorm = null, string? tariffNorm = null, string? contractNorm = null, decimal? houseNorm = null, decimal? usage = null, decimal? share = null, bool season = true, decimal? loss = null, decimal? houseArea = null, HouseMeterData? meter = null, int month = 11) =>
        ChargeEngine.Calculate(new ChargeInput(scheme, price, month, area, residents, serviceNorm, tariffNorm, contractNorm, houseNorm, usage, share, season, loss, houseArea, meter));

    [Fact]
    public void NormHouse_RoundsVolumeToFourDigitsBeforePrice_LikeOldSystem()
    {
        // 33,33 м² × 0,01725 = 0,5749425 Гкал -> 0,5749; × 3128,09 ₽ = 1798,34 ₽. Без округления вышло бы 1798,47 ₽.
        var result = ChargeEngine.Calculate(new ChargeInput("NormHouse", 3128.09m, 1, 33.33m, 0, HouseHeatNorm: 0.01725m));
        Assert.Equal(0.5749m, result.Volume);
        Assert.Equal(179834, result.Amount);
    }

    [Fact]
    public void AllDocumentedSchemesAreHandled()
    {
        var meter = new HouseMeterData(100, 40, 1000, 200, 40, 10);
        foreach (var scheme in ChargeEngine.Schemes)
        {
            var result = Calc(scheme, serviceNorm: "2", tariffNorm: "2", contractNorm: "2", houseNorm: 2, usage: 2, share: 50, loss: 10, houseArea: 500, meter: meter);
            Assert.Null(result.Error);
        }
    }

    // Суммы посчитаны вручную по формулам из документации
    [Theory]
    [InlineData("Norm", 6.47, 3 * 6.47, 194.1)]
    [InlineData("NormTarif", 7.117, 3 * 7.117, 213.51)]
    [InlineData("ServiceNorm", 6.47, 6.47, 64.7)]
    public void NormSchemes(string scheme, double norm, double volume, double rubles)
    {
        var text = norm.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = Calc(scheme, serviceNorm: text, tariffNorm: text);
        Assert.Equal((decimal)volume, result.Volume, 3);
        Assert.Equal((long)Math.Round(rubles * 100), result.Amount);
    }

    [Fact]
    public void AreaSchemes()
    {
        Assert.Equal(50m, Calc("HeatHouseNorm").Volume);
        Assert.Equal(50000, Calc("HeatHouseNorm").Amount);
        Assert.Equal(50m, Calc("AreaKrsk").Volume);
        Assert.Equal(20m, Calc("AreaKrsk", contractNorm: "20").Volume);
        Assert.Equal(50m * .01875m, Calc("NormHouse", houseNorm: .01875m).Volume);
        Assert.Equal(50m * 7.117m, Calc("AreaNormTarif", tariffNorm: "7.117").Volume);
    }

    [Fact]
    public void SeasonalScheme_OutOfSeason_IsZero()
    {
        var result = Calc("AreaNormTarifSeasonal", tariffNorm: "2", season: false);
        Assert.Null(result.Error);
        Assert.Equal(0, result.Amount);
    }

    [Fact]
    public void MonthlyNormFormat_UsesCurrentMonth()
    {
        Assert.Equal(5m, Calc("ContractNorm", contractNorm: "01=1#11=5#12=9").Volume);
        Assert.NotNull(Calc("ContractNorm", contractNorm: "01=1#12=9").Error);
    }

    [Fact]
    public void HeatLost_UsesShareOfMonth()
    {
        Assert.Equal(17m, Calc("HeatLost", contractNorm: "100", share: 17).Volume);
    }

    [Fact]
    public void HouseMeterSchemes_ExcludeIndividualMeters()
    {
        var meter = new HouseMeterData(Usage: 100, IndividualUsage: 40, TotalArea: 1000, IndividualArea: 200, TotalResidents: 40, IndividualResidents: 10);
        // (100 - 40) × 50 / (1000 - 200) = 3,75
        Assert.Equal(3.75m, Calc("MeterHouse", meter: meter).Volume);
        // (100 - 40) × 3 / (40 - 10) = 6
        Assert.Equal(6m, Calc("HouseMeterLeftover", meter: meter).Volume);
    }

    [Fact]
    public void Rounding_IsToKopecks_AwayFromZero()
    {
        Assert.Equal(13, ChargeEngine.Money(1m, 0.125m));
    }

    [Fact]
    public void MissingData_IsReported_NotZeroCharged()
    {
        Assert.NotNull(Calc("Norm").Error);
        Assert.NotNull(Calc("MeterApartment").Error);
        Assert.NotNull(Calc("Unknown").Error);
    }
}

public class PenaltyEngineTests
{
    private static readonly PenaltySettings Settings = new("2017-07", 15, [new(0, 0), new(31, 1m / 300m), new(91, 1m / 130m)], [new(new DateOnly(2025, 1, 1), .095m)]);

    [Fact]
    public void DueDate_Is15thOfNextMonth() => Assert.Equal(new DateOnly(2026, 11, 15), PenaltyEngine.DueDate("2026-10", 15));

    [Fact]
    public void FirstThirtyDays_AreFree()
    {
        // Октябрь: срок 15.11, в ноябре максимум 15 дней просрочки
        Assert.Equal(0, PenaltyEngine.Calculate([new("2026-10", 100000)], "2026-11", Settings));
    }

    [Fact]
    public void SecondTier_HandCalculated()
    {
        // Август: срок 15.09, весь ноябрь просрочка 47-76 дней. 100000 × 9,5 % / 300 × 30 = 950
        Assert.Equal(950, PenaltyEngine.Calculate([new("2026-08", 100000)], "2026-11", Settings));
    }

    [Fact]
    public void ThirdTier_HandCalculated()
    {
        // Май: срок 15.06, в ноябре просрочка больше 90 дней. 100000 × 9,5 % / 130 × 30 = 2192,3
        Assert.Equal(2192, PenaltyEngine.Calculate([new("2026-05", 100000)], "2026-11", Settings));
    }

    [Fact]
    public void BoundaryMonth_MixesTiers()
    {
        // Сентябрь: срок 15.10, 31-й день просрочки приходится на 15.11.
        // С 15 по 30 ноября 16 дней: 100000 × 9,5 % / 300 × 16 = 506,67
        Assert.Equal(507, PenaltyEngine.Calculate([new("2026-09", 100000)], "2026-11", Settings));
    }

    [Fact]
    public void DebtBeforeStartPeriod_IsIgnored() => Assert.Equal(0, PenaltyEngine.Calculate([new("2016-01", 100000)], "2026-11", Settings));

    [Fact]
    public void RateChangesDuringMonth()
    {
        var settings = Settings with { Rates = [new(new DateOnly(2025, 1, 1), .10m), new(new DateOnly(2026, 11, 16), .20m)] };
        // 15 дней по 10 % и 15 дней по 20 %: 100000 / 300 × (1,5 + 3) = 1500
        Assert.Equal(1500, PenaltyEngine.Calculate([new("2026-08", 100000)], "2026-11", settings));
    }

    [Fact]
    public void Allocate_KeepsDebtInNewestCharges()
    {
        var result = PenaltyEngine.Allocate(150, [new("2026-11", 100), new("2026-10", 100), new("2026-09", 100)]);
        Assert.Equal([new OutstandingDebt("2026-11", 100), new OutstandingDebt("2026-10", 50)], result);
    }
}

public sealed class ChargingApiTests : IDisposable
{
    private readonly EmirFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> Accountant()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login = "buh1", password = "test-password" })).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<(long Balance, JsonElement Abonent)> Get(HttpClient client, string id)
    {
        var abonent = await client.GetFromJsonAsync<JsonElement>($"/api/abonents/{id}");
        var debt = abonent.GetProperty("services").EnumerateArray().Sum(line => line.GetProperty("debt").GetInt64());
        return (abonent.GetProperty("credit").GetInt64() - debt, abonent);
    }

    [Fact]
    public async Task Preview_DoesNotChangeBalances()
    {
        var client = await Accountant();
        var before = (await Get(client, "a2")).Balance;
        var preview = await client.GetFromJsonAsync<JsonElement>("/api/period/charges/preview");
        Assert.True(preview.GetProperty("total").GetInt64() > 0);
        Assert.Equal(before, (await Get(client, "a2")).Balance);
    }

    [Fact]
    public async Task ChargeRun_AddsAreaTimesPrice()
    {
        var client = await Accountant();
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        // Петров: 44 м² × 35,43 ₽ = 1558,92 ₽
        Assert.Equal(-155892, (await Get(client, "a2")).Balance);
    }

    [Fact]
    public async Task Recharge_DoesNotDoubleTheCharge()
    {
        var client = await Accountant();
        for (var attempt = 0; attempt < 3; attempt++)
            (await client.PostAsync("/api/abonents/a2/recharge", null)).EnsureSuccessStatusCode();
        Assert.Equal(-155892, (await Get(client, "a2")).Balance);
    }

    [Fact]
    public async Task MissingReading_IsReportedAndNotCharged()
    {
        var client = await Accountant();
        var before = (await Get(client, "a7")).Balance;
        var result = await (await client.PostAsync("/api/abonents/a7/recharge", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(result.GetProperty("issues").EnumerateArray(), issue => issue.GetProperty("problem").GetString() == "Нет показаний за месяц");
        // Волков: 47 м² × 35,43 = 1665,21 ₽ только за тепло
        Assert.Equal(before - 166521, (await Get(client, "a7")).Balance);
    }

    [Fact]
    public async Task ReadingBeforeRun_IsNotChargedTwice()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { login = "admin", password = "test-password" })).EnsureSuccessStatusCode();
        var before = (await Get(client, "a7")).Balance;
        (await client.PostAsJsonAsync("/api/readings", new { abonentId = "a7", serviceId = "water", value = 335m })).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/abonents/a7/recharge", null)).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/abonents/a7/recharge", null)).EnsureSuccessStatusCode();
        // 4 м³ × 52 ₽ = 208 ₽ и тепло 1665,21 ₽
        Assert.Equal(before - 20800 - 166521, (await Get(client, "a7")).Balance);
    }

    [Fact]
    public async Task Month_FullCycle_ClosesAndThenBlocksChanges()
    {
        var client = await Accountant();
        foreach (var step in new[] { "charges", "checks", "penalty", "close" })
            (await client.PostAsync($"/api/period/steps/{step}/run", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/abonents/a2/recharge", null)).StatusCode);
        var closed = await client.GetFromJsonAsync<JsonElement>("/api/period/closed");
        Assert.Equal(1, closed.GetArrayLength());
    }

    [Fact]
    public async Task PenaltyStep_OnFreshOctoberDebts_AddsNothing()
    {
        var client = await Accountant();
        (await client.PostAsync("/api/period/steps/charges/run", null)).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/period/steps/checks/run", null)).EnsureSuccessStatusCode();
        var before = (await Get(client, "a3")).Balance;
        (await client.PostAsync("/api/period/steps/penalty/run", null)).EnsureSuccessStatusCode();
        // Долг октября ещё не просрочен на 31 день
        Assert.Equal(before, (await Get(client, "a3")).Balance);
    }
}
