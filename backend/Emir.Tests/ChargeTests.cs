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
    public void CurrentPenalty_StartsOnEleventhOfSecondMonth_LikeOldSystemJournals()
    {
        // Начисление за апрель 2022: первая пеня в журнале старой системы 11 июня, 10 июня её ещё нет.
        var settings = PenaltyEngine.Current;
        var due = PenaltyEngine.DueDate("2022-04", settings.PayDayNextMonth);
        Assert.Equal(0m, PenaltyEngine.FractionFor(new DateOnly(2022, 6, 10).DayNumber - due.DayNumber, settings));
        Assert.Equal(1m / 300m, PenaltyEngine.FractionFor(new DateOnly(2022, 6, 11).DayNumber - due.DayNumber, settings));
    }

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

    [Theory]
    // Сезон окт–апр (10..4) с переходом через год: зимние месяцы в сезоне, летние — нет.
    [InlineData(10, 4, 1, true)]
    [InlineData(10, 4, 4, true)]
    [InlineData(10, 4, 10, true)]
    [InlineData(10, 4, 5, false)]
    [InlineData(10, 4, 7, false)]
    [InlineData(10, 4, 9, false)]
    // Сезон без перехода (май–сентябрь).
    [InlineData(5, 9, 7, true)]
    [InlineData(5, 9, 4, false)]
    [InlineData(5, 9, 10, false)]
    public void InHeatingSeason_HandlesWrapAround(int start, int end, int month, bool expected) =>
        Assert.Equal(expected, ChargeEngine.InHeatingSeason(month, start, end));

    [Fact]
    public void SeasonalScheme_InSeason_ChargesLikeAreaNormTarif()
    {
        // В сезоне сезонная схема считается как AreaNormTarif: 50 × 2 × 10 ₽ = 1000 ₽.
        var result = Calc("AreaNormTarifSeasonal", tariffNorm: "2", season: true);
        Assert.Null(result.Error);
        Assert.Equal(100000, result.Amount);
    }

    [Fact]
    public void MonthlyNormFormat_UsesCurrentMonth()
    {
        Assert.Equal(5m, Calc("ContractNorm", contractNorm: "01=1#11=5#12=9").Volume);
        // Месяца нет в нормативе: старая система начисляет 0 без ошибки (Krsk.Contract.Scheme.ContractNorm)
        var missing = Calc("ContractNorm", contractNorm: "01=1#12=9");
        Assert.Null(missing.Error);
        Assert.Equal(0, missing.Amount);
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

    // Расхождения, найденные сверкой с кодом старой системы (GKH.Contract.Scheme.*)
    [Fact]
    public void MonthlyAmount_IsRublesNotVolume()
    {
        // «Ежемесячные суммы по заказу»: 1500 ₽ в ноябре при цене 10 ₽ — это 1500 ₽ и 150 единиц, а не 15000 ₽
        var result = Calc("MonthlyAmount", contractNorm: "10=1000#11=1500");
        Assert.Equal(150000, result.Amount);
        Assert.Equal(150m, result.Volume);
        Assert.Equal(0, Calc("MonthlyAmount", contractNorm: "10=1000").Amount);
        Assert.Null(Calc("MonthlyAmount", contractNorm: "10=1000").Error);
    }

    [Fact]
    public void MonthlyQuantity_TakesVolumeOfMonth()
    {
        Assert.Equal(12.5m, Calc("MonthlyQuantity", contractNorm: "11=12,5").Volume);
        Assert.Equal(12500, Calc("MonthlyQuantity", contractNorm: "11=12,5").Amount);
        Assert.Equal(0, Calc("MonthlyQuantity", contractNorm: "12=3").Amount);
    }

    [Fact]
    public void MeterAndHeatLost_RoundVolume_LikeOldSystem()
    {
        // Объём округляется до 4 знаков у физлиц и до 3 у юрлиц до умножения на цену
        Assert.Equal(1.2346m, Calc("MeterApartment", usage: 1.23456m).Volume);
        Assert.Equal(1.235m, ChargeEngine.Calculate(new ChargeInput("MeterApartment", 10m, 11, MeterUsage: 1.23456m, IsOrg: true)).Volume);
        Assert.Equal(33.3333m, Calc("HeatLost", contractNorm: "100", share: 33.33333m).Volume);
    }

    [Fact]
    public void HouseMeterLeftover_FailsWhenApartmentsUseMoreThanHouse()
    {
        var meter = new HouseMeterData(Usage: 30, IndividualUsage: 40, TotalArea: 1000, IndividualArea: 200, TotalResidents: 40, IndividualResidents: 10);
        Assert.NotNull(Calc("HouseMeterLeftover", meter: meter).Error);
    }

    [Fact]
    public void AreaSchemes_RequireArea_HeatHouseNormShowsGcal()
    {
        Assert.NotNull(Calc("Area", area: 0).Error);
        Assert.NotNull(Calc("AreaNormTarif", area: 0, tariffNorm: "2").Error);
        Assert.Equal(50m, Calc("AreaKrsk", contractNorm: "0").Volume);
        // Тепло по норме дома: деньги по площади, объём в Гкал = норма × площадь
        var heat = Calc("HeatHouseNorm", houseNorm: 0.02m);
        Assert.Equal(1m, heat.Volume);
        Assert.Equal(50000, heat.Amount);
        // Норматив в заказе не задан: считается как тепло по норме дома
        Assert.Equal(50000, Calc("HeatHouseNormKrsk").Amount);
        Assert.Equal(20000, Calc("HeatHouseNormKrsk", contractNorm: "20").Amount);
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
        // Август: срок 15.09, весь ноябрь просрочка 47-76 дней. За день 100000 × 9,5 % / 300 = 31,67 → 32 коп., × 30 = 960
        Assert.Equal(960, PenaltyEngine.Calculate([new("2026-08", 100000)], "2026-11", Settings));
    }

    [Fact]
    public void ThirdTier_HandCalculated()
    {
        // Май: срок 15.06, в ноябре просрочка больше 90 дней. За день 100000 × 9,5 % / 130 = 73,08 → 73 коп., × 30 = 2190
        Assert.Equal(2190, PenaltyEngine.Calculate([new("2026-05", 100000)], "2026-11", Settings));
    }

    [Fact]
    public void BoundaryMonth_MixesTiers()
    {
        // Сентябрь: срок 15.10, 31-й день просрочки приходится на 15.11.
        // С 15 по 30 ноября 16 дней по 32 коп. = 512
        Assert.Equal(512, PenaltyEngine.Calculate([new("2026-09", 100000)], "2026-11", Settings));
    }

    [Fact]
    public void DebtBeforeStartPeriod_IsIgnored() => Assert.Equal(0, PenaltyEngine.Calculate([new("2016-01", 100000)], "2026-11", Settings));

    [Fact]
    public void RateChangesDuringMonth()
    {
        var settings = Settings with { Rates = [new(new DateOnly(2025, 1, 1), .10m), new(new DateOnly(2026, 11, 16), .20m)] };
        // 15 дней по 33 коп. (10 %) и 15 дней по 67 коп. (20 %) = 1500
        Assert.Equal(1500, PenaltyEngine.Calculate([new("2026-08", 100000)], "2026-11", settings));
    }

    [Fact]
    public void DailyAmountBelowKopeck_IsNotCharged_LikeOldSystem()
    {
        // 2 ₽ долга: за день 200 × 9,5 % / 300 = 0,06 коп. — старая система такую пеню не начисляет
        Assert.Equal(0, PenaltyEngine.Calculate([new("2026-08", 200)], "2026-11", Settings));
    }

    [Fact]
    public void DebtsOfOneMonth_AreSummedBeforeRounding()
    {
        // Две услуги по 500 ₽ за август: считается как 1000 ₽ одного месяца, а не два раза по 16 коп.
        Assert.Equal(960, PenaltyEngine.Calculate([new("2026-08", 50000), new("2026-08", 50000)], "2026-11", Settings));
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
