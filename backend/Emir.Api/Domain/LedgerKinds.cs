namespace Emir.Api.Domain;

public static class LedgerKinds
{
    public static string Name(string kind) => kind switch
    {
        "opening" => "Входящий остаток",
        "reading" => "Начисление по показанию",
        "charge" => "Начисление",
        "penalty" => "Пени",
        "payment" => "Оплата",
        "refund" => "Возврат",
        "correction" => "Корректировка",
        "exchange" => "Оплата из реестра",
        "reclose" => "Перезакрытие",
        "cancel" => "Отмена начисления",
        _ => kind,
    };
}
