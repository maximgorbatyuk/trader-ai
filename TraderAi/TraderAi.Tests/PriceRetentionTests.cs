using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class PriceRetentionTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public PriceRetentionTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task ArchivingKeepsNewestLivePriceForEachCompany()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        var market = await context.Markets.SingleAsync();
        market.NextDividendCycleNumber = 100;
        var company = await context.Companies.SingleAsync();
        var firstSnapshot = await context.PriceSnapshots.SingleAsync();
        var currentCycle = await AddCyclesThroughAsync(4);
        var secondCycle = await context.MarketCycles.SingleAsync(cycle => cycle.CycleNumber == 2);
        var latestSnapshot = new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = 125m,
            CreatedInCycleId = secondCycle.Id,
            CreatedAt = DateTime.UtcNow,
        };
        context.PriceSnapshots.Add(latestSnapshot);
        market.CurrentCycleId = currentCycle.Id;
        await context.SaveChangesAsync();

        var service = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            archiveOptions: Options.Create(new ArchiveOptions { Enabled = true, RetentionCycles = 1 }));

        var result = await service.AdvanceCycleAsync();

        Assert.True(result.Success);
        Assert.Equal([latestSnapshot.Id], await context.PriceSnapshots.Select(snapshot => snapshot.Id).ToListAsync());
        Assert.Equal([firstSnapshot.Id], await context.PriceSnapshotArchives.Select(snapshot => snapshot.Id).ToListAsync());
        Assert.Equal(125m, (await context.PriceSnapshots.SingleAsync(snapshot => snapshot.CompanyId == company.Id)).Price);
        Assert.Contains(
            await context.ParticipantWorthSnapshots.ToListAsync(),
            snapshot => snapshot.HoldingsValue == 1_250m);
    }

    private async Task<MarketCycle> AddCyclesThroughAsync(int finalCycleNumber)
    {
        for (var cycleNumber = 2; cycleNumber <= finalCycleNumber; cycleNumber++)
        {
            context.MarketCycles.Add(new MarketCycle
            {
                CycleNumber = cycleNumber,
                Status = CycleStatus.Running,
                StartedAt = DateTime.UtcNow,
            });
        }

        await context.SaveChangesAsync();
        return await context.MarketCycles.SingleAsync(cycle => cycle.CycleNumber == finalCycleNumber);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
