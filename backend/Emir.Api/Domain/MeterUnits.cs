namespace Emir.Api.Domain;

/// <summary>
/// Единицы показаний тепловых счётчиков. Основная единица Гкал: тариф и начисление считаются в ней,
/// показания в других единицах переводятся в Гкал перед расчётом.
/// </summary>
public static class MeterUnits
{
    public const string Gcal = "Гкал";

    /// <summary>
    /// Сколько Гкал в одной единице показаний. Коэффициенты как в старом ЕМИР
    /// (настройка Common/UnitConvert: GKAL/J=4.186#MWH/GKAL=0.86#KWH/GKAL=0.00086):
    /// 1 ГДж = 1/4,186 Гкал, 1 МВт·ч = 0,86 Гкал, 1 кВт·ч = 0,00086 Гкал.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, decimal> ToGcal = new Dictionary<string, decimal>
    {
        [Gcal] = 1m,
        ["ГДж"] = 1m / 4.186m,
        ["МВт·ч"] = 0.86m,
        ["кВт·ч"] = 0.00086m,
    };

    /// <summary>Множитель для перевода в Гкал. Без единицы показания уже в единицах тарифа (например, м³ для воды).</summary>
    public static decimal Factor(string? unit) => unit is null ? 1m : ToGcal.TryGetValue(unit, out var factor) ? factor : 1m;

    /// <summary>Проверяет единицу из запроса: null или пусто значит «в единицах тарифа».</summary>
    public static string? Parse(string? unit)
    {
        var value = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        if (value is not null && !ToGcal.ContainsKey(value)) throw new ApiError(422, $"Неизвестная единица «{value}». Допустимо: {string.Join(", ", ToGcal.Keys)}");
        return value;
    }
}
