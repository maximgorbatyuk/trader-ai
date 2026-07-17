using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
        marketService = CreateMarketService(bigInvestmentEnabled: true);
    }

    private MarketService CreateMarketService(bool bigInvestmentEnabled)
    {
        var chanceRates = Options.Create(new RandomChanceRatesOptions());
        var bigInvestmentService = new BigInvestmentService(
            context,
            Options.Create(new BigInvestmentOptions { Enabled = bigInvestmentEnabled }),
            chanceRates,
            new MarketImpactService(context),
            new Random(2));
        return new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            chanceRates: chanceRates,
            marginService: new MarginService(context, Options.Create(new MarginOptions { Enabled = true })),
            automatedBuyOrderPolicy: new AutomatedBuyOrderPolicy(Options.Create(new AutomatedTradingOptions())),
            bigInvestmentService: bigInvestmentService);
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
        var buy = await context.Orders.SingleAsync(order => order.Type == OrderType.Buy);
        Assert.Equal(2, buy.Quantity);
        Assert.Equal(100m, buy.LimitPrice);
    }

    [Fact]
    public async Task ValidBigInvestmentIsAppliedFromAiDecision()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.CurrentBalance = 100_000m;
        participant.SettledCashBalance = 100_000m;
        await context.SaveChangesAsync();
        var decision = new AiTradeDecision(
            "fund company directly",
            [],
            bigInvestment: new AiBigInvestmentDecision(seed.CompanyBId, 50_000m, "long-term growth"));

        var result = await marketService.ApplyAiDecisionAsync(seed.ParticipantId, 1, decision);

        Assert.True(result.BigInvestment!.Applied);
        Assert.Equal(500, result.BigInvestment.SharesMinted);
        Assert.Equal(50_000m, participant.CurrentBalance);
        Assert.Equal(50_000m, participant.SettledCashBalance);
        Assert.Equal(1, await context.CompanyInvestments.CountAsync());
    }

    [Fact]
    public async Task BigInvestmentOnlyDecisionPersistsTheCompleteDealAndRaisedPrice()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.CurrentBalance = 100_000m;
        participant.SettledCashBalance = 100_000m;
        context.Auditors.Add(new Auditor
        {
            Name = "Ratings",
            Description = "Test auditor",
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        var decision = new AiTradeDecision(
            "fund company directly",
            [],
            bigInvestment: new AiBigInvestmentDecision(seed.CompanyBId, 50_000m, "long-term growth"));

        var result = await marketService.ApplyAiDecisionAsync(seed.ParticipantId, 1, decision);
        context.ChangeTracker.Clear();

        Assert.True(result.BigInvestment!.Applied);
        Assert.Equal(1, await context.ShareTransactions.CountAsync());
        Assert.Equal(1, await context.OrderFills.CountAsync());
        Assert.Equal(1, await context.MoneyTransactions.CountAsync());
        Assert.Equal(1, await context.CompanyRatings.CountAsync());
        Assert.Equal(1, await context.NewsPosts.CountAsync());
        Assert.Equal(
            108m,
            await context.PriceSnapshots
                .Where(snapshot => snapshot.CompanyId == seed.CompanyBId)
                .OrderByDescending(snapshot => snapshot.Id)
                .Select(snapshot => snapshot.Price)
                .FirstAsync());
    }

    [Fact]
    public async Task DisabledBigInvestmentRejectsAnAiRequest()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.CurrentBalance = 100_000m;
        participant.SettledCashBalance = 100_000m;
        await context.SaveChangesAsync();
        var decision = new AiTradeDecision(
            "fund company directly",
            [],
            bigInvestment: new AiBigInvestmentDecision(seed.CompanyBId, 50_000m, "long-term growth"));

        var result = await CreateMarketService(bigInvestmentEnabled: false)
            .ApplyAiDecisionAsync(seed.ParticipantId, 1, decision);

        Assert.False(result.BigInvestment!.Applied);
        Assert.Contains("disabled", result.BigInvestment.RejectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await context.CompanyInvestments.CountAsync());
    }

    [Fact]
    public async Task BigInvestmentRebuildsExposureBeforeApplyingOrders()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.CurrentBalance = 100_000m;
        participant.SettledCashBalance = 100_000m;
        await AddSellAsync(seed.CompanyBId, quantity: 10, limitPrice: 100m);
        await context.SaveChangesAsync();
        var decision = new AiTradeDecision(
            "invest before considering another order",
            [new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 100m, "only if exposure permits")],
            bigInvestment: new AiBigInvestmentDecision(seed.CompanyBId, 90_000m, "large direct position"));

        var result = await marketService.ApplyAiDecisionAsync(seed.ParticipantId, 1, decision);

        Assert.True(result.BigInvestment!.Applied);
        Assert.False(result.Orders[0].Applied);
        Assert.Contains("exposure", result.Orders[0].RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BigInvestmentRejectsAnAmountThatWouldBeSilentlyRoundedDown()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.CurrentBalance = 100_000m;
        participant.SettledCashBalance = 100_000m;
        await context.SaveChangesAsync();
        var decision = new AiTradeDecision(
            "use an exact amount",
            [],
            bigInvestment: new AiBigInvestmentDecision(seed.CompanyBId, 50_050m, "avoid silent adjustment"));

        var result = await marketService.ApplyAiDecisionAsync(seed.ParticipantId, 1, decision);

        Assert.False(result.BigInvestment!.Applied);
        Assert.Contains("whole shares", result.BigInvestment.RejectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(100_000m, participant.CurrentBalance);
        Assert.Equal(0, await context.CompanyInvestments.CountAsync());
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

    [Fact]
    public async Task BelowTargetBuyMustCrossTheBestExecutableSeller()
    {
        var seed = await SeedAsync();
        await AddSellAsync(seed.CompanyBId, quantity: 20, limitPrice: 100m);

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            Decision(new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 99m, "too passive")));

        Assert.False(result.Orders[0].Applied);
        Assert.Contains("cross", result.Orders[0].RejectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(await context.Orders.ToListAsync(),
            order => order.ParticipantId == seed.ParticipantId && order.Type == OrderType.Buy);
    }

    [Fact]
    public async Task CrossingBuyAboveBestAskKeepsAiPriceAndQuantityExact()
    {
        var seed = await SeedAsync();
        await AddSellAsync(seed.CompanyBId, quantity: 20, limitPrice: 99m);

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            Decision(new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 100m, "cross with room")));

        Assert.True(result.Orders[0].Applied);
        var order = await context.Orders.SingleAsync(candidate => candidate.ParticipantId == seed.ParticipantId);
        Assert.Equal(2, order.Quantity);
        Assert.Equal(100m, order.LimitPrice);
    }

    [Fact]
    public async Task BuyInsideAllowedRangeButOutsideActiveBandIsRejected()
    {
        var seed = await SeedAsync();

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            Decision(new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 1, 120m, "outside active band")));

        Assert.False(result.Orders[0].Applied);
        Assert.Contains("active band", result.Orders[0].RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuantityEnvelopeIsRecomputedAtTheAiExactCrossingPrice()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.CurrentBalance = 9_000m;
        participant.SettledCashBalance = 9_000m;
        await AddSellAsync(seed.CompanyBId, quantity: 20, limitPrice: 99m);
        await context.SaveChangesAsync();

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            Decision(new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 101m, "too large at my price")));

        Assert.False(result.Orders[0].Applied);
        Assert.Contains("at most 1", result.Orders[0].RejectionReason);
    }

    [Fact]
    public async Task MultipleBuysCannotReuseTheSameExecutableSellQuantity()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.CurrentBalance = 99_000m;
        participant.SettledCashBalance = 99_000m;
        await AddSellAsync(seed.CompanyBId, quantity: 10, limitPrice: 100m);
        await context.SaveChangesAsync();

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            Decision(
                new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 10, 100m, "take available ask"),
                new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 10, 100m, "reuse same ask")));

        Assert.True(result.Orders[0].Applied);
        Assert.False(result.Orders[1].Applied);
        Assert.Single(await context.Orders
            .Where(order => order.ParticipantId == seed.ParticipantId && order.Type == OrderType.Buy)
            .ToListAsync());
    }

    [Fact]
    public async Task LaterHigherPricedBuyCannotJumpAnEarlierAiAllocation()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.CurrentBalance = 99_000m;
        participant.SettledCashBalance = 99_000m;
        var lowerAsk = await AddSellAsync(seed.CompanyBId, quantity: 10, limitPrice: 100m);
        var higherAsk = await AddSellAsync(seed.CompanyBId, quantity: 2, limitPrice: 101m);
        await context.SaveChangesAsync();

        var application = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            Decision(
                new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 10, 100m, "take lower ask"),
                new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 101m, "jump earlier allocation")));

        Assert.True(application.Orders[0].Applied);
        Assert.False(application.Orders[1].Applied);
        Assert.Contains("priority", application.Orders[1].RejectionReason, StringComparison.OrdinalIgnoreCase);

        var cycle = await context.MarketCycles.SingleAsync();
        var fills = await new MatchingEngine(context).RunAsync(cycle);
        await context.SaveChangesAsync();

        Assert.Equal(1, fills);
        Assert.Equal(OrderStatus.Filled, lowerAsk.Status);
        Assert.Equal(OrderStatus.Open, higherAsk.Status);
        var aiBuy = await context.Orders.SingleAsync(order => order.Id == application.Orders[0].CreatedOrderId);
        Assert.Equal(10, aiBuy.FilledQuantity);
        Assert.Equal(10, await context.Holdings
            .Where(holding => holding.ParticipantId == seed.ParticipantId && holding.CompanyId == seed.CompanyBId)
            .Select(holding => holding.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task ExistingCrossingBuyKeepsPriorityOverANewHigherAiBuy()
    {
        var seed = await SeedAsync();
        var ask = await AddSellAsync(seed.CompanyBId, quantity: 10, limitPrice: 100m);
        var existingBuyer = new Participant
        {
            Name = "Existing buyer",
            Type = ParticipantType.Individual,
            IsActive = true,
            CurrentBalance = 10_000m,
            SettledCashBalance = 10_000m,
            ReservedBalance = 1_000m,
        };
        context.Participants.Add(existingBuyer);
        await context.SaveChangesAsync();
        var cycleId = (await context.Markets.SingleAsync()).CurrentCycleId!.Value;
        var existingBuy = new Order
        {
            ParticipantId = existingBuyer.Id,
            CompanyId = seed.CompanyBId,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 10,
            LimitPrice = 100m,
            ReservedCashAmount = 1_000m,
            CreatedInCycleId = cycleId,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        context.Orders.Add(existingBuy);
        await context.SaveChangesAsync();

        var application = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            Decision(new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 101m, "jump existing demand")));

        Assert.False(application.Orders[0].Applied);
        Assert.Contains("priority", application.Orders[0].RejectionReason, StringComparison.OrdinalIgnoreCase);

        var cycle = await context.MarketCycles.SingleAsync();
        var fills = await new MatchingEngine(context).RunAsync(cycle);
        await context.SaveChangesAsync();

        Assert.Equal(1, fills);
        Assert.Equal(OrderStatus.Filled, existingBuy.Status);
        Assert.Equal(OrderStatus.Filled, ask.Status);
        Assert.DoesNotContain(await context.Orders.ToListAsync(),
            order => order.ParticipantId == seed.ParticipantId && order.Type == OrderType.Buy);
        Assert.Equal(10, await context.Holdings
            .Where(holding => holding.ParticipantId == existingBuyer.Id && holding.CompanyId == seed.CompanyBId)
            .Select(holding => holding.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task EqualPricedAiBuyMayRemainBehindAnEarlierAllocation()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.CurrentBalance = 99_000m;
        participant.SettledCashBalance = 99_000m;
        await AddSellAsync(seed.CompanyBId, quantity: 12, limitPrice: 100m);
        await context.SaveChangesAsync();

        var application = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            Decision(
                new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 10, 100m, "take most of ask"),
                new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 100m, "remain behind earlier buy")));

        Assert.All(application.Orders, order => Assert.True(order.Applied));
        Assert.Equal(100m, application.Orders[1].LimitPrice);

        var cycle = await context.MarketCycles.SingleAsync();
        var fills = await new MatchingEngine(context).RunAsync(cycle);
        await context.SaveChangesAsync();

        Assert.Equal(2, fills);
        var buys = await context.Orders
            .Where(order => order.ParticipantId == seed.ParticipantId && order.Type == OrderType.Buy)
            .OrderBy(order => order.Id)
            .ToListAsync();
        Assert.Equal([10, 2], buys.Select(order => order.FilledQuantity).ToArray());
    }

    [Fact]
    public async Task PartialLowerLevelCannotLetLaterHigherBuyStealEarlierAllocation()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.CurrentBalance = 99_000m;
        participant.SettledCashBalance = 99_000m;
        var lowerAsk = await AddSellAsync(seed.CompanyBId, quantity: 10, limitPrice: 100m);
        var higherAsk = await AddSellAsync(seed.CompanyBId, quantity: 10, limitPrice: 101m);
        await context.SaveChangesAsync();

        var application = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            Decision(
                new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 5, 100m, "take part of lower level"),
                new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 15, 101m, "jump across both levels")));

        Assert.True(application.Orders[0].Applied);
        Assert.False(application.Orders[1].Applied);
        Assert.Contains("priority", application.Orders[1].RejectionReason, StringComparison.OrdinalIgnoreCase);

        var cycle = await context.MarketCycles.SingleAsync();
        var fills = await new MatchingEngine(context).RunAsync(cycle);
        await context.SaveChangesAsync();

        Assert.Equal(1, fills);
        Assert.Equal(5, lowerAsk.FilledQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, lowerAsk.Status);
        Assert.Equal(OrderStatus.Open, higherAsk.Status);
        var earlierBuy = await context.Orders.SingleAsync(order => order.Id == application.Orders[0].CreatedOrderId);
        Assert.Equal(OrderStatus.Filled, earlierBuy.Status);
        Assert.Equal(5, earlierBuy.FilledQuantity);
    }

    [Fact]
    public async Task BelowTargetBuyQuantityMustStayInsideMeaningfulEnvelope()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.RiskProfile = RiskProfile.Medium;
        participant.CurrentBalance = 99_000m;
        participant.SettledCashBalance = 99_000m;
        await AddSellAsync(seed.CompanyBId, quantity: 50, limitPrice: 100m);
        await context.SaveChangesAsync();

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            Decision(
                new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 4, 100m, "below minimum"),
                new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 21, 100m, "above maximum")));

        Assert.All(result.Orders, order => Assert.False(order.Applied));
        Assert.Contains("at least 5", result.Orders[0].RejectionReason);
        Assert.Contains("at most 20", result.Orders[1].RejectionReason);
    }

    [Theory]
    [InlineData(RiskProfile.Low)]
    [InlineData(RiskProfile.Medium)]
    public async Task LowAndMediumRiskCannotReserveWithMargin(RiskProfile riskProfile)
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.RiskProfile = riskProfile;
        participant.CurrentBalance = 1_000m;
        participant.SettledCashBalance = 1_000m;
        participant.ReservedBalance = 950m;
        await context.SaveChangesAsync();

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            Decision(new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 1, 100m, "needs margin")));

        Assert.False(result.Orders[0].Applied);
        Assert.Contains("only available to High risk", result.Orders[0].RejectionReason);
    }

    [Fact]
    public async Task HighRiskMarginCannotExceedTenPercentOfNetWorth()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.RiskProfile = RiskProfile.High;
        participant.CurrentBalance = 1_000m;
        participant.SettledCashBalance = 1_000m;
        participant.ReservedBalance = 950m;
        context.MarginAccounts.Add(new MarginAccount
        {
            ParticipantId = participant.Id,
            DebitBalance = 200m,
            InitialMarginRate = 0.5m,
            MaintenanceMarginRate = 0.25m,
            Status = MarginAccountStatus.Active,
        });
        await context.SaveChangesAsync();

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            Decision(new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 1, 100m, "over margin cap")));

        Assert.False(result.Orders[0].Applied);
        Assert.Contains("margin", result.Orders[0].RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AggregateReservedBuysReduceFreshExposureHeadroom()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.RiskProfile = RiskProfile.Medium;
        participant.CurrentBalance = 100_000m;
        participant.SettledCashBalance = 100_000m;
        participant.ReservedBalance = 60_000m;
        await context.SaveChangesAsync();

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            Decision(new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 1, 100m, "ignores commitments")));

        Assert.False(result.Orders[0].Applied);
        Assert.Contains("reserved", result.Orders[0].RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationsAreIndependentReleaseCashAndRunBeforeNewOrders()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        var other = await context.Participants.SingleAsync(candidate => candidate.Name == "Seller");
        var cycleId = (await context.Markets.SingleAsync()).CurrentCycleId!.Value;
        var ownBuy = new Order
        {
            ParticipantId = participant.Id,
            CompanyId = seed.CompanyBId,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 2,
            LimitPrice = 100m,
            ReservedCashAmount = 200m,
            CreatedInCycleId = cycleId,
        };
        var ownSell = new Order
        {
            ParticipantId = participant.Id,
            CompanyId = seed.CompanyAId,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = 2,
            LimitPrice = 100m,
            CreatedInCycleId = cycleId,
        };
        var otherOrder = new Order
        {
            ParticipantId = other.Id,
            CompanyId = seed.CompanyBId,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 1,
            LimitPrice = 100m,
            ReservedCashAmount = 100m,
            CreatedInCycleId = cycleId,
        };
        var closedOwnOrder = new Order
        {
            ParticipantId = participant.Id,
            CompanyId = seed.CompanyBId,
            Type = OrderType.Buy,
            Status = OrderStatus.Filled,
            Quantity = 1,
            FilledQuantity = 1,
            LimitPrice = 100m,
            CreatedInCycleId = cycleId,
        };
        context.Orders.AddRange(ownBuy, ownSell, otherOrder, closedOwnOrder);
        participant.ReservedBalance = 200m;
        await context.SaveChangesAsync();

        var decision = new AiTradeDecision(
            "replace stale orders",
            [new AiTradeOrderDecision(OrderType.Buy, seed.CompanyAId, 2, 100m, "replacement")],
            [ownBuy.Id, ownSell.Id, otherOrder.Id, closedOwnOrder.Id, int.MaxValue]);

        var result = await marketService.ApplyAiDecisionAsync(seed.ParticipantId, 1, decision);

        Assert.Equal(5, result.Cancellations.Length);
        Assert.True(result.Cancellations[0].Applied);
        Assert.True(result.Cancellations[1].Applied);
        Assert.False(result.Cancellations[2].Applied);
        Assert.False(result.Cancellations[3].Applied);
        Assert.False(result.Cancellations[4].Applied);
        Assert.True(result.Orders[0].Applied);
        Assert.Equal(OrderStatus.Cancelled, ownBuy.Status);
        Assert.Equal(OrderStatus.Cancelled, ownSell.Status);
        Assert.Equal(OrderStatus.Open, otherOrder.Status);
        Assert.Equal(OrderStatus.Filled, closedOwnOrder.Status);
        Assert.Equal(200m, participant.ReservedBalance);
        Assert.Contains(await context.MoneyTransactions.ToListAsync(),
            transaction => transaction.Type == MoneyTransactionType.Release && transaction.Amount == 200m);
    }

    [Fact]
    public async Task CancellationRecomputesReplacementEnvelopeAgainstRestoredSupply()
    {
        var seed = await SeedAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        participant.CurrentBalance = 100_000m;
        participant.SettledCashBalance = 100_000m;
        participant.ReservedBalance = 720m;
        await AddSellAsync(seed.CompanyBId, quantity: 10, limitPrice: 90m);

        var cycleId = (await context.Markets.SingleAsync()).CurrentCycleId!.Value;
        var ownBuy = new Order
        {
            ParticipantId = participant.Id,
            CompanyId = seed.CompanyBId,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 8,
            LimitPrice = 90m,
            ReservedCashAmount = 720m,
            CreatedInCycleId = cycleId,
        };
        context.Orders.Add(ownBuy);
        await context.SaveChangesAsync();

        var snapshot = await SnapshotBuilder().BuildAsync(seed.ParticipantId);
        var company = Assert.Single(snapshot!.Companies, candidate => candidate.CompanyId == seed.CompanyBId);
        Assert.Equal(90m, company.BestExecutableSellPrice);
        Assert.Equal(2, company.BestExecutableSellQuantity);
        Assert.Equal(2, company.BuyEnvelope!.MaximumQuantity);
        Assert.Equal("CurrentOpenOrdersBeforeCancellations", company.BuyEnvelope.StateBasis);

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            new AiTradeDecision(
                "replace current demand",
                [new AiTradeOrderDecision(OrderType.Buy, seed.CompanyBId, 2, 90m, "replace exactly")],
                [ownBuy.Id]));

        Assert.True(result.Cancellations[0].Applied);
        Assert.False(result.Orders[0].Applied);
        Assert.Contains("at least 3", result.Orders[0].RejectionReason);
        Assert.Equal(2, result.Orders[0].Quantity);
        Assert.Equal(90m, result.Orders[0].LimitPrice);
        Assert.Equal(OrderStatus.Cancelled, ownBuy.Status);
        Assert.Equal(0m, participant.ReservedBalance);
    }

    [Fact]
    public async Task AiCannotCancelItsOpenMarginCallOrder()
    {
        var seed = await SeedAsync();
        var forcedOrder = await AddForcedBuyOrderAsync(seed, marginCallOwned: true);

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            new AiTradeDecision("try to evade margin call", [], [forcedOrder.Id]));

        Assert.False(result.Cancellations[0].Applied);
        Assert.Contains("margin", result.Cancellations[0].RejectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OrderStatus.Open, forcedOrder.Status);
        Assert.Equal(100m, (await context.Participants.SingleAsync(p => p.Id == seed.ParticipantId)).ReservedBalance);
        Assert.DoesNotContain(await context.MoneyTransactions.ToListAsync(),
            transaction => transaction.Type == MoneyTransactionType.Release);
    }

    [Fact]
    public async Task AiCannotCancelItsOpenLoanDistressOrder()
    {
        var seed = await SeedAsync();
        var forcedOrder = await AddForcedBuyOrderAsync(seed, marginCallOwned: false);

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            new AiTradeDecision("try to evade loan distress", [], [forcedOrder.Id]));

        Assert.False(result.Cancellations[0].Applied);
        Assert.Contains("loan", result.Cancellations[0].RejectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OrderStatus.Open, forcedOrder.Status);
        Assert.Equal(100m, (await context.Participants.SingleAsync(p => p.Id == seed.ParticipantId)).ReservedBalance);
        Assert.DoesNotContain(await context.MoneyTransactions.ToListAsync(),
            transaction => transaction.Type == MoneyTransactionType.Release);
    }

    [Fact]
    public async Task DuplicateCancellationIdIsRejectedAndReleasesOnlyOnce()
    {
        var seed = await SeedAsync();
        var order = Assert.Single(await AddCancellableBuyOrdersAsync(seed, count: 1));

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            new AiTradeDecision("duplicate internal input", [], [order.Id, order.Id]));

        Assert.True(result.Cancellations[0].Applied);
        Assert.False(result.Cancellations[1].Applied);
        Assert.Contains("duplicate", result.Cancellations[1].RejectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(0m, (await context.Participants.SingleAsync(p => p.Id == seed.ParticipantId)).ReservedBalance);
        Assert.Single(await context.MoneyTransactions
            .Where(transaction => transaction.Type == MoneyTransactionType.Release)
            .ToListAsync());
    }

    [Fact]
    public async Task InternalCancellationListIsCappedWithExplicitRejectionResults()
    {
        var seed = await SeedAsync();
        var orders = await AddCancellableBuyOrdersAsync(seed, count: 11);

        var result = await marketService.ApplyAiDecisionAsync(
            seed.ParticipantId,
            1,
            new AiTradeDecision("oversized internal input", [], orders.Select(order => order.Id).ToArray()));

        Assert.Equal(11, result.Cancellations.Length);
        Assert.All(result.Cancellations.Take(10), cancellation => Assert.True(cancellation.Applied));
        Assert.False(result.Cancellations[10].Applied);
        Assert.Contains("limit", result.Cancellations[10].RejectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(100m, (await context.Participants.SingleAsync(p => p.Id == seed.ParticipantId)).ReservedBalance);
        Assert.Equal(10, await context.MoneyTransactions
            .CountAsync(transaction => transaction.Type == MoneyTransactionType.Release));
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
            RiskProfile = RiskProfile.Medium,
        };
        var seller = new Participant
        {
            Name = "Seller",
            Type = ParticipantType.Individual,
            IsActive = true,
            CurrentBalance = 10_000m,
            SettledCashBalance = 10_000m,
        };
        context.Participants.AddRange(participant, seller);
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

    private async Task<Order> AddSellAsync(int companyId, int quantity, decimal limitPrice)
    {
        var seller = await context.Participants.SingleAsync(candidate => candidate.Name == "Seller");
        var cycleId = (await context.Markets.SingleAsync()).CurrentCycleId!.Value;
        var holding = await context.Holdings
            .FirstOrDefaultAsync(candidate => candidate.ParticipantId == seller.Id && candidate.CompanyId == companyId);
        if (holding is null)
        {
            holding = new Holding
            {
                ParticipantId = seller.Id,
                CompanyId = companyId,
                AverageCost = limitPrice,
            };
            context.Holdings.Add(holding);
        }
        holding.Quantity += quantity;
        holding.SettledQuantity += quantity;

        var order = new Order
        {
            ParticipantId = seller.Id,
            CompanyId = companyId,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = quantity,
            LimitPrice = limitPrice,
            CreatedInCycleId = cycleId,
        };
        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return order;
    }

    private async Task<Order> AddForcedBuyOrderAsync(Seed seed, bool marginCallOwned)
    {
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        var market = await context.Markets.SingleAsync();
        var cycleId = market.CurrentCycleId!.Value;
        int? marginCallId = null;
        int? loanId = null;

        if (marginCallOwned)
        {
            var account = new MarginAccount
            {
                ParticipantId = participant.Id,
                InitialMarginRate = 0.5m,
                MaintenanceMarginRate = 0.25m,
                Status = MarginAccountStatus.UnderCall,
            };
            context.MarginAccounts.Add(account);
            await context.SaveChangesAsync();
            var call = new MarginCall
            {
                MarginAccountId = account.Id,
                OpenedInTradingDayId = market.CurrentTradingDayId!.Value,
                OpenedInCycleId = cycleId,
                Status = MarginCallStatus.Open,
                CreatedAt = DateTime.UtcNow,
            };
            context.MarginCalls.Add(call);
            await context.SaveChangesAsync();
            marginCallId = call.Id;
        }
        else
        {
            var bank = new Bank { Name = "Test bank", Balance = 1_000_000m };
            context.Banks.Add(bank);
            await context.SaveChangesAsync();
            var loan = new Loan
            {
                BankId = bank.Id,
                ParticipantId = participant.Id,
                Principal = 1_000m,
                RemainingPrincipal = 1_000m,
                TermCycles = 10,
                ScheduledInstallment = 100m,
                Status = LoanStatus.Open,
                OpenedInCycleId = cycleId,
                CreatedAt = DateTime.UtcNow,
            };
            context.Loans.Add(loan);
            await context.SaveChangesAsync();
            loanId = loan.Id;
        }

        var order = new Order
        {
            ParticipantId = participant.Id,
            CompanyId = seed.CompanyBId,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 1,
            LimitPrice = 100m,
            ReservedCashAmount = 100m,
            RelatedMarginCallId = marginCallId,
            RelatedLoanId = loanId,
            CreatedInCycleId = cycleId,
        };
        participant.ReservedBalance = 100m;
        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return order;
    }

    private async Task<List<Order>> AddCancellableBuyOrdersAsync(Seed seed, int count)
    {
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
        var cycleId = (await context.Markets.SingleAsync()).CurrentCycleId!.Value;
        var orders = Enumerable.Range(0, count)
            .Select(index => new Order
            {
                ParticipantId = participant.Id,
                CompanyId = seed.CompanyBId,
                Type = OrderType.Buy,
                Status = OrderStatus.Open,
                Quantity = 1,
                LimitPrice = 100m + index / 100m,
                ReservedCashAmount = 100m,
                CreatedInCycleId = cycleId,
            })
            .ToList();
        participant.ReservedBalance = count * 100m;
        context.Orders.AddRange(orders);
        await context.SaveChangesAsync();
        return orders;
    }

    private AiMarketSnapshotBuilder SnapshotBuilder() => new(
        context,
        new MarginService(context, Options.Create(new MarginOptions { Enabled = true })),
        new TradingClockService(context, Options.Create(new TradingClockOptions
        {
            TradingCyclesPerDay = 210,
            TradingCycleSeconds = 2,
            BreakDurationSeconds = 60,
        })),
        Options.Create(new AiTradingOptions { HistoryCycles = 30, MaxOrdersPerDecision = 10 }),
        Options.Create(new TradeFeeOptions()),
        Options.Create(new SettlementOptions { SettlementLagTradingDays = 1 }),
        Options.Create(new MarginOptions { Enabled = true }),
        Options.Create(new VolatilityHaltOptions()),
        Options.Create(new BigInvestmentOptions { Enabled = true }),
        Options.Create(new RandomChanceRatesOptions()),
        new AutomatedBuyOrderPolicy(Options.Create(new AutomatedTradingOptions())));

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
