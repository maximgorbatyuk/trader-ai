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

    public DbSet<MarketRun> MarketRuns => Set<MarketRun>();

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

    public DbSet<PrimaryIssuanceEvent> PrimaryIssuanceEvents => Set<PrimaryIssuanceEvent>();

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

    public DbSet<CompanyAuditEvidence> CompanyAuditEvidence => Set<CompanyAuditEvidence>();

    public DbSet<CompanyDividendEvent> CompanyDividendEvents => Set<CompanyDividendEvent>();

    public DbSet<CompanyFinancialSnapshot> CompanyFinancialSnapshots => Set<CompanyFinancialSnapshot>();

    public DbSet<PortfolioAuditSummary> PortfolioAuditSummaries => Set<PortfolioAuditSummary>();

    public DbSet<PortfolioAuditSummaryItem> PortfolioAuditSummaryItems => Set<PortfolioAuditSummaryItem>();

    public DbSet<ShareEmission> ShareEmissions => Set<ShareEmission>();

    public DbSet<ShareEmissionRecipient> ShareEmissionRecipients => Set<ShareEmissionRecipient>();

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

    public DbSet<AiPrediction> AiPredictions => Set<AiPrediction>();

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

        modelBuilder.Entity<Market>()
            .HasIndex(market => market.CurrentRunId);

        modelBuilder.Entity<MarketCycle>()
            .HasIndex(cycle => cycle.MarketRunId);

        modelBuilder.Entity<AiTraderCall>()
            .HasIndex(call => call.MarketRunId);

        modelBuilder.Entity<AiTraderCall>()
            .HasIndex(call => new { call.AttemptGroupId, call.AttemptNumber })
            .IsUnique();

        modelBuilder.Entity<CompanyInvestment>()
            .HasIndex(investment => investment.MarketRunId);

        modelBuilder.Entity<ShareEmission>()
            .HasIndex(emission => emission.MarketRunId);

        modelBuilder.Entity<ShareEmission>()
            .HasMany(emission => emission.Recipients)
            .WithOne(recipient => recipient.ShareEmission)
            .HasForeignKey(recipient => recipient.ShareEmissionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ShareEmissionRecipient>()
            .HasIndex(recipient => new { recipient.ShareEmissionId, recipient.ParticipantId })
            .IsUnique();

        modelBuilder.Entity<OrderArchive>()
            .HasIndex(order => order.MarketRunId);

        modelBuilder.Entity<PriceSnapshotArchive>()
            .HasIndex(snapshot => snapshot.MarketRunId);

        modelBuilder.Entity<MoneyTransactionArchive>()
            .HasIndex(transaction => transaction.MarketRunId);

        modelBuilder.Entity<ParticipantWorthSnapshotArchive>()
            .HasIndex(snapshot => snapshot.MarketRunId);

        modelBuilder.Entity<SectorSentimentSnapshotArchive>()
            .HasIndex(snapshot => snapshot.MarketRunId);

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

        modelBuilder.Entity<CompanyRating>()
            .HasOne<Company>()
            .WithMany()
            .HasForeignKey(rating => rating.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CompanyRating>()
            .HasOne<Auditor>()
            .WithMany()
            .HasForeignKey(rating => rating.AuditorId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CompanyAuditEvidence>()
            .HasKey(evidence => evidence.CompanyRatingId);

        modelBuilder.Entity<CompanyAuditEvidence>()
            .HasIndex(evidence => new
            {
                evidence.CompanyId,
                evidence.EffectiveTradingDayNumber,
            })
            .IsUnique();

        modelBuilder.Entity<CompanyAuditEvidence>()
            .HasOne(evidence => evidence.CompanyRating)
            .WithOne(rating => rating.Evidence)
            .HasForeignKey<CompanyAuditEvidence>(evidence => evidence.CompanyRatingId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CompanyAuditEvidence>()
            .HasOne<Company>()
            .WithMany()
            .HasForeignKey(evidence => evidence.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CompanyAuditEvidence>()
            .HasOne(evidence => evidence.LatestDividendEvent)
            .WithMany()
            .HasForeignKey(evidence => new { evidence.LatestDividendEventId, evidence.CompanyId })
            .HasPrincipalKey(dividendEvent => new { dividendEvent.Id, dividendEvent.CompanyId })
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CompanyAuditEvidence>()
            .HasOne(evidence => evidence.CompanyFinancialSnapshot)
            .WithMany()
            .HasForeignKey(evidence => new { evidence.CompanyFinancialSnapshotId, evidence.CompanyId })
            .HasPrincipalKey(snapshot => new { snapshot.Id, snapshot.CompanyId })
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CompanyDividendEvent>()
            .HasOne<Company>()
            .WithMany()
            .HasForeignKey(dividendEvent => dividendEvent.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CompanyDividendEvent>()
            .HasIndex(dividendEvent => new { dividendEvent.CompanyId, dividendEvent.TradingDayNumber, dividendEvent.Id });

        modelBuilder.Entity<CompanyDividendEvent>()
            .HasAlternateKey(dividendEvent => new { dividendEvent.Id, dividendEvent.CompanyId });

        modelBuilder.Entity<CompanyDividendEvent>()
            .ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_CompanyDividendEvents_Amounts_NonNegative",
                    "CAST(DeclaredAmount AS NUMERIC) >= 0 AND CAST(FundedAmount AS NUMERIC) >= 0 AND CAST(IssuerCashBeforeFunding AS NUMERIC) >= 0");
                table.HasCheckConstraint(
                    "CK_CompanyDividendEvents_FundedNotAboveDeclared",
                    "CAST(FundedAmount AS NUMERIC) <= CAST(DeclaredAmount AS NUMERIC)");
            });

        modelBuilder.Entity<CompanyFinancialSnapshot>()
            .HasOne<Company>()
            .WithMany()
            .HasForeignKey(snapshot => snapshot.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CompanyFinancialSnapshot>()
            .HasOne<MarketCycle>()
            .WithMany()
            .HasForeignKey(snapshot => snapshot.CreatedInCycleId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CompanyFinancialSnapshot>()
            .HasOne(snapshot => snapshot.LatestDividendEvent)
            .WithMany()
            .HasForeignKey(snapshot => new { snapshot.LatestDividendEventId, snapshot.CompanyId })
            .HasPrincipalKey(dividendEvent => new { dividendEvent.Id, dividendEvent.CompanyId })
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CompanyFinancialSnapshot>()
            .HasAlternateKey(snapshot => new { snapshot.Id, snapshot.CompanyId });

        modelBuilder.Entity<CompanyFinancialSnapshot>()
            .HasIndex(snapshot => new { snapshot.CompanyId, snapshot.TradingDayNumber, snapshot.Moment })
            .IsUnique();

        modelBuilder.Entity<CompanyFinancialSnapshot>()
            .HasIndex(snapshot => new { snapshot.CompanyId, snapshot.CreatedInCycleId });

        modelBuilder.Entity<CompanyFinancialSnapshot>()
            .ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_CompanyFinancialSnapshots_TradingDay_Positive",
                    "TradingDayNumber > 0");
                table.HasCheckConstraint(
                    "CK_CompanyFinancialSnapshots_NonNegativeValues",
                    "CAST(Revenue AS NUMERIC) >= 0"
                    + " AND CAST(TotalAssets AS NUMERIC) >= 0"
                    + " AND CAST(TotalLiabilities AS NUMERIC) >= 0"
                    + " AND CAST(TotalDebt AS NUMERIC) >= 0"
                    + " AND CAST(ExpectedDividendPerShare AS NUMERIC) >= 0"
                    + " AND CAST(ExpectedDividendPool AS NUMERIC) >= 0"
                    + " AND CAST(DividendCoverageRatio AS NUMERIC) >= 0"
                    + " AND CAST(BusinessRiskScore AS NUMERIC) >= 0"
                    + " AND CAST(ManagementRevenueForecast AS NUMERIC) >= 0");
                table.HasCheckConstraint(
                    "CK_CompanyFinancialSnapshots_DebtWithinLiabilities",
                    "CAST(TotalDebt AS NUMERIC) <= CAST(TotalLiabilities AS NUMERIC)");
                table.HasCheckConstraint(
                    "CK_CompanyFinancialSnapshots_ScoresInRange",
                    "CAST(BusinessRiskScore AS NUMERIC) <= 100"
                    + " AND CAST(ManagementConfidenceScore AS NUMERIC) >= 0"
                    + " AND CAST(ManagementConfidenceScore AS NUMERIC) <= 100"
                    + " AND CAST(ProfitabilityScore AS NUMERIC) >= 0"
                    + " AND CAST(ProfitabilityScore AS NUMERIC) <= 100"
                    + " AND CAST(StabilityScore AS NUMERIC) >= 0"
                    + " AND CAST(StabilityScore AS NUMERIC) <= 100"
                    + " AND CAST(ClosureRiskScore AS NUMERIC) >= 0"
                    + " AND CAST(ClosureRiskScore AS NUMERIC) <= 100");
                table.HasCheckConstraint(
                    "CK_CompanyFinancialSnapshots_Moment_Valid",
                    "Moment IN (0, 1, 2)");
                table.HasCheckConstraint(
                    "CK_CompanyFinancialSnapshots_ManagementOutlook_Valid",
                    "ManagementOutlook IN (0, 1, 2)");
                table.HasCheckConstraint(
                    "CK_CompanyFinancialSnapshots_Levels_Valid",
                    "ProfitabilityLevel IN (0, 1, 2)"
                    + " AND FinancialVolatilityLevel IN (0, 1, 2)"
                    + " AND ClosureRiskLevel IN (0, 1, 2)");
                table.HasCheckConstraint(
                    "CK_CompanyFinancialSnapshots_ChangedMetrics_Valid",
                    "ChangedMetrics >= 0 AND (ChangedMetrics & ~4095) = 0");
            });

        modelBuilder.Entity<PortfolioAuditSummary>()
            .HasOne(summary => summary.NewsPost)
            .WithOne(newsPost => newsPost.PortfolioAuditSummary)
            .HasForeignKey<PortfolioAuditSummary>(summary => summary.NewsPostId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PortfolioAuditSummary>()
            .HasMany(summary => summary.Items)
            .WithOne(item => item.PortfolioAuditSummary)
            .HasForeignKey(item => item.PortfolioAuditSummaryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PortfolioAuditSummaryItem>()
            .HasOne<Company>()
            .WithMany()
            .HasForeignKey(item => item.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PortfolioAuditSummaryItem>()
            .HasOne(item => item.CompanyRating)
            .WithMany()
            .HasForeignKey(item => item.CompanyRatingId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PortfolioAuditSummaryItem>()
            .HasIndex(item => new { item.PortfolioAuditSummaryId, item.CompanyId })
            .IsUnique();

        modelBuilder.Entity<PortfolioAuditSummaryItem>()
            .ToTable(table => table.HasCheckConstraint(
                "CK_PortfolioAuditSummaryItems_Quantities_NonNegative",
                "PlayerQuantity >= 0 AND ManagedFundQuantity >= 0"));

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

        modelBuilder.Entity<PrimaryIssuanceEvent>()
            .HasOne<Company>()
            .WithMany()
            .HasForeignKey(issuance => issuance.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PrimaryIssuanceEvent>()
            .HasOne<MarketCycle>()
            .WithMany()
            .HasForeignKey(issuance => issuance.CreatedInCycleId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PrimaryIssuanceEvent>()
            .HasIndex(issuance => new { issuance.CompanyId, issuance.CreatedInCycleId })
            .IsUnique();

        modelBuilder.Entity<PrimaryIssuanceEvent>()
            .ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_PrimaryIssuanceEvents_Counts_Positive",
                    "IssuedSharesBefore > 0 AND NewlyIssuedShares > 0 AND IssuedSharesAfter > 0");
                table.HasCheckConstraint(
                    "CK_PrimaryIssuanceEvents_Counts_Coherent",
                    "IssuedSharesBefore + NewlyIssuedShares = IssuedSharesAfter");
            });

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
            .HasMany(call => call.Predictions)
            .WithOne(prediction => prediction.AiTraderCall)
            .HasForeignKey(prediction => prediction.AiTraderCallId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AiPrediction>()
            .HasIndex(prediction => new { prediction.AiTraderCallId, prediction.CompanyId, prediction.HorizonCycles })
            .IsUnique();

        modelBuilder.Entity<AiPrediction>()
            .HasIndex(prediction => new { prediction.MarketRunId, prediction.SnapshotCycleNumber });

        modelBuilder.Entity<AiPrediction>()
            .Property(prediction => prediction.Reason).HasMaxLength(1000);

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

        modelBuilder.Entity<AiTraderCall>()
            .Property(call => call.FailureCategory).HasMaxLength(64);

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
        modelBuilder.Entity<AiPrediction>().Property(prediction => prediction.Confidence).HasPrecision(18, 6);
        modelBuilder.Entity<CompanyAuditEvidence>().Property(evidence => evidence.AdjustedReturnPercent).HasPrecision(18, 6);
        modelBuilder.Entity<CompanyAuditEvidence>().Property(evidence => evidence.MaximumAdjustedCycleMovePercent).HasPrecision(18, 6);
        modelBuilder.Entity<CompanyAuditEvidence>().Property(evidence => evidence.FreeShareDilutionPercent).HasPrecision(18, 6);
        modelBuilder.Entity<CompanyAuditEvidence>().Property(evidence => evidence.DividendCoverageRatio).HasPrecision(18, 6);
        modelBuilder.Entity<CompanyFinancialSnapshot>().Property(snapshot => snapshot.ExpectedDividendPerShare).HasPrecision(18, 6);
        modelBuilder.Entity<CompanyFinancialSnapshot>().Property(snapshot => snapshot.DividendCoverageRatio).HasPrecision(18, 6);
        modelBuilder.Entity<CompanyFinancialSnapshot>().Property(snapshot => snapshot.BusinessRiskScore).HasPrecision(18, 6);
        modelBuilder.Entity<CompanyFinancialSnapshot>().Property(snapshot => snapshot.ManagementConfidenceScore).HasPrecision(18, 6);
        modelBuilder.Entity<CompanyFinancialSnapshot>().Property(snapshot => snapshot.ProfitabilityScore).HasPrecision(18, 6);
        modelBuilder.Entity<CompanyFinancialSnapshot>().Property(snapshot => snapshot.StabilityScore).HasPrecision(18, 6);
        modelBuilder.Entity<CompanyFinancialSnapshot>().Property(snapshot => snapshot.ClosureRiskScore).HasPrecision(18, 6);
        modelBuilder.Entity<CompanyFinancialSnapshot>().Property(snapshot => snapshot.Moment).HasConversion<int>();
        modelBuilder.Entity<CompanyFinancialSnapshot>().Property(snapshot => snapshot.ManagementOutlook).HasConversion<int>();
        modelBuilder.Entity<CompanyFinancialSnapshot>().Property(snapshot => snapshot.ProfitabilityLevel).HasConversion<int>();
        modelBuilder.Entity<CompanyFinancialSnapshot>().Property(snapshot => snapshot.FinancialVolatilityLevel).HasConversion<int>();
        modelBuilder.Entity<CompanyFinancialSnapshot>().Property(snapshot => snapshot.ClosureRiskLevel).HasConversion<int>();
        modelBuilder.Entity<CompanyFinancialSnapshot>().Property(snapshot => snapshot.ChangedMetrics).HasConversion<int>();
        modelBuilder.Entity<PortfolioAuditSummary>().Property(summary => summary.AverageScore).HasPrecision(18, 6);

        // Behavioural-audit indices are min-max-normalised sums near the 0..5 range; the money scale above would
        // flatten the small gaps the nearest-group-average classification reads, so give them a finer scale.
        modelBuilder.Entity<Participant>().Property(participant => participant.TemperamentIndex).HasPrecision(18, 6);
        modelBuilder.Entity<Participant>().Property(participant => participant.RiskProfileIndex).HasPrecision(18, 6);
    }
}
