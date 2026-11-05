using Emir.Api.Domain;
using Microsoft.AspNetCore.Identity;

namespace Emir.Api.Data;

/// <summary>Вымышленные данные только для разработки. В боевой среде не запускается.</summary>
public static class Seed
{
    private const string Chi = "р.п. Чистоозерное";
    private const string Zhu = "с. Журавка";

    public static void Run(EmirDb db, string demoPassword)
    {
        if (db.Users.Any()) return;

        db.Operation = new MoneyOperation("opening", "Начальные данные", "2026-10");
        var hasher = new PasswordHasher<AppUser>();
        foreach (var (login, name, role) in new[]
        {
            ("kassa1", "Кассир 1", Roles.Cashier), ("oper1", "Оператор 1", Roles.Service),
            ("buh1", "Бухгалтер", Roles.Accountant), ("admin", "Администратор", Roles.Admin),
        })
        {
            var user = new AppUser { Login = login, DisplayName = name, Role = role, PasswordHash = "" };
            user.PasswordHash = hasher.HashPassword(user, demoPassword);
            db.Users.Add(user);
        }

        db.Abonents.AddRange(
            Person("a1", "Иванова Анна Петровна", "Иванова А. П.", Chi, "ул. Школьная, 12", "4", "40010001", "+7 900 000-00-11", 3, 52, 0, Heat(184210), Water(20790, "СТК-10001", 412, noReading: true), Penalty(8450)),
            Person("a2", "Петров Сергей Ильич", "Петров С. И.", Chi, "ул. Школьная, 12", "7", "40010002", null, 2, 44, 0, Heat(0)),
            Person("a3", "Сидорова Мария Олеговна", "Сидорова М. О.", Zhu, "ул. Новая, 3", "1", "40010003", "+7 900 000-00-22", 1, 39, 0, Heat(711360), Penalty(29640)),
            Org("a4", "ООО «Пример»", Chi, "ул. Центральная, 1", "40020001", "+7 900 000-00-33", "5400000000", 310, 125000, Heat(0), Water(0, "СТК-20001", 2140)),
            Person("a5", "Кузнецов Олег Витальевич", "Кузнецов О. В.", Zhu, "ул. Новая, 3", "2", "40010004", null, 4, 58, 0, Heat(49181), Penalty(2049)),
            Person("a6", "Морозова Елена Юрьевна", "Морозова Е. Ю.", Chi, "ул. Садовая, 8", null, "40010005", "+7 900 000-00-44", 2, 61, 0, Heat(8273), Water(962, "СТК-10005", 88), Penalty(385)),
            Person("a7", "Волков Дмитрий Андреевич", "Волков Д. А.", Chi, "ул. Мира, 21", "3", "40010006", "+7 900 000-00-55", 3, 47, 1500, Heat(0), Water(0, "СТК-10006", 331)),
            Person("a8", "Зайцева Нина Сергеевна", "Зайцева Н. С.", Chi, "ул. Мира, 23", "1", "40010007", "+7 900 000-00-66", 1, 33, 0, Heat(126787), Penalty(5283)),
            Org("a9", "МБОУ «Школа»", Zhu, "ул. Центральная, 39", "40020002", "+7 900 000-00-77", "5400000001", 820, 0, Heat(0), Water(0, "СТК-20002", 5120)),
            Person("a10", "Соколов Игорь Романович", "Соколов И. Р.", Chi, "ул. Лесная, 5", null, "40010008", "+7 900 000-00-88", 2, 72, 0, Heat(262825), Water(30561, "СТК-10008", 702), Penalty(12224)));

        // Цены вымышленные: настоящие тарифы нужно взять из старой системы
        db.Tariffs.AddRange(
            new Tariff { Id = "heat-norm", Name = "Теплоснабжение по площади", Scheme = "HeatHouseNorm", Unit = "м²" },
            new Tariff { Id = "water-meter", Name = "Вода по счётчику", Scheme = "MeterApartment", Unit = "м³" });
        db.TariffPrices.AddRange(
            new TariffPrice { TariffId = "heat-norm", FromPeriod = TariffHistory.Beginning, Price = 35.43m, Who = "Начальные данные", AtUtc = DateTime.UtcNow },
            new TariffPrice { TariffId = "water-meter", FromPeriod = TariffHistory.Beginning, Price = 52m, Who = "Начальные данные", AtUtc = DateTime.UtcNow });
        db.SaveChanges();
        foreach (var abonent in db.ChangeTracker.Entries<Abonent>().Select(entry => entry.Entity))
            db.AbonentFacts.Add(new AbonentFact { AbonentId = abonent.Id, FromPeriod = TariffHistory.Beginning, Area = abonent.Area, Residents = abonent.Residents, Who = "Начальные данные", AtUtc = DateTime.UtcNow });
        foreach (var line in db.ChangeTracker.Entries<ServiceLine>().Select(entry => entry.Entity).Where(line => line.ServiceId != "penalty"))
            db.ServiceTerms.Add(new ServiceTerm { AbonentId = line.AbonentId, ServiceId = line.ServiceId, FromPeriod = TariffHistory.Beginning, TariffId = line.TariffId, State = line.State, Who = "Начальные данные", AtUtc = DateTime.UtcNow });

        // Текущие долги считаем начисленными в октябре, иначе пене не от чего считать просрочку
        var opening = new[]
        {
            ("a1", "heat", 184210L), ("a1", "water", 20790L), ("a3", "heat", 711360L), ("a5", "heat", 49181L), ("a6", "heat", 8273L), ("a6", "water", 962L), ("a8", "heat", 126787L), ("a10", "heat", 262825L), ("a10", "water", 30561L),
        };
        foreach (var (abonentId, serviceId, amount) in opening)
            db.ChargeEntries.Add(new ChargeEntry { AbonentId = abonentId, ServiceId = serviceId, Period = "2026-10", Scheme = "Opening", Amount = amount, AppliedToDebt = true, AtUtc = DateTime.UtcNow });

        var now = DateTime.UtcNow;
        var ids = new[] { "a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8", "a9", "a10" };
        foreach (var id in ids)
        {
            db.Payments.Add(new Payment { Id = $"{id}-p1", AbonentId = id, AtUtc = now.AddDays(-23), Source = "Касса ФЛ", Cashier = "Кассир 1", Amount = 150000 });
            db.Payments.Add(new Payment { Id = $"{id}-p2", AbonentId = id, AtUtc = now.AddDays(-56), Source = "Банковские платежи", Cashier = "Реестр", Amount = 200000 });
        }

        // Вымышленная организация для разработки. НДС 5 % взят из настроек старой системы, порядок расчёта нужно подтвердить
        db.OrganizationProfiles.Add(new OrganizationProfile
        {
            Name = "ООО «Демо-Тепло»", Inn = "5400000099", Kpp = "540001001", Address = "р.п. Чистоозерное, ул. Центральная, 1",
            BankName = "Демо-банк", Bik = "045000000", CorrAccount = "30101810000000000000", Account = "40702810000000000000",
            Director = "Директор Д. Д.", ChiefAccountant = "Бухгалтер Б. Б.", VatRate = 5,
        });
        // Примеры для разработки. Настоящий список категорий переносится из старой системы
        db.BenefitCategories.AddRange(new BenefitCategory { Name = "Ветеран труда" }, new BenefitCategory { Name = "Многодетная семья" });
        db.AccountingMonths.Add(new AccountingMonth { Period = "2026-11" });
        db.PeriodSteps.AddRange(Periods.NewSteps());

        db.ExchangeBatches.AddRange(
            new ExchangeBatch
            {
                Id = "b1", Partner = "1С", AtUtc = now.AddHours(-2), Title = "Платежи из 1С", FileName = "platezhi_demo.csv", KeyColumn = "Лицевой счёт", IsPayments = true,
                Lines =
                [
                    new ExchangeLine { BatchId = "b1", Line = 2, Key = "40019090", Amount = 120000, State = "not-found" },
                    new ExchangeLine { BatchId = "b1", Line = 3, Key = "40010003", Amount = 150000, State = "ok" },
                    new ExchangeLine { BatchId = "b1", Line = 4, Key = "40010003", Amount = 150000, State = "duplicate" },
                    new ExchangeLine { BatchId = "b1", Line = 5, Key = "40010007", Amount = 64000, State = "ok" },
                ],
            },
            new ExchangeBatch
            {
                Id = "b2", Partner = "Сбербанк", AtUtc = now.AddDays(-1), Title = "Реестр оплат", FileName = "sber_demo.csv", KeyColumn = "Лицевой счёт", IsPayments = true, Posted = true,
                Lines = [new ExchangeLine { BatchId = "b2", Line = 2, Key = "40010008", Amount = 90000, State = "ok" }],
            });

        db.Messages.AddRange(
            new Message { Title = "Добро пожаловать", Details = "Система подготовлена, данные тестовые" });

        db.Journal.Add(new JournalRecord { AbonentId = "a1", AtUtc = now.AddDays(-2), Action = "Состояние услуги", Who = "Оператор 1", Details = "Приостановлен, теперь Работает" });
        db.SaveChanges();
    }

