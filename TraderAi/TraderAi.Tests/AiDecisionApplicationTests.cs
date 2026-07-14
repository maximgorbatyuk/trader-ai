using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AiDecisionApplicationTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly MarketService marketService;

    public AiDecisionApplicationTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        marketService = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1));
    }

    [Fact]
    public async Task MatchingRevisionAppliesValidBuyAndSell()
    {
        var seed = await SeedAsync();

        var decision = Decision(
            new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 100m, "buy stronger"),
            new AiTradeOrderDecision(OrderType.Sell, seed.CompanyAId, 5, 100m, "trim weaker"));

        var result = await marketService.ApplyAiDecisionAsync(seed.ParticipantId, 1, decision);

        Assert.True(result.ConfigurationStillCurrent);
        Assert.All(result.Orders, order => Assert.True(order.Applied));
        Assert.Equal(2, await context.Orders.CountAsync());
        Assert.All(await context.Orders.ToListAsync(), order => Assert.Equal(OrderStatus.Open, order.Status));
    }

    [Fact]
    public async Task StaleRevisionAppliesNothing()
    {
        var seed = await SeedAsync();
        var decision = Decision(new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 100m, "buy"));

        var result = await marketService.ApplyAiDecisionAsync(seed.ParticipantId, 999, decision);

        Assert.False(result.ConfigurationStillCurrent);
        Assert.Empty(result.Orders);
        Assert.Equal(0, await context.Orders.CountAsync());
    }

    [Fact]
    public async Task MissingConfigurationAppliesNothing()
    {
        var seed = await SeedAsync(withConfiguration: false);
        var decision = Decision(new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 100m, "buy"));

        var result = await marketService.ApplyAiDecisionAsync(seed.ParticipantId, 1, decision);

        Assert.False(result.ConfigurationStillCurrent);
        Assert.Equal(0, await context.Orders.CountAsync());
    }

    [Fact]
    public async Task InactiveParticipantAppliesNothing()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(p => p.Id == seed.ParticipantId);
        participant.IsActive = false;
        await context.SaveChangesAsync();

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId, 1, Decision(new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 100m, "buy")));

        Assert.False(result.ConfigurationStillCurrent);
    }

    [Fact]
    public async Task BankruptParticipantAppliesNothing()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(p => p.Id == seed.ParticipantId);
        participant.IsBankrupt = true;
        await context.SaveChangesAsync();

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId, 1, Decision(new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 100m, "buy")));

        Assert.False(result.ConfigurationStillCurrent);
    }

    [Fact]
    public async Task OneInvalidOrderDoesNotRollBackAValidOrder()
    {
        var seed = await SeedAsync();

        var decision = Decision(
            new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 100m, "valid"),
            new AiTradeOrderDecision(OrderType.Buy, 9999, 1, 100m, "unknown company"));

        var result = await marketService.ApplyAiDecisionAsync(seed.ParticipantId, 1, decision);

        Assert.True(result.Orders[0].Applied);
        Assert.False(result.Orders[1].Applied);
        Assert.NotNull(result.Orders[1].RejectionReason);
        Assert.Equal(1, await context.Orders.CountAsync());
    }

    [Fact]
    public async Task OwnedShareAndPriceRangeChecksAreEnforced()
    {
        var seed = await SeedAsync();

        var decision = Decision(
            new AiTradeOrderDecision(OrderType.Sell, seed.CompanyAId, 50, 100m, "oversell"),
            new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 1, 500m, "above allowed range"));

        var result = await marketService.ApplyAiDecisionAsync(seed.ParticipantId, 1, decision);

        Assert.False(result.Orders[0].Applied);
        Assert.False(result.Orders[1].Applied);
        Assert.Equal(0, await context.Orders.CountAsync());
    }

    [Fact]
    public async Task StoredReasonIsReturnedButNotCopiedToOrderRecords()
    {
        var seed = await SeedAsync();
        var decision = Decision(new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 100m, "distinct-ai-reason"));

        var result = await marketService.ApplyAiDecisionAsync(seed.ParticipantId, 1, decision);

        Assert.Equal("distinct-ai-reason", result.Orders[0].Reason);
        var order = await context.Orders.SingleAsync();
        var transactions = await context.MoneyTransactions.ToListAsync();
        Assert.DoesNotContain(transactions, transaction => (transaction.Description ?? string.Empty).Contains("distinct-ai-reason"));
        Assert.NotNull(result.Orders[0].CreatedOrderId);
        Assert.Equal(order.Id, result.Orders[0].CreatedOrderId);
    }

    private async Task<Seed> SeedAsync(bool withConfiguration = true)
    {
        var day = new TradingDay { DayNumber = 1, State = TradingSessionState.Trading, OpenedInCycleId = 0 };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();
        var cycle = new MarketCycle { CycleNumber = 1, TradingDayId = day.Id, TradingCycleNumber = 1, Status = CycleStatus.Running };
        var market = new Market { Name = "Market", Status = MarketStatus.Running };
        var industry = new Industry { Name = "Tech" };
        context.AddRange(cycle, market, industry);
        await context.SaveChangesAsync();

        var companyA = new Company { Name = "Acme", IndustryId = industry.Id, IssuedSharesCount = 1_000 };
        var companyB = new Company { Name = "Zenith", IndustryId = industry.Id, IssuedSharesCount = 1_000 };
        context.AddRange(companyA, companyB);
        await context.SaveChangesAsync();

        context.PriceSnapshots.AddRange(
            new PriceSnapshot { CompanyId = companyA.Id, Price = 100m, Capitalization = 100_000m, CreatedInCycleId = cycle.Id },
            new PriceSnapshot { CompanyId = companyB.Id, Price = 100m, Capitalization = 100_000m, CreatedInCycleId = cycle.Id });

        var participant = new Participant
        {
            Name = "AI Trader",
            Type = ParticipantType.AIAgent,
            IsActive = true,
            CurrentBalance = 10_000m,
            SettledCashBalance = 10_000m,
        };
        context.Participants.Add(participant);
        await context.SaveChangesAsync();

        context.Holdings.Add(new Holding
        {
            ParticipantId = participant.Id,
            CompanyId = companyA.Id,
            Quantity = 10,
            SettledQuantity = 10,
            AverageCost = 90m,
        });
        if (withConfiguration)
        {
            context.AiTraderConfigurations.Add(new AiTraderConfiguration
            {
                ParticipantId = participant.Id,
                ProviderId = "glm",
                Model = "glm-4.6",
                ApiKey = "secret-key",
                Revision = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        day.OpenedInCycleId = cycle.Id;
        market.CurrentCycleId = cycle.Id;
        market.CurrentTradingDayId = day.Id;
        await context.SaveChangesAsync();

        return new Seed(participant.Id, companyA.Id, companyB.Id);
    }

    private static AiTradeDecision Decision(params AiTradeOrderDecision[] orders)
        => new("summary", orders);

    private sealed record Seed(int ParticipantId, int CompanyAId, int CompanyBId);

    private sealed class NoOpDecisionEngine : IDecisionEngine
    {
        public IReadOnlyList<OrderIntent> Decide(DecisionContext context) => [];
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
