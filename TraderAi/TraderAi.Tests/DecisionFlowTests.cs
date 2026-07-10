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

    [Fact]
    public async Task GeneratedQuotesUseIndustrySentimentAndDefaultMissingIndustryToZero()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var industry = await context.Industries.SingleAsync();
        industry.SentimentValue = 375;
        var cycle = await context.MarketCycles.SingleAsync();
        var unmappedCompany = new Company
        {
            Name = "Unmapped",
            IndustryId = 999,
            IssuedSharesCount = 10,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Companies.Add(unmappedCompany);
        await context.SaveChangesAsync();
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = unmappedCompany.Id,
            Price = 50m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var decisionEngine = new QuoteCapturingDecisionEngine();
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            decisionEngine,
            new MarketCycleLock(),
            new Random(1));

        var result = await service.GenerateDecisionsAsync();

        Assert.True(result.Success);
        Assert.NotNull(decisionEngine.LastQuotes);
        var quotes = decisionEngine.LastQuotes!;
        Assert.Equal(375, Assert.Single(quotes, quote => quote.CompanyId != unmappedCompany.Id).SectorSentiment);
        Assert.Equal(0, Assert.Single(quotes, quote => quote.CompanyId == unmappedCompany.Id).SectorSentiment);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed class QuoteCapturingDecisionEngine : IDecisionEngine
    {
        public IReadOnlyList<CompanyQuote>? LastQuotes { get; private set; }

        public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
        {
            LastQuotes = context.Companies.ToArray();
            return [];
        }
    }
}