    private static Abonent Person(string id, string name, string shortName, string place, string street, string? flat, string account, string? phone, int residents, decimal area, long credit, params ServiceTemplate[] services) =>
        Build(new Abonent { Id = id, Kind = "person", Name = name, ShortName = shortName, Place = place, Street = street, Flat = flat, Account = account, Phone = phone, Residents = residents, Area = area, Credit = credit }, services);

    private static Abonent Org(string id, string name, string place, string street, string account, string phone, string inn, decimal area, long credit, params ServiceTemplate[] services) =>
        Build(new Abonent { Id = id, Kind = "org", Name = name, ShortName = name, Place = place, Street = street, Account = account, Phone = phone, Inn = inn, Area = area, Credit = credit }, services);

    private static Abonent Build(Abonent abonent, ServiceTemplate[] services)
    {
        for (var index = 0; index < services.Length; index++)
        {
            var service = services[index];
            abonent.Services.Add(new ServiceLine
            {
                AbonentId = abonent.Id, ServiceId = service.Id, Position = index, Name = service.Name, TariffName = service.Tariff, ChargeStatus = service.Status, Debt = service.Debt,
                TariffId = service.Id switch { "heat" => "heat-norm", "water" => "water-meter", _ => null },
                MeterNumber = service.Meter, MeterLastReading = service.Last, MeterVerifiedUntil = service.Meter is null ? null : "05.2029",
            });
        }
        return abonent;
    }

    private record ServiceTemplate(string Id, string Name, string Tariff, string Status, long Debt, string? Meter = null, decimal? Last = null);

    private static ServiceTemplate Heat(long debt) => new("heat", "Теплоснабжение", "По нормативу", "Ждёт начисления", debt);
    private static ServiceTemplate Water(long debt, string meter, decimal last, bool noReading = false) => new("water", "Холодная вода", "По счётчику", "Нет показаний за ноябрь", debt, meter, last);
    private static ServiceTemplate Penalty(long debt) => new("penalty", "Пени", "По настройке пени", "Ждёт начисления", debt);
}
