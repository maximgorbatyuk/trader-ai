using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

    // The batch resolves bounds once and hands them to the engine on the quote: the active band and the wider
    // allowed range for a $100 reference.
    [Fact]
    public async Task GeneratedQuotesCarryResolvedOrderPriceBounds()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var decisionEngine = new QuoteCapturingDecisionEngine();
        var service = new MarketService(context, new MatchingEngine(context), decisionEngine, new MarketCycleLock(), new Random(1));

        await service.GenerateDecisionsAsync();

        var bounds = Assert.Single(decisionEngine.LastQuotes!).Bounds;
        Assert.NotNull(bounds);
        Assert.Equal(85m, bounds!.ActiveLowerPrice);
        Assert.Equal(115m, bounds.ActiveUpperPrice);
        Assert.Equal(75m, bounds.AllowedMinimumPrice);
        Assert.Equal(125m, bounds.AllowedMaximumPrice);
    }

    // Server-owned validation cannot be bypassed by a deferred automated write: an intent priced beyond the
    // allowed range is dropped rather than persisted.
    [Fact]
    public async Task DeferredAutomatedOrderBeyondTheAllowedRangeIsRejected()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var service = new MarketService(context, new MatchingEngine(context), new FixedBuyIntentEngine(200m), new MarketCycleLock(), new Random(1));

        var result = await service.GenerateDecisionsAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.OrdersPlaced);
        Assert.Equal(0, await context.Orders.CountAsync());
    }

    [Fact]
    public async Task FundKeepsFifteenPercentWhenMemberCanLeaveNextTradingDay()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var currentCycle = await context.MarketCycles.SingleAsync();
        var currentDay = new TradingDay { DayNumber = 20, State = TradingSessionState.Trading };
        var joinedDay = new TradingDay { DayNumber = 14, State = TradingSessionState.Trading };
        context.TradingDays.AddRange(currentDay, joinedDay);
        await context.SaveChangesAsync();
        currentCycle.TradingDayId = currentDay.Id;
        currentCycle.TradingCycleNumber = 1;
        currentDay.OpenedInCycleId = currentCycle.Id;
        var joinedCycle = new MarketCycle
        {
            CycleNumber = 14,
            TradingDayId = joinedDay.Id,
            TradingCycleNumber = 1,
            Status = CycleStatus.Completed,
        };
        context.MarketCycles.Add(joinedCycle);
        await context.SaveChangesAsync();
        joinedDay.OpenedInCycleId = joinedCycle.Id;
        var market = await context.Markets.SingleAsync();
        market.CurrentTradingDayId = currentDay.Id;

        var fundParticipant = new Participant
        {
            Name = "Reserve Fund",
            Type = ParticipantType.CollectiveFund,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 200m,
            CurrentBalance = 200m,
            SettledCashBalance = 200m,
            IsActive = true,
        };
        var member = new Participant
        {
            Name = "Reserve Member",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            CurrentBalance = 0m,
            SettledCashBalance = 0m,
            IsActive = true,
        };
        context.Participants.AddRange(fundParticipant, member);
        await context.SaveChangesAsync();
        var company = await context.Companies.FirstAsync();
        context.Holdings.Add(new Holding
        {
            ParticipantId = fundParticipant.Id,
            CompanyId = company.Id,
            Quantity = 8,
            SettledQuantity = 8,
            AverageCost = 100m,
        });
        var fund = new CollectiveFund
        {
            ParticipantId = fundParticipant.Id,
            FoundedByParticipantId = member.Id,
            Status = CollectiveFundStatus.Active,
            CreatedInCycleId = joinedCycle.Id,
            CreatedAt = DateTime.UtcNow,
        };
        context.CollectiveFunds.Add(fund);
        await context.SaveChangesAsync();
        context.CollectiveFundParticipants.Add(new CollectiveFundParticipant
        {
            CollectiveFundId = fund.Id,
            ParticipantId = member.Id,
            JoinedAt = DateTime.UtcNow,
            JoinedInCycleId = joinedCycle.Id,
            DepositAmount = 900m,
        });
        await context.SaveChangesAsync();

        var engine = new CashCapturingDecisionEngine();
        var marginOptions = Options.Create(new MarginOptions { Enabled = true });
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            engine,
            new MarketCycleLock(),
            new Random(1),
            marginService: new MarginService(context, marginOptions),
            collectiveFundOptions: Options.Create(new CollectiveFundOptions
            {
                MinimumMembershipTradingDays = 7,
                CashBufferFraction = 0.10m,
                PreLeaveCashBufferFraction = 0.15m,
            }));

        await service.GenerateDecisionsAsync();

        Assert.Equal(50m, engine.AvailableCashByParticipantId[fundParticipant.Id]);
    }

    [Fact]
    public async Task ConfiguredAiAgentsAreNotSentToTheDecisionEngine()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var aiTrader = new Participant
        {
            Name = "AI Trader",
            Type = ParticipantType.AIAgent,
            IsActive = true,
            CurrentBalance = 10_000m,
            SettledCashBalance = 10_000m,
        };
        context.Participants.Add(aiTrader);
        await context.SaveChangesAsync();

        var engine = new SeenParticipantsDecisionEngine();
        var service = new MarketService(context, new MatchingEngine(context), engine, new MarketCycleLock(), new Random(1));
        await service.GenerateDecisionsAsync();

        Assert.DoesNotContain(aiTrader.Id, engine.SeenParticipantIds);
        Assert.DoesNotContain(engine.SeenTypes, type => type == ParticipantType.AIAgent);
        Assert.Contains(engine.SeenTypes, type => type == ParticipantType.Individual);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed class SeenParticipantsDecisionEngine : IDecisionEngine
    {
        public List<int> SeenParticipantIds { get; } = new();

        public List<ParticipantType> SeenTypes { get; } = new();

        public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
        {
            SeenParticipantIds.Add(context.Participant.Id);
            SeenTypes.Add(context.Participant.Type);
            return [];
        }
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

    private sealed class CashCapturingDecisionEngine : IDecisionEngine
    {
        public Dictionary<int, decimal> AvailableCashByParticipantId { get; } = [];

        public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
        {
            AvailableCashByParticipantId[context.Participant.Id] = context.AvailableCash;
            return [];
        }
    }

    // Emits one buy for the first company at a fixed price, ignoring signals, so the batch's own range validation
    // is what decides whether the order survives.
    private sealed class FixedBuyIntentEngine(decimal limitPrice) : IDecisionEngine
    {
        public IReadOnlyList<OrderIntent> Decide(DecisionContext context) =>
            context.Companies.Count == 0
                ? []
                : [new OrderIntent(OrderType.Buy, context.Companies[0].CompanyId, 1, limitPrice)];
    }
}
