using Microsoft.EntityFrameworkCore;
using TraderAi.Models;

namespace TraderAi.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<GameSetting> GameSettings => Set<GameSetting>();

    public DbSet<Company> Companies => Set<Company>();

    public DbSet<Participant> Participants => Set<Participant>();

    public DbSet<Holding> Holdings => Set<Holding>();

    public DbSet<MarketCycle> MarketCycles => Set<MarketCycle>();

    public DbSet<TradingDay> TradingDays => Set<TradingDay>();

    public DbSet<TradingBreakCycle> TradingBreakCycles => Set<TradingBreakCycle>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderFill> OrderFills => Set<OrderFill>();

    public DbSet<ShareTransaction> ShareTransactions => Set<ShareTransaction>();

    public DbSet<SettlementInstruction> SettlementInstructions => Set<SettlementInstruction>();

    public DbSet<MoneyTransaction> MoneyTransactions => Set<MoneyTransaction>();

    public DbSet<DividendPayout> DividendPayouts => Set<DividendPayout>();

    public DbSet<CorporateCashTransaction> CorporateCashTransactions => Set<CorporateCashTransaction>();

    public DbSet<PriceBandState> PriceBandStates => Set<PriceBandState>();

    public DbSet<StockDenominationEvent> StockDenominationEvents => Set<StockDenominationEvent>();

    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();

    public DbSet<Market> Markets => Set<Market>();

    public DbSet<Industry> Industries => Set<Industry>();

    public DbSet<NewsPost> NewsPosts => Set<NewsPost>();

    public DbSet<NewsPostIndustry> NewsPostIndustries => Set<NewsPostIndustry>();

    public DbSet<Crisis> Crises => Set<Crisis>();

    public DbSet<CrisisIndustry> CrisisIndustries => Set<CrisisIndustry>();

    public DbSet<CrisisEvent> CrisisEvents => Set<CrisisEvent>();

    public DbSet<ScienceInvestigation> ScienceInvestigations => Set<ScienceInvestigation>();

    public DbSet<ScienceInvestigationIndustry> ScienceInvestigationIndustries => Set<ScienceInvestigationIndustry>();

    public DbSet<Bankruptcy> Bankruptcies => Set<Bankruptcy>();

    public DbSet<CollectiveFund> CollectiveFunds => Set<CollectiveFund>();

    public DbSet<CollectiveFundParticipant> CollectiveFundParticipants => Set<CollectiveFundParticipant>();

    public DbSet<CollectiveFundMembershipEvent> CollectiveFundMembershipEvents => Set<CollectiveFundMembershipEvent>();

    public DbSet<ParticipantWorthSnapshot> ParticipantWorthSnapshots => Set<ParticipantWorthSnapshot>();

    public DbSet<MarketExit> MarketExits => Set<MarketExit>();

    public DbSet<Auditor> Auditors => Set<Auditor>();

    public DbSet<CompanyRating> CompanyRatings => Set<CompanyRating>();

    public DbSet<ShareEmission> ShareEmissions => Set<ShareEmission>();

    public DbSet<CompanyInvestment> CompanyInvestments => Set<CompanyInvestment>();

    public DbSet<PriceSnapshotArchive> PriceSnapshotArchives => Set<PriceSnapshotArchive>();

    public DbSet<MoneyTransactionArchive> MoneyTransactionArchives => Set<MoneyTransactionArchive>();

    public DbSet<OrderArchive> OrderArchives => Set<OrderArchive>();

    public DbSet<ParticipantWorthSnapshotArchive> ParticipantWorthSnapshotArchives => Set<ParticipantWorthSnapshotArchive>();

    public DbSet<ParticipantDailyWorthSnapshot> ParticipantDailyWorthSnapshots => Set<ParticipantDailyWorthSnapshot>();

    public DbSet<SectorSentimentSnapshot> SectorSentimentSnapshots => Set<SectorSentimentSnapshot>();

    public DbSet<SectorSentimentSnapshotArchive> SectorSentimentSnapshotArchives => Set<SectorSentimentSnapshotArchive>();

    public DbSet<Bank> Banks => Set<Bank>();

    public DbSet<Loan> Loans => Set<Loan>();

    public DbSet<MarginAccount> MarginAccounts => Set<MarginAccount>();

    public DbSet<MarginCall> MarginCalls => Set<MarginCall>();

    public DbSet<AiTraderConfiguration> AiTraderConfigurations => Set<AiTraderConfiguration>();

    public DbSet<AiTraderCall> AiTraderCalls => Set<AiTraderCall>();

    // Per-context read cache for maps that many per-cycle services request repeatedly. The whole tick runs on
    // one scoped context, so this collapses those repeated reads to a single query per map; each map is rebuilt
    // only after a save touches the table it derives from, keeping it consistent with committed state.
    private long priceSnapshotGeneration;
    private long marketCycleGeneration;
    private Dictionary<int, decimal>? latestPriceByCompany;
    private long latestPriceGeneration = -1;
    private IReadOnlyDictionary<int, int>? cycleNumbersById;
    private long cycleNumbersGeneration = -1;

    // Latest price per company by its highest snapshot id, so the whole history is never materialised. A fresh
    // copy is returned because callers such as the decision pass prune companies from their own view.
    public async Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync()
    {
        if (latestPriceByCompany is null || latestPriceGeneration != priceSnapshotGeneration)
        {
            var latestSnapshotIds = await PriceSnapshots
                .GroupBy(snapshot => snapshot.CompanyId)
                .Select(group => group.Max(snapshot => snapshot.Id))
                .ToListAsync();
            latestPriceByCompany = (await PriceSnapshots
                    .AsNoTracking()
                    .Where(snapshot => latestSnapshotIds.Contains(snapshot.Id))
                    .Select(snapshot => new { snapshot.CompanyId, snapshot.Price })
                    .ToListAsync())
                .ToDictionary(row => row.CompanyId, row => row.Price);
            latestPriceGeneration = priceSnapshotGeneration;
        }

        return new Dictionary<int, decimal>(latestPriceByCompany);
    }

    // Cycle number by id is immutable history within a tick, so callers share one read-only map.
    public async Task<IReadOnlyDictionary<int, int>> CycleNumbersByIdAsync()
    {
        if (cycleNumbersById is null || cycleNumbersGeneration != marketCycleGeneration)
        {
            cycleNumbersById = await MarketCycles.ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);
            cycleNumbersGeneration = marketCycleGeneration;
        }

        return cycleNumbersById;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var pricesTouched = HasPendingChanges<PriceSnapshot>();
        var cyclesTouched = HasPendingChanges<MarketCycle>();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        InvalidateReadCache(pricesTouched, cyclesTouched);
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var pricesTouched = HasPendingChanges<PriceSnapshot>();
        var cyclesTouched = HasPendingChanges<MarketCycle>();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        InvalidateReadCache(pricesTouched, cyclesTouched);
        return result;
    }

    private bool HasPendingChanges<TEntity>() where TEntity : class =>
        ChangeTracker.Entries<TEntity>().Any(entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

    private void InvalidateReadCache(bool pricesTouched, bool cyclesTouched)
    {
        if (pricesTouched)
        {
            priceSnapshotGeneration++;
        }

        if (cyclesTouched)
        {
            marketCycleGeneration++;
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameSetting>()
            .HasKey(setting => setting.Key);

        modelBuilder.Entity<GameSetting>()
            .Property(setting => setting.Key)
            .HasMaxLength(256);

        modelBuilder.Entity<NewsPost>()
            .HasMany(newsPost => newsPost.Industries)
            .WithOne()
            .HasForeignKey(link => link.NewsPostId);

        modelBuilder.Entity<Crisis>()
            .HasMany(crisis => crisis.Industries)
            .WithOne()
            .HasForeignKey(link => link.CrisisId);

        modelBuilder.Entity<Crisis>()
            .HasMany(crisis => crisis.Events)
            .WithOne()
            .HasForeignKey(crisisEvent => crisisEvent.CrisisId);

        modelBuilder.Entity<ScienceInvestigation>()
            .HasMany(investigation => investigation.Industries)
            .WithOne()
            .HasForeignKey(link => link.ScienceInvestigationId);

        modelBuilder.Entity<CollectiveFund>()
            .HasMany(fund => fund.Members)
            .WithOne()
            .HasForeignKey(member => member.CollectiveFundId);

        // Membership history is read newest-first from two sides: a member's page seeks on ParticipantId, a
        // fund's page on FundParticipantId; each index covers its own paged lookup.
        modelBuilder.Entity<CollectiveFundMembershipEvent>()
            .HasIndex(membershipEvent => new { membershipEvent.ParticipantId, membershipEvent.Id });

        modelBuilder.Entity<CollectiveFundMembershipEvent>()
            .HasIndex(membershipEvent => new { membershipEvent.FundParticipantId, membershipEvent.Id });

        // One position row per (participant, company); every holdings read groups on these columns.
        modelBuilder.Entity<Holding>()
            .HasIndex(holding => new { holding.ParticipantId, holding.CompanyId })
            .IsUnique();

        modelBuilder.Entity<Holding>()
            .HasIndex(holding => holding.CompanyId);

        modelBuilder.Entity<MarketCycle>()
            .HasIndex(cycle => cycle.CycleNumber)
            .IsUnique();

        modelBuilder.Entity<TradingDay>()
            .HasIndex(day => day.DayNumber)
            .IsUnique();

        modelBuilder.Entity<MarketCycle>()
            .HasIndex(cycle => new { cycle.TradingDayId, cycle.TradingCycleNumber })
            .IsUnique()
            .HasFilter("TradingDayId > 0");

        modelBuilder.Entity<TradingBreakCycle>()
            .HasIndex(cycle => new { cycle.TradingDayId, cycle.IsActive });

        // Worth snapshots are read back per trader in cycle order to chart total worth over time.
        modelBuilder.Entity<ParticipantWorthSnapshot>()
            .HasIndex(snapshot => new { snapshot.ParticipantId, snapshot.CreatedInCycleId });

        modelBuilder.Entity<Industry>().Property(industry => industry.SentimentValue).HasDefaultValue(0);
        modelBuilder.Entity<Industry>().Property(industry => industry.SentimentVolatility).HasDefaultValue(0m);
        modelBuilder.Entity<Industry>().Property(industry => industry.SectorBeta).HasDefaultValue(1m);

        modelBuilder.Entity<SectorSentimentSnapshot>()
            .HasOne<Industry>()
            .WithMany()
            .HasForeignKey(snapshot => snapshot.IndustryId);

        modelBuilder.Entity<SectorSentimentSnapshot>()
            .HasOne<MarketCycle>()
            .WithMany()
            .HasForeignKey(snapshot => snapshot.CreatedInCycleId);

        modelBuilder.Entity<SectorSentimentSnapshot>()
            .HasIndex(snapshot => snapshot.CreatedInCycleId);

        modelBuilder.Entity<SectorSentimentSnapshotArchive>()
            .HasIndex(snapshot => snapshot.CreatedInCycleId);

        // Ratings are read back per company to find the current verdict and its history.
        modelBuilder.Entity<CompanyRating>()
            .HasIndex(rating => new { rating.CompanyId, rating.CreatedInCycleId });

        // The order book is read either as "all open orders" or per (participant, company) when validating a
        // new order, so both a status-only index and the composite covering that lookup are needed.
        modelBuilder.Entity<Order>()
            .HasIndex(order => order.Status);

        modelBuilder.Entity<Order>()
            .HasIndex(order => new { order.ParticipantId, order.CompanyId, order.Type, order.Status });

        // Archiving selects terminal orders by the cycle they were created in, so the source table indexes it.
        modelBuilder.Entity<Order>()
            .HasIndex(order => order.CreatedInCycleId);

        // The exit path sums a departing participant's lifetime orders across live and archived rows.
        modelBuilder.Entity<OrderArchive>()
            .HasIndex(order => order.ParticipantId);

        // Latest-price-per-company and per-company price history both seek on (company, id).
        modelBuilder.Entity<PriceSnapshot>()
            .HasIndex(snapshot => new { snapshot.CompanyId, snapshot.Id });

        // Archiving selects rows by the cycle they were created in, so each archived-source table indexes it.
        modelBuilder.Entity<PriceSnapshot>()
            .HasIndex(snapshot => snapshot.CreatedInCycleId);

        modelBuilder.Entity<MoneyTransaction>()
            .HasIndex(transaction => transaction.CreatedInCycleId);

        modelBuilder.Entity<ParticipantWorthSnapshot>()
            .HasIndex(snapshot => snapshot.CreatedInCycleId);

        // One daily worth row per participant per closed trading day; read back in day order for the
        // long-horizon total-worth chart, and unique so a day close cannot record a participant twice.
        modelBuilder.Entity<ParticipantDailyWorthSnapshot>()
            .HasIndex(snapshot => new { snapshot.ParticipantId, snapshot.TradingDayId })
            .IsUnique();

        modelBuilder.Entity<Bank>()
            .HasMany<Loan>()
            .WithOne(loan => loan.Bank)
            .HasForeignKey(loan => loan.BankId);

        // Loans are read per borrower (its panel/detail) and per bank (the loans roster), each split by status.
        modelBuilder.Entity<Loan>()
            .HasIndex(loan => new { loan.ParticipantId, loan.Status });

        modelBuilder.Entity<Loan>()
            .HasIndex(loan => new { loan.BankId, loan.Status });

        modelBuilder.Entity<MarginAccount>()
            .HasIndex(account => account.ParticipantId)
            .IsUnique();

        modelBuilder.Entity<MarginCall>()
            .HasIndex(call => new { call.MarginAccountId, call.Status });

        // Loan-distress sells and loan-linked transactions are looked up by the loan they belong to.
        modelBuilder.Entity<Order>()
            .HasIndex(order => new { order.RelatedLoanId, order.Status });

        modelBuilder.Entity<Order>()
            .HasIndex(order => new { order.RelatedMarginCallId, order.Status });

        modelBuilder.Entity<MoneyTransaction>()
            .HasIndex(transaction => transaction.RelatedLoanId);

        // A Dividend transaction's per-company breakdown: read by parent for the detail view, deleted by cycle
        // when the parent transaction is archived.
        modelBuilder.Entity<DividendPayout>()
            .HasOne(payout => payout.MoneyTransaction)
            .WithMany()
            .HasForeignKey(payout => payout.MoneyTransactionId);

        modelBuilder.Entity<DividendPayout>()
            .HasIndex(payout => payout.MoneyTransactionId);

        modelBuilder.Entity<DividendPayout>()
            .HasIndex(payout => payout.CreatedInCycleId);

        modelBuilder.Entity<CorporateCashTransaction>()
            .HasOne<Company>()
            .WithMany()
            .HasForeignKey(transaction => transaction.CompanyId);

        modelBuilder.Entity<CorporateCashTransaction>()
            .HasIndex(transaction => new { transaction.CompanyId, transaction.Id });

        modelBuilder.Entity<CorporateCashTransaction>()
            .ToTable(table => table.HasCheckConstraint(
                "CK_CorporateCashTransactions_Amount_Positive",
                "CAST(Amount AS NUMERIC) > 0"));

        modelBuilder.Entity<PriceBandState>()
            .HasKey(state => state.CompanyId);

        modelBuilder.Entity<Company>()
            .HasOne(company => company.PriceBandState)
            .WithOne(state => state.Company)
            .HasForeignKey<PriceBandState>(state => state.CompanyId);

        modelBuilder.Entity<StockDenominationEvent>()
            .HasOne<Company>()
            .WithMany()
            .HasForeignKey(denominationEvent => denominationEvent.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<StockDenominationEvent>()
            .HasOne<MarketCycle>()
            .WithMany()
            .HasForeignKey(denominationEvent => denominationEvent.EffectiveInCycleId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<StockDenominationEvent>()
            .HasIndex(denominationEvent => new { denominationEvent.CompanyId, denominationEvent.EffectiveInCycleNumber })
            .IsUnique();

        modelBuilder.Entity<SettlementInstruction>()
            .HasOne(instruction => instruction.ShareTransaction)
            .WithOne(transaction => transaction.SettlementInstruction)
            .HasForeignKey<SettlementInstruction>(instruction => instruction.ShareTransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SettlementInstruction>()
            .HasIndex(instruction => instruction.ShareTransactionId)
            .IsUnique();

        modelBuilder.Entity<SettlementInstruction>()
            .HasIndex(instruction => new { instruction.Status, instruction.DueDayNumber });

        modelBuilder.Entity<SettlementInstruction>()
            .HasIndex(instruction => new { instruction.BuyerId, instruction.Status, instruction.DueDayNumber });

        modelBuilder.Entity<SettlementInstruction>()
            .HasIndex(instruction => new { instruction.SellerId, instruction.Status, instruction.DueDayNumber });

        // One AI configuration per participant: ParticipantId is the primary key and the foreign key, so it
        // cascades away with the participant. The audit log keeps no relationship, so its history outlives a
        // participant departure and is only cleared by a full market reset.
        modelBuilder.Entity<AiTraderConfiguration>()
            .HasKey(configuration => configuration.ParticipantId);

        modelBuilder.Entity<AiTraderConfiguration>()
            .HasOne<Participant>()
            .WithOne()
            .HasForeignKey<AiTraderConfiguration>(configuration => configuration.ParticipantId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AiTraderConfiguration>()
            .Property(configuration => configuration.ProviderId).HasMaxLength(64);

        modelBuilder.Entity<AiTraderConfiguration>()
            .Property(configuration => configuration.Model).HasMaxLength(128);

        // Call history is read newest-first per participant, so the seek covers (participant, id).
        modelBuilder.Entity<AiTraderCall>()
            .HasIndex(call => new { call.ParticipantId, call.Id });

        modelBuilder.Entity<AiTraderCall>()
            .Property(call => call.ProviderId).HasMaxLength(64);

        modelBuilder.Entity<AiTraderCall>()
            .Property(call => call.ProviderLabel).HasMaxLength(128);

        modelBuilder.Entity<AiTraderCall>()
            .Property(call => call.Model).HasMaxLength(128);

        modelBuilder.Entity<AiTraderCall>()
            .Property(call => call.PromptHash).HasMaxLength(64);

        modelBuilder.Entity<AiTraderCall>()
            .Property(call => call.Error).HasMaxLength(2000);

        modelBuilder.Entity<AiTraderCall>()
            .Property(call => call.Summary).HasMaxLength(1000);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var propertyType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                var propertyBuilder = modelBuilder.Entity(entityType.ClrType).Property(property.Name);

                if (propertyType.IsEnum)
                {
                    propertyBuilder.HasConversion<string>();
                }

                if (propertyType == typeof(decimal))
                {
                    propertyBuilder.HasPrecision(18, 2);
                }
            }
        }

        // The interest rate is a fraction, so a finer scale after the blanket money pass keeps it from rounding.
        modelBuilder.Entity<Bank>().Property(bank => bank.InterestRate).HasPrecision(18, 6);
        modelBuilder.Entity<Loan>().Property(loan => loan.InterestRate).HasPrecision(18, 6);

        // Behavioural-audit indices are min-max-normalised sums near the 0..5 range; the money scale above would
        // flatten the small gaps the nearest-group-average classification reads, so give them a finer scale.
        modelBuilder.Entity<Participant>().Property(participant => participant.TemperamentIndex).HasPrecision(18, 6);
        modelBuilder.Entity<Participant>().Property(participant => participant.RiskProfileIndex).HasPrecision(18, 6);
    }
}
