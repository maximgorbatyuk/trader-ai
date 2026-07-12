using Microsoft.EntityFrameworkCore;
using TraderAi.Models;

namespace TraderAi.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();

    public DbSet<Participant> Participants => Set<Participant>();

    public DbSet<Holding> Holdings => Set<Holding>();

    public DbSet<MarketCycle> MarketCycles => Set<MarketCycle>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderFill> OrderFills => Set<OrderFill>();

    public DbSet<ShareTransaction> ShareTransactions => Set<ShareTransaction>();

    public DbSet<MoneyTransaction> MoneyTransactions => Set<MoneyTransaction>();

    public DbSet<DividendPayout> DividendPayouts => Set<DividendPayout>();

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

    public DbSet<PriceSnapshotArchive> PriceSnapshotArchives => Set<PriceSnapshotArchive>();

    public DbSet<MoneyTransactionArchive> MoneyTransactionArchives => Set<MoneyTransactionArchive>();

    public DbSet<ParticipantWorthSnapshotArchive> ParticipantWorthSnapshotArchives => Set<ParticipantWorthSnapshotArchive>();

    public DbSet<SectorSentimentSnapshot> SectorSentimentSnapshots => Set<SectorSentimentSnapshot>();

    public DbSet<SectorSentimentSnapshotArchive> SectorSentimentSnapshotArchives => Set<SectorSentimentSnapshotArchive>();

    public DbSet<Bank> Banks => Set<Bank>();

    public DbSet<Loan> Loans => Set<Loan>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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

        modelBuilder.Entity<Bank>()
            .HasMany<Loan>()
            .WithOne(loan => loan.Bank)
            .HasForeignKey(loan => loan.BankId);

        // Loans are read per borrower (its panel/detail) and per bank (the loans roster), each split by status.
        modelBuilder.Entity<Loan>()
            .HasIndex(loan => new { loan.ParticipantId, loan.Status });

        modelBuilder.Entity<Loan>()
            .HasIndex(loan => new { loan.BankId, loan.Status });

        // Loan-distress sells and loan-linked transactions are looked up by the loan they belong to.
        modelBuilder.Entity<Order>()
            .HasIndex(order => new { order.RelatedLoanId, order.Status });

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

        // The per-cycle interest rate (e.g. 0.001) would round to 0.00 at the money scale above, so give the
        // rate columns a finer scale after the blanket pass.
        modelBuilder.Entity<Bank>().Property(bank => bank.InterestRatePerCycle).HasPrecision(18, 6);
        modelBuilder.Entity<Loan>().Property(loan => loan.InterestRatePerCycle).HasPrecision(18, 6);

        // Behavioural-audit indices are min-max-normalised sums near the 0..5 range; the money scale above would
        // flatten the small gaps the nearest-group-average classification reads, so give them a finer scale.
        modelBuilder.Entity<Participant>().Property(participant => participant.TemperamentIndex).HasPrecision(18, 6);
        modelBuilder.Entity<Participant>().Property(participant => participant.RiskProfileIndex).HasPrecision(18, 6);
    }
}
