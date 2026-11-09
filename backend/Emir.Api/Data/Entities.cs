namespace Emir.Api.Data;

public static class Roles
{
    public const string Cashier = "cashier";
    public const string Service = "service";
    public const string Accountant = "accountant";
    public const string Admin = "admin";
}

public class AppUser
{
    public int Id { get; set; }
    public required string Login { get; set; }
    public required string DisplayName { get; set; }
    public required string Role { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Меняется вместе с паролем и доступом: сессии с прежним значением перестают действовать.</summary>
    public string SessionStamp { get; set; } = Guid.NewGuid().ToString("N");
    public bool MustChangePassword { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
}

public class Abonent
{
    public required string Id { get; set; }
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public required string ShortName { get; set; }
    /// <summary>Населённый пункт: значение из справочника «Города».</summary>
    public string Place { get; set; } = "";
    /// <summary>Улица и дом одной строкой, как в старой системе. Для адресов из справочников собирается из StreetName и House.</summary>
    public required string Street { get; set; }
    /// <summary>Улица из справочника «Улицы» (без дома). Нет значения: адрес перенесён строкой и в справочниках не сверен.</summary>
    public string? StreetName { get; set; }
    /// <summary>Номер дома из справочника «Дома».</summary>
    public string? House { get; set; }
    public string? Flat { get; set; }
    public required string Account { get; set; }
    public string? Phone { get; set; }
    public string? Inn { get; set; }
    /// <summary>КПП организации.</summary>
    public string? Kpp { get; set; }
    public string? Email { get; set; }
    public DateOnly? BirthDate { get; set; }
    /// <summary>Паспорт физлица, зашифрован (Data Protection). Расшифровывает только просмотр администратора с записью в журнал.</summary>
    public string? PassportProtected { get; set; }
    public int Residents { get; set; }
    public decimal Area { get; set; }
    /// <summary>Общая площадь квартиры, м². В расчёте не участвует (расчёт идёт по отапливаемой Area), нужна для документов и отчётов, как в старой системе.</summary>
    public decimal? TotalArea { get; set; }
    /// <summary>Флаги лицевого счёта из старой системы: не выгружать в реестр Платосферы, не принимать платежи из файла 1С, не показывать в сверке одинаковых ЛС.</summary>
    public bool HideInPlatosfera { get; set; }
    public bool HideIn1C { get; set; }
    public bool IgnoreDuplicateCheck { get; set; }
    /// <summary>Сельсовет, котельная, тепловая камера, категория дома и материал стен: из справочников старой системы, для отчётов и отбора.</summary>
    public string? District { get; set; }
    public string? Boiler { get; set; }
    public string? HeatChamber { get; set; }
    public string? HouseCategory { get; set; }
    public string? WallMaterial { get; set; }
    /// <summary>Норма теплопотребления категории дома, Гкал на м² в месяц. Нужна схеме NormHouse.</summary>
    public decimal? HouseHeatNorm { get; set; }
    /// <summary>Переплата в копейках.</summary>
    public long Credit { get; set; }
    public string? Note { get; set; }
    /// <summary>Согласие на передачу персональных данных. Нет значения: не указано, это не то же, что отказ.</summary>
    public bool? Consent { get; set; }
    public DateTime? ConsentAtUtc { get; set; }
    public string? OwnershipDocument { get; set; }
    public List<ServiceLine> Services { get; set; } = [];

    public long Balance => Credit - Services.Sum(line => line.Debt);
}

public class ServiceLine
{
    public required string AbonentId { get; set; }
    public required string ServiceId { get; set; }
    public int Position { get; set; }
    public required string Name { get; set; }
    public string State { get; set; } = "Работает";
    public required string TariffName { get; set; }
    public required string ChargeStatus { get; set; }
    public long Debt { get; set; }
    /// <summary>Общедомовой счётчик, по которому начисляется услуга со схемой MeterHouse.</summary>
    public string? HouseMeterId { get; set; }
    public string? MeterNumber { get; set; }
    public decimal? MeterLastReading { get; set; }
    public string? MeterVerifiedUntil { get; set; }
    public decimal MeterAverage { get; set; } = 5;
    /// <summary>Единица показаний квартирного счётчика (Гкал, ГДж, кВт·ч…). Null: показания в единицах тарифа, без перевода.</summary>
    public string? MeterUnit { get; set; }
    /// <summary>Тариф открытого месяца. История лежит в ServiceTerm.</summary>
    public string? TariffId { get; set; }
    /// <summary>Норматив по заказу: число или помесячно, «01=1#02=2».</summary>
    public string? ContractNorm { get; set; }
}

public class Tariff
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Scheme { get; set; }
    public string? ServiceNorm { get; set; }
    public string? TariffNorm { get; set; }
    public bool PenaltyEnabled { get; set; } = true;
    /// <summary>Единица измерения для документов.</summary>
    public string? Unit { get; set; }
}

/// <summary>Цена тарифа: действует с месяца FromPeriod до следующей записи.</summary>
public class TariffPrice
{
    public int Id { get; set; }
    public required string TariffId { get; set; }
    public required string FromPeriod { get; set; }
    /// <summary>Цена за единицу в рублях: тарифы могут иметь больше двух знаков.</summary>
    public decimal Price { get; set; }
    public required string Who { get; set; }
    public DateTime AtUtc { get; set; }
    public string? Reason { get; set; }
}

/// <summary>Тариф и состояние услуги абонента: действуют с месяца FromPeriod до следующей записи.</summary>
public class ServiceTerm
{
    public int Id { get; set; }
    public required string AbonentId { get; set; }
    public required string ServiceId { get; set; }
    public required string FromPeriod { get; set; }
    public string? TariffId { get; set; }
    public required string State { get; set; }
    public required string Who { get; set; }
    public DateTime AtUtc { get; set; }
    public string? Reason { get; set; }
}

public class ChargeEntry
{
    public int Id { get; set; }
    public required string AbonentId { get; set; }
    public required string ServiceId { get; set; }
    public required string Period { get; set; }
    public required string Scheme { get; set; }
    public decimal Volume { get; set; }
    public long Amount { get; set; }
    /// <summary>Тариф и цена на момент расчёта: начисление остаётся объяснимым после смены цены.</summary>
    public string? TariffId { get; set; }
    public decimal? Price { get; set; }
    /// <summary>Данные абонента на момент расчёта: перезакрытие считает по ним, а не по сегодняшним.</summary>
    public decimal? Area { get; set; }
    public int? Residents { get; set; }
    public string? ContractNorm { get; set; }
    /// <summary>Нет для счётчиков: их сумма попадает в долг при вводе показания.</summary>
    public bool AppliedToDebt { get; set; }
    public DateTime AtUtc { get; set; }
}

/// <summary>Общедомовой счётчик. Расход делится по площади между услугами, привязанными к счётчику.</summary>
public class HouseMeter
{
    public required string Id { get; set; }
    public string Place { get; set; } = "";
    public required string Street { get; set; }
    public string Number { get; set; } = "";
    public string Unit { get; set; } = "Гкал";
    /// <summary>Сколько Гкал в единице показаний: для Гкал 1, для ГДж около 0,239.</summary>
    public decimal UnitFactor { get; set; } = 1;
}

/// <summary>Показание общедомового счётчика на конец месяца.</summary>
public class HouseMeterReading
{
    public int Id { get; set; }
    public required string HouseMeterId { get; set; }
    public required string Period { get; set; }
    public decimal Value { get; set; }
    public string Who { get; set; } = "";
    public DateTime AtUtc { get; set; }
}

/// <summary>Справочник старой системы, перенесённый как есть: только для просмотра.</summary>
public class ReferenceTable
{
    public required string Name { get; set; }
    /// <summary>JSON: список названий колонок.</summary>
    public required string ColumnsJson { get; set; }
    /// <summary>JSON: список строк, каждая строка это список значений.</summary>
    public required string RowsJson { get; set; }
    public int RowCount { get; set; }
    /// <summary>Есть персональные данные.</summary>
    public bool Sensitive { get; set; }
    public DateTime LoadedAtUtc { get; set; }
}

public class PeriodReading
{
    public int Id { get; set; }
    public required string AbonentId { get; set; }
    public required string ServiceId { get; set; }
    public required string Period { get; set; }
    public decimal StartValue { get; set; }
    public decimal Value { get; set; }
    /// <summary>Сколько копеек это показание уже добавило в долг.</summary>
    public long AppliedAmount { get; set; }
}

/// <summary>Расчётный месяц. Открыт, пока нет ClosedAtUtc. ReopenedAtUtc: закрытый месяц открыт на исправление.</summary>
public class AccountingMonth
{
    public required string Period { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public string? ClosedBy { get; set; }
    public DateTime? ReopenedAtUtc { get; set; }
    public string? ReopenedBy { get; set; }
    public string? ReopenReason { get; set; }
    public DateTime? ReclosedAtUtc { get; set; }
    public string? ReclosedBy { get; set; }
    public int ReclosedCount { get; set; }
}

/// <summary>Что изменило перезакрытие в начислении: старое значение не теряется.</summary>
public class ChargeRevision
{
    public int Id { get; set; }
    public required string Period { get; set; }
    public required string AbonentId { get; set; }
    public required string ServiceId { get; set; }
    public long OldAmount { get; set; }
    public long NewAmount { get; set; }
    public decimal? OldPrice { get; set; }
    public decimal? NewPrice { get; set; }
    public required string Who { get; set; }
    public DateTime AtUtc { get; set; }
    public required string Reason { get; set; }
}

public class Payment
{
    public required string Id { get; set; }
    public required string AbonentId { get; set; }
    public DateTime AtUtc { get; set; }
    public required string Source { get; set; }
    public required string Cashier { get; set; }
    public long Amount { get; set; }
    public string Status { get; set; } = "Проведена";
    public string? IdempotencyKey { get; set; }
    /// <summary>Офис приёма платежа (справочник «Офисы приёма платежей»).</summary>
    public string? Office { get; set; }
    /// <summary>Расчётный месяц, за который принят платёж, ГГГГ-ММ.</summary>
    public string? Period { get; set; }
}

public class CashOperation
{
    public int Id { get; set; }
    public DateTime AtUtc { get; set; }
    public required string Name { get; set; }
    public long Amount { get; set; }
}

public class JournalRecord
{
    public int Id { get; set; }
    public string? AbonentId { get; set; }
    public DateTime AtUtc { get; set; }
    public required string Action { get; set; }
    public required string Who { get; set; }
    public required string Details { get; set; }
    public string? Reason { get; set; }
}

public class PeriodStepState
{
    public required string Id { get; set; }
    public int Position { get; set; }
    public required string State { get; set; }
    public required string Status { get; set; }
}

public class ExchangeBatch
{
    public required string Id { get; set; }
    public required string Partner { get; set; }
    public DateTime AtUtc { get; set; }
    public required string Title { get; set; }
    public required string FileName { get; set; }
    public required string KeyColumn { get; set; }
    public bool IsPayments { get; set; }
    public bool Posted { get; set; }
    public List<ExchangeLine> Lines { get; set; } = [];
}

public class ExchangeLine
{
    public int Id { get; set; }
    public required string BatchId { get; set; }
    public int Line { get; set; }
    public required string Key { get; set; }
    public long Amount { get; set; }
    public required string State { get; set; }
}

/// <summary>Норма теплопотребления для категории дома, Гкал на м² в месяц. Применяется к HouseHeatNorm всех абонентов этой категории (схема NormHouse).</summary>
public class HeatNormCategory
{
    public required string Category { get; set; }
    public decimal Norm { get; set; }
}

/// <summary>Реквизиты организации для документов. Одна запись.</summary>
public class OrganizationProfile
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Inn { get; set; }
    public string? Kpp { get; set; }
    public required string Address { get; set; }
    public string? BankName { get; set; }
    public string? Bik { get; set; }
    public string? CorrAccount { get; set; }
    public string? Account { get; set; }
    public string? Director { get; set; }
    public string? ChiefAccountant { get; set; }
    /// <summary>НДС в процентах, входит в сумму начисления. Нет значения: без НДС.</summary>
    public decimal? VatRate { get; set; }
    /// <summary>Отопительный сезон для сезонных схем: первый месяц (10 = октябрь).</summary>
    public int HeatSeasonStartMonth { get; set; } = 10;
    /// <summary>Отопительный сезон: последний месяц включительно (4 = апрель). Сезон переходит через год: окт–апр. Вне сезона сезонные схемы не начисляются.</summary>
    public int HeatSeasonEndMonth { get; set; } = 4;
}

public class DocumentCounter
{
    public int Year { get; set; }
    public required string Kind { get; set; }
    public int Last { get; set; }
}

/// <summary>Выпущенный документ. Data: снимок для печати. При перевыпуске прежняя запись остаётся со статусом replaced.</summary>
public class BillingDocument
{
    public int Id { get; set; }
    public required string Kind { get; set; }
    public required string Period { get; set; }
    public int Year { get; set; }
    public required string AbonentId { get; set; }
    public required string Number { get; set; }
    public int Revision { get; set; }
    public required string Status { get; set; }
    /// <summary>Начисления изменились после выпуска: документ нужно перевыпустить.</summary>
    public bool Stale { get; set; }
    public long Total { get; set; }
    public required string Data { get; set; }
    public required string Who { get; set; }
    public DateTime AtUtc { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// Одно движение денег. Записи создаёт EmirDb при сохранении, менять и удалять их запрещает база.
/// ServiceId пуст для переплаты. Остаток = сумма CreditDelta − сумма DebtDelta.
/// </summary>
public class LedgerEntry
{
    public long Id { get; set; }
    public required string AbonentId { get; set; }
    public string? ServiceId { get; set; }
    public required string Kind { get; set; }
    public string? Period { get; set; }
    public long DebtDelta { get; set; }
    public long CreditDelta { get; set; }
    public required string Who { get; set; }
    public DateTime AtUtc { get; set; }
    public string? Reference { get; set; }
}

/// <summary>Площадь и жильцы: действуют с месяца FromPeriod до следующей записи. Abonent.Area и Residents показывают рабочий месяц.</summary>
public class AbonentFact
{
    public int Id { get; set; }
    public required string AbonentId { get; set; }
    public required string FromPeriod { get; set; }
    public decimal Area { get; set; }
    public int Residents { get; set; }
    public required string Who { get; set; }
    public DateTime AtUtc { get; set; }
    public string? Reason { get; set; }
}

public class Resident
{
    public int Id { get; set; }
    public required string AbonentId { get; set; }
    public required string Name { get; set; }
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
}

/// <summary>Категория льготы. Как и в старой системе, только название: размера льготы здесь нет.</summary>
public class BenefitCategory
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Beneficiary
{
    public int Id { get; set; }
    public required string AbonentId { get; set; }
    public int CategoryId { get; set; }
    public required string FullName { get; set; }
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
    /// <summary>Идентификатор для обмена с соцзащитой.</summary>
    public string Uuid { get; set; } = Guid.NewGuid().ToString();
    public required string Who { get; set; }
    public DateTime AtUtc { get; set; }
}

public class Message
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public required string Details { get; set; }
}
