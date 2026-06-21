using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class DecisionFlowTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly MarketService marketService;

    public DecisionFlowTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
        marketService = new MarketService(context, new MatchingEngine(context), new DeterministicDecisionEngine(), new MarketCycleLock(), new Random(1));
    }

    [Fact]
    public async Task GeneratedDecisionsPlaceOrdersThatSettleOnAdvance()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        var decisions = await marketService.GenerateDecisionsAsync();

        Assert.True(decisions.Success);
        Assert.Equal(2, decisions.OrdersPlaced);
        Assert.Equal(1, await context.Orders.CountAsync(order => order.Type == OrderType.Buy));
        Assert.Equal(1, await context.Orders.CountAsync(order => order.Type == OrderType.Sell));

        var advance = await marketService.AdvanceCycleAsync();
        Assert.Equal(1, advance.FillCount);

        var transaction = await context.ShareTransactions.SingleAsync();
        Assert.Equal(2, transaction.Quantity);

        // Buyer bids 110, seller asks 98; the match executes at the 104 midpoint.
        Assert.Equal(104m, transaction.Price);
    }

    [Fact]
    public async Task DecisionsAreSkippedForCompaniesThatAlreadyHaveOpenOrders()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        var first = await marketService.GenerateDecisionsAsync();
        Assert.Equal(2, first.OrdersPlaced);

        var second = await marketService.GenerateDecisionsAsync();
        Assert.Equal(0, second.OrdersPlaced);
    }

    [Fact]
    public async Task GeneratingDecisionsFailsWhenNoMarketExists()
    {
        var result = await marketService.GenerateDecisionsAsync();

        Assert.False(result.Success);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
