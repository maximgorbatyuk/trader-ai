using Microsoft.EntityFrameworkCore;
using TraderAi.Models;

namespace TraderAi.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();

    public DbSet<Participant> Participants => Set<Participant>();

    public DbSet<Share> Shares => Set<Share>();

    public DbSet<MarketCycle> MarketCycles => Set<MarketCycle>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderShare> OrderShares => Set<OrderShare>();

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

        // A share can be offered by at most one open sell order; consumed links are deleted on fill.
        modelBuilder.Entity<OrderShare>()
            .HasIndex(orderShare => orderShare.ShareId)
            .IsUnique();

        modelBuilder.Entity<OrderShare>()
            .HasOne(orderShare => orderShare.Order)
            .WithMany(order => order.OrderShares)
            .HasForeignKey(orderShare => orderShare.OrderId);

        modelBuilder.Entity<OrderShare>()
            .HasOne(orderShare => orderShare.Share)
            .WithMany()
            .HasForeignKey(orderShare => orderShare.ShareId);

        modelBuilder.Entity<MarketCycle>()
            .HasIndex(cycle => cycle.CycleNumber)
            .IsUnique();

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
