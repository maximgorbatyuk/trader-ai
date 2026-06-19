using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class MarketLoopTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly MarketService marketService;

    public MarketLoopTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
        marketService = new MarketService(context, new MatchingEngine(context), new RuleBasedDecisionEngine(), new MarketCycleLock());
    }

    [Fact]
    public async Task CycleTickDecidesAndAdvancesWhenMarketIsRunning()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        var tick = await marketService.RunCycleTickAsync();

        Assert.True(tick.Ran);
        Assert.Equal(2, tick.OrdersPlaced);
        Assert.Equal(1, tick.FillCount);
        Assert.Equal(1, tick.CompletedCycleNumber);
        Assert.Equal(1, await context.ShareTransactions.CountAsync());
    }

    [Fact]
    public async Task CycleTickIsSkippedWhenMarketIsPaused()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        await marketService.SetStatusAsync(MarketStatus.Paused);

        var tick = await marketService.RunCycleTickAsync();

        Assert.False(tick.Ran);
        Assert.Equal(0, await context.Orders.CountAsync());
        Assert.Equal(0, await context.ShareTransactions.CountAsync());
    }

    [Fact]
    public async Task ResumingAfterPauseLetsCycleTicksRunAgain()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        await marketService.SetStatusAsync(MarketStatus.Paused);
        Assert.False((await marketService.RunCycleTickAsync()).Ran);

        await marketService.SetStatusAsync(MarketStatus.Running);
        var tick = await marketService.RunCycleTickAsync();

        Assert.True(tick.Ran);
    }

    [Fact]
    public async Task SetStatusReturnsNullWhenNoMarketExists()
    {
        Assert.Null(await marketService.SetStatusAsync(MarketStatus.Paused));
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
