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

    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();

    public DbSet<Market> Markets => Set<Market>();

    public DbSet<Industry> Industries => Set<Industry>();

    public DbSet<NewsPost> NewsPosts => Set<NewsPost>();

    public DbSet<NewsPostIndustry> NewsPostIndustries => Set<NewsPostIndustry>();

    public DbSet<Crisis> Crises => Set<Crisis>();

    public DbSet<CrisisIndustry> CrisisIndustries => Set<CrisisIndustry>();

    public DbSet<ScienceInvestigation> ScienceInvestigations => Set<ScienceInvestigation>();

    public DbSet<ScienceInvestigationIndustry> ScienceInvestigationIndustries => Set<ScienceInvestigationIndustry>();

    public DbSet<Bankruptcy> Bankruptcies => Set<Bankruptcy>();

    public DbSet<CollectiveFund> CollectiveFunds => Set<CollectiveFund>();

    public DbSet<CollectiveFundParticipant> CollectiveFundParticipants => Set<CollectiveFundParticipant>();

    public DbSet<ParticipantWorthSnapshot> ParticipantWorthSnapshots => Set<ParticipantWorthSnapshot>();

    public DbSet<MarketExit> MarketExits => Set<MarketExit>();

    public DbSet<Auditor> Auditors => Set<Auditor>();

    public DbSet<CompanyRating> CompanyRatings => Set<CompanyRating>();

    public DbSet<ShareEmission> ShareEmissions => Set<ShareEmission>();

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

        modelBuilder.Entity<ScienceInvestigation>()
            .HasMany(investigation => investigation.Industries)
            .WithOne()
            .HasForeignKey(link => link.ScienceInvestigationId);

        modelBuilder.Entity<CollectiveFund>()
            .HasMany(fund => fund.Members)
            .WithOne()
            .HasForeignKey(member => member.CollectiveFundId);

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

        // Ratings are read back per company to find the current verdict and its history.
        modelBuilder.Entity<CompanyRating>()
            .HasIndex(rating => new { rating.CompanyId, rating.CreatedInCycleId });

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
    }
}
