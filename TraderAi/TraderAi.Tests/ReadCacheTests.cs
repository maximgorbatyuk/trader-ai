using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Tests;

public sealed class ReadCacheTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public ReadCacheTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task LatestPriceRebuildsAfterASnapshotSaveAndReturnsIndependentCopies()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();

        var first = await context.LatestPriceByCompanyAsync();
        Assert.Equal(100m, first[company.Id]);

        // Mutating the returned map must not leak into the cached copy the decision pass shares.
        first[company.Id] = 1m;
        var second = await context.LatestPriceByCompanyAsync();
        Assert.Equal(100m, second[company.Id]);

        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = 150m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var third = await context.LatestPriceByCompanyAsync();
        Assert.Equal(150m, third[company.Id]);
    }

    [Fact]
    public async Task CycleNumbersByIdRebuildsAfterANewCycleIsSaved()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        var first = await context.CycleNumbersByIdAsync();
        Assert.Single(first);

        var added = new MarketCycle { CycleNumber = 2, Status = CycleStatus.Running, StartedAt = DateTime.UtcNow };
        context.MarketCycles.Add(added);
        await context.SaveChangesAsync();

        var second = await context.CycleNumbersByIdAsync();
        Assert.Equal(2, second[added.Id]);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
