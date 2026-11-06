using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Emir.Api.Data;

/// <summary>Kind: opening, reading, charge, penalty, payment, refund, correction, exchange, reclose, cancel.</summary>
public record MoneyOperation(string Kind, string Who, string? Period, string? Reference = null);

public class EmirDb(DbContextOptions<EmirDb> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Abonent> Abonents => Set<Abonent>();
    public DbSet<ServiceLine> ServiceLines => Set<ServiceLine>();
    public DbSet<PeriodReading> PeriodReadings => Set<PeriodReading>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CashOperation> CashOperations => Set<CashOperation>();
    public DbSet<JournalRecord> Journal => Set<JournalRecord>();
    public DbSet<PeriodStepState> PeriodSteps => Set<PeriodStepState>();
    public DbSet<ExchangeBatch> ExchangeBatches => Set<ExchangeBatch>();
    public DbSet<ExchangeLine> ExchangeLines => Set<ExchangeLine>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Tariff> Tariffs => Set<Tariff>();
    public DbSet<BenefitCategory> BenefitCategories => Set<BenefitCategory>();
    public DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();
    public DbSet<AbonentFact> AbonentFacts => Set<AbonentFact>();
    public DbSet<Resident> Residents => Set<Resident>();
    public DbSet<HouseMeter> HouseMeters => Set<HouseMeter>();
    public DbSet<ReferenceTable> ReferenceTables => Set<ReferenceTable>();
    public DbSet<HouseMeterReading> HouseMeterReadings => Set<HouseMeterReading>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<OrganizationProfile> OrganizationProfiles => Set<OrganizationProfile>();
    public DbSet<DocumentCounter> DocumentCounters => Set<DocumentCounter>();
    public DbSet<BillingDocument> BillingDocuments => Set<BillingDocument>();
    public DbSet<AccountingMonth> AccountingMonths => Set<AccountingMonth>();
    public DbSet<ChargeRevision> ChargeRevisions => Set<ChargeRevision>();
    public DbSet<TariffPrice> TariffPrices => Set<TariffPrice>();
    public DbSet<ServiceTerm> ServiceTerms => Set<ServiceTerm>();
    public DbSet<ChargeEntry> ChargeEntries => Set<ChargeEntry>();
    public DbSet<HeatNormCategory> HeatNormCategories => Set<HeatNormCategory>();

    /// <summary>Какая операция сейчас меняет деньги. Без неё сохранить изменение долга или переплаты нельзя.</summary>
    public MoneyOperation? Operation { get; set; }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RecordLedger();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RecordLedger();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private static long Delta<T>(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<T> entry, System.Linq.Expressions.Expression<Func<T, long>> property) where T : class => entry.State switch
    {
        EntityState.Added => entry.Property(property).CurrentValue,
        EntityState.Deleted => -entry.Property(property).OriginalValue,
        EntityState.Modified => entry.Property(property).CurrentValue - entry.Property(property).OriginalValue,
        _ => 0,
    };

    /// <summary>Журнал пишется здесь, а не в операциях: так ни одно изменение денег нельзя забыть.</summary>
    private void RecordLedger()
    {
        ChangeTracker.DetectChanges();
        var rows = new List<LedgerEntry>();
        foreach (var entry in ChangeTracker.Entries<ServiceLine>())
        {
            var delta = Delta(entry, line => line.Debt);
            if (delta != 0) rows.Add(Row(entry.Entity.AbonentId, entry.Entity.ServiceId, delta, 0));
        }
        foreach (var entry in ChangeTracker.Entries<Abonent>())
        {
            var delta = Delta(entry, abonent => abonent.Credit);
            if (delta != 0) rows.Add(Row(entry.Entity.Id, null, 0, delta));
        }
        LedgerEntries.AddRange(rows);
    }

    private LedgerEntry Row(string abonentId, string? serviceId, long debtDelta, long creditDelta)
    {
        var operation = Operation ?? throw new InvalidOperationException("Деньги меняются без указания операции");
        return new LedgerEntry { AbonentId = abonentId, ServiceId = serviceId, Kind = operation.Kind, Period = operation.Period, DebtDelta = debtDelta, CreditDelta = creditDelta, Who = operation.Who, AtUtc = DateTime.UtcNow, Reference = operation.Reference };
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        // Точность фиксирована: тарифы и нормативы хранят до четырёх знаков, показания до трёх
        configuration.Properties<decimal>().HavePrecision(18, 4);
    }

    /// <summary>Денежные операции идут в Serializable: одновременные изменения не теряются, а завершаются ошибкой 40001.</summary>
    public Task<IDbContextTransaction> BeginMoneyTransaction() => Database.BeginTransactionAsync(IsolationLevel.Serializable);

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Abonent>().Property(item => item.HouseHeatNorm).HasPrecision(18, 6);
        model.Entity<HeatNormCategory>().HasKey(item => item.Category);
        model.Entity<HeatNormCategory>().Property(item => item.Norm).HasPrecision(18, 6);
        model.Entity<ReferenceTable>().HasKey(item => item.Name);
        model.Entity<AppUser>().HasIndex(user => user.Login).IsUnique();
        model.Entity<HouseMeterReading>().HasIndex(item => new { item.HouseMeterId, item.Period }).IsUnique();
        model.Entity<HouseMeter>().Property(item => item.UnitFactor).HasPrecision(18, 8);
        model.Entity<Abonent>().HasIndex(abonent => abonent.Account).IsUnique();
        model.Entity<Abonent>().Ignore(abonent => abonent.Balance);
        model.Entity<ServiceLine>().HasKey(line => new { line.AbonentId, line.ServiceId });
        model.Entity<Abonent>().HasMany(abonent => abonent.Services).WithOne().HasForeignKey(line => line.AbonentId);
        model.Entity<PeriodReading>().HasIndex(reading => new { reading.AbonentId, reading.ServiceId, reading.Period }).IsUnique();
        model.Entity<Payment>().HasIndex(payment => payment.IdempotencyKey).IsUnique();
        model.Entity<ChargeEntry>().HasIndex(entry => new { entry.AbonentId, entry.ServiceId, entry.Period }).IsUnique();
        model.Entity<BenefitCategory>().HasIndex(category => category.Name).IsUnique();
        model.Entity<Beneficiary>().HasIndex(item => item.AbonentId);
        model.Entity<Beneficiary>().HasIndex(item => item.Uuid).IsUnique();
        model.Entity<Beneficiary>().HasOne<Abonent>().WithMany().HasForeignKey(item => item.AbonentId);
        model.Entity<Beneficiary>().HasOne<BenefitCategory>().WithMany().HasForeignKey(item => item.CategoryId);
        model.Entity<AbonentFact>().HasIndex(fact => new { fact.AbonentId, fact.FromPeriod }).IsUnique();
        model.Entity<AbonentFact>().HasOne<Abonent>().WithMany().HasForeignKey(fact => fact.AbonentId);
        model.Entity<Resident>().HasIndex(resident => resident.AbonentId);
        model.Entity<Resident>().HasOne<Abonent>().WithMany().HasForeignKey(resident => resident.AbonentId);
        model.Entity<LedgerEntry>().HasIndex(entry => new { entry.AbonentId, entry.Id });
        model.Entity<LedgerEntry>().HasIndex(entry => entry.AtUtc);
        model.Entity<DocumentCounter>().HasKey(counter => new { counter.Year, counter.Kind });
        model.Entity<BillingDocument>().Property(document => document.Data).HasColumnType("jsonb");
        model.Entity<BillingDocument>().HasOne<Abonent>().WithMany().HasForeignKey(document => document.AbonentId);
        // На абонента, месяц и вид только один действующий документ, и номер не повторяется
        model.Entity<BillingDocument>().HasIndex(document => new { document.AbonentId, document.Period, document.Kind }).IsUnique().HasFilter("\"Status\" = 'active'");
        model.Entity<BillingDocument>().HasIndex(document => new { document.Kind, document.Year, document.Number, document.Revision }).IsUnique();
        model.Entity<AccountingMonth>().HasKey(month => month.Period);
        // Исправлять можно только один месяц за раз
        model.Entity<AccountingMonth>().HasIndex(month => month.ReopenedAtUtc).IsUnique().HasFilter("\"ReopenedAtUtc\" IS NOT NULL");
        model.Entity<ChargeRevision>().HasIndex(revision => new { revision.Period, revision.AbonentId });
        model.Entity<TariffPrice>().HasIndex(price => new { price.TariffId, price.FromPeriod }).IsUnique();
        model.Entity<TariffPrice>().HasOne<Tariff>().WithMany().HasForeignKey(price => price.TariffId);
        model.Entity<ServiceTerm>().HasIndex(term => new { term.AbonentId, term.ServiceId, term.FromPeriod }).IsUnique();
        model.Entity<ServiceTerm>().HasOne<ServiceLine>().WithMany().HasForeignKey(term => new { term.AbonentId, term.ServiceId });
        model.Entity<ServiceTerm>().HasOne<Tariff>().WithMany().HasForeignKey(term => term.TariffId);
        model.Entity<ExchangeBatch>().HasMany(batch => batch.Lines).WithOne().HasForeignKey(line => line.BatchId);
    }
}
