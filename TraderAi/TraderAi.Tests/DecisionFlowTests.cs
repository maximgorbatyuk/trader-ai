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

    [Fact]
    public async Task GeneratedContextsBatchExecutableSupplyAndIndividualFinancials()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.SingleAsync();
        var industry = await context.Industries.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        var seller = await context.Participants.SingleAsync(participant => participant.Name == "Alice");
        seller.ReservedBalance = 1_200m;

        var bank = new Bank { Name = "Test Bank", InterestRatePerCycle = 0.01m };
        context.Banks.Add(bank);
        await context.SaveChangesAsync();
        context.Loans.Add(new Loan
        {
            BankId = bank.Id,
            ParticipantId = seller.Id,
            Principal = 300m,
            RemainingPrincipal = 300m,
            InterestRatePerCycle = 0.01m,
            TermCycles = 10,
            ScheduledInstallment = 30m,
            PastDueInterest = 20m,
            AccruedFees = 10m,
            Status = LoanStatus.Open,
            OpenedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        context.MarginAccounts.Add(new MarginAccount
        {
            ParticipantId = seller.Id,
            DebitBalance = 100m,
            AccruedInterest = 10m,
            InitialMarginRate = 0.50m,
            MaintenanceMarginRate = 0.25m,
            Status = MarginAccountStatus.Active,
        });

        var haltedCompany = new Company
        {
            Name = "Halted",
            IndustryId = industry.Id,
            IssuedSharesCount = 20,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Companies.Add(haltedCompany);
        await context.SaveChangesAsync();
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = haltedCompany.Id,
            Price = 100m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        context.PriceBandStates.Add(new PriceBandState
        {
            CompanyId = haltedCompany.Id,
            State = LuldState.TradingPause,
            ReferencePrice = 100m,
            LowerBandPrice = 85m,
            UpperBandPrice = 115m,
            UpdatedInCycleId = cycle.Id,
        });
        var partiallyFilledBestAsk = IssuerSell(company.Id, cycle.Id, quantity: 4, price: 104m);
        partiallyFilledBestAsk.Status = OrderStatus.PartiallyFilled;
        partiallyFilledBestAsk.FilledQuantity = 2;
        context.Orders.AddRange(
            IssuerSell(company.Id, cycle.Id, quantity: 3, price: 104m),
            partiallyFilledBestAsk,
            IssuerSell(company.Id, cycle.Id, quantity: 9, price: 106m),
            IssuerSell(company.Id, cycle.Id, quantity: 100, price: 80m),
            IssuerSell(haltedCompany.Id, cycle.Id, quantity: 5, price: 100m));
        await context.SaveChangesAsync();

        var engine = new ContextCapturingDecisionEngine();
        var marginService = new MarginService(context, Options.Create(new MarginOptions { Enabled = true }));
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            engine,
            new MarketCycleLock(),
            new Random(1),
            marginService: marginService);

        await service.GenerateDecisionsAsync();

        var sellerContext = engine.Contexts.Single(candidate => candidate.Participant.Id == seller.Id);
        var quote = Assert.Single(sellerContext.Companies);
        Assert.Equal(company.Id, quote.CompanyId);
        Assert.Equal(10, quote.IssuedShares);
        Assert.Equal(104m, quote.BestExecutableSellPrice);
        Assert.Equal(5, quote.BestExecutableSellQuantity);
        Assert.Equal(1_000m, sellerContext.HoldingsValue);
        Assert.Equal(1_560m, sellerContext.NetWorth);
        Assert.Equal(-200m, sellerContext.AvailableBalance);
        Assert.Equal(1_580m, sellerContext.BuyingPower);
        Assert.Equal(110m, sellerContext.MarginLiability);
        Assert.Equal(1_200m, sellerContext.ReservedBuyNotional);
        Assert.True(sellerContext.HasAutomatedTradingData);
    }

    [Fact]
    public async Task HaltedHoldingStillContributesToIndividualValuationAndMarginBuyingPower()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var industry = await context.Industries.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        var buyer = await context.Participants.SingleAsync(participant => participant.Name == "Bob");
        var haltedCompany = new Company
        {
            Name = "Paused Holding",
            IndustryId = industry.Id,
            IssuedSharesCount = 100,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Companies.Add(haltedCompany);
        await context.SaveChangesAsync();
        context.Holdings.Add(new Holding
        {
            ParticipantId = buyer.Id,
            CompanyId = haltedCompany.Id,
            Quantity = 4,
            AverageCost = 100m,
        });
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = haltedCompany.Id,
            Price = 100m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        context.PriceBandStates.Add(new PriceBandState
        {
            CompanyId = haltedCompany.Id,
            State = LuldState.TradingPause,
            ReferencePrice = 100m,
            LowerBandPrice = 85m,
            UpperBandPrice = 115m,
            UpdatedInCycleId = cycle.Id,
        });
        context.Orders.Add(IssuerSell(haltedCompany.Id, cycle.Id, quantity: 10, price: 100m));
        await context.SaveChangesAsync();

        var engine = new ContextCapturingDecisionEngine();
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            engine,
            new MarketCycleLock(),
            new Random(1),
            marginService: new MarginService(context, Options.Create(new MarginOptions { Enabled = true })));

        await service.GenerateDecisionsAsync();

        var buyerContext = engine.Contexts.Single(candidate => candidate.Participant.Id == buyer.Id);
        Assert.DoesNotContain(buyerContext.Companies, quote => quote.CompanyId == haltedCompany.Id);
        Assert.All(buyerContext.Companies, quote => Assert.Null(quote.BestExecutableSellPrice));
        Assert.Equal(400m, buyerContext.HoldingsValue);
        Assert.Equal(5_400m, buyerContext.NetWorth);
        Assert.Equal(10_400m, buyerContext.BuyingPower);
    }

    [Fact]
    public async Task ReservedOpenBuysReduceIndividualHeadroomInTheDecisionFlow()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var industry = await context.Industries.SingleAsync();
        var company = await context.Companies.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        var buyer = await context.Participants.SingleAsync(participant => participant.Name == "Bob");
        buyer.ReservedBalance = 3_500m;
        company.IssuedSharesCount = 10_000;

        var control = new Participant
        {
            Name = "Unreserved Control",
            Type = ParticipantType.Individual,
            Temperament = buyer.Temperament,
            RiskProfile = buyer.RiskProfile,
            InitialBalance = buyer.InitialBalance,
            CurrentBalance = buyer.CurrentBalance,
            SettledCashBalance = buyer.SettledCashBalance,
            IsActive = true,
        };
        var signalTrader = new Participant
        {
            Name = "Signal Player",
            Type = ParticipantType.Player,
            CurrentBalance = 100_000m,
            SettledCashBalance = 100_000m,
            IsActive = true,
        };
        context.Participants.AddRange(control, signalTrader);

        var secondCompany = new Company
        {
            Name = "Second",
            IndustryId = industry.Id,
            IssuedSharesCount = 100,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Companies.Add(secondCompany);
        await context.SaveChangesAsync();
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = secondCompany.Id,
            Price = 100m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        context.Orders.AddRange(
            new Order
            {
                // The resting bid supplies a strong demand signal without competing for the executable ask, so
                // reservation headroom remains the only behavioral difference between the two Individuals.
                ParticipantId = signalTrader.Id,
                CompanyId = company.Id,
                Type = OrderType.Buy,
                Status = OrderStatus.Open,
                Quantity = 1_000,
                LimitPrice = 80m,
                ReservedCashAmount = 80_000m,
                CreatedInCycleId = cycle.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new Order
            {
                ParticipantId = buyer.Id,
                CompanyId = secondCompany.Id,
                Type = OrderType.Buy,
                Status = OrderStatus.Open,
                Quantity = 35,
                LimitPrice = 100m,
                ReservedCashAmount = 3_500m,
                CreatedInCycleId = cycle.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            IssuerSell(company.Id, cycle.Id, quantity: 100, price: 100m));
        await context.SaveChangesAsync();

        var decisionEngine = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ZeroRandom());
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            decisionEngine,
            new MarketCycleLock(),
            new Random(1));

        var result = await service.GenerateDecisionsAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.OrdersPlaced);
        var controlOrder = await context.Orders.SingleAsync(order => order.ParticipantId == control.Id);
        Assert.Equal(company.Id, controlOrder.CompanyId);
        Assert.Equal(1, controlOrder.Quantity);
        Assert.Equal(100m, controlOrder.LimitPrice);
        Assert.DoesNotContain(
            await context.Orders.Where(order => order.ParticipantId == buyer.Id).ToListAsync(),
            order => order.CompanyId == company.Id);
    }

    [Fact]
    public async Task DecisionBatchAllocatesOneBestAskAcrossIndividuals()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        context.Orders.Add(IssuerSell(company.Id, cycle.Id, quantity: 5, price: 100m));
        await context.SaveChangesAsync();

        var engine = new ExecutableSupplyConsumingDecisionEngine(company.Id);
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            engine,
            new MarketCycleLock(),
            new Random(1));

        var result = await service.GenerateDecisionsAsync();

        Assert.True(result.Success);
        Assert.Equal([5, 0], engine.SeenExecutableQuantities);
        Assert.Equal(1, result.OrdersPlaced);
        Assert.Equal(
            5,
            await context.Orders
                .Where(order => order.ParticipantId != null && order.Type == OrderType.Buy)
                .SumAsync(order => order.Quantity));
        Assert.Equal(5, (await context.Orders.SingleAsync(order => order.ParticipantId != null)).Quantity);
    }

    [Fact]
    public async Task DecisionBatchStopsIndividualsAfterTheFirstAllocatedAskLevel()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        var third = AutomatedIndividual("Third Trader");
        var fourth = AutomatedIndividual("Fourth Trader");
        context.Participants.AddRange(third, fourth);
        context.Orders.AddRange(
            IssuerSell(company.Id, cycle.Id, quantity: 2, price: 100m),
            IssuerSell(company.Id, cycle.Id, quantity: 3, price: 100m),
            IssuerSell(company.Id, cycle.Id, quantity: 4, price: 105m));
        await context.SaveChangesAsync();

        var engine = new MultiLevelSupplyConsumingDecisionEngine(company.Id);
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            engine,
            new MarketCycleLock(),
            new Random(1));

        var result = await service.GenerateDecisionsAsync();

        Assert.True(result.Success);
        Assert.Equal(
            [(100m, 5), (100m, 2), ((decimal?)null, 0), ((decimal?)null, 0)],
            engine.SeenExecutableLevels);
        Assert.Equal(2, result.OrdersPlaced);
        var individualBuys = await context.Orders
            .Where(order => order.ParticipantId != null && order.Type == OrderType.Buy)
            .ToListAsync();
        Assert.Equal(5, individualBuys.Where(order => order.LimitPrice == 100m).Sum(order => order.Quantity));
        Assert.DoesNotContain(individualBuys, order => order.LimitPrice == 105m);

        var issuerSells = await context.Orders
            .Where(order => order.ParticipantId == null && order.Type == OrderType.Sell)
            .ToListAsync();
        Assert.Equal(5, issuerSells.Where(order => order.LimitPrice == 100m).Sum(order => order.Quantity));
        Assert.Equal(4, issuerSells.Where(order => order.LimitPrice == 105m).Sum(order => order.Quantity));
        Assert.All(issuerSells, order =>
        {
            Assert.Equal(0, order.FilledQuantity);
            Assert.Equal(OrderStatus.Open, order.Status);
        });
    }

    [Fact]
    public async Task SameBatchIndividualAllocationsPreserveMatchingPricePriority()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        var first = await context.Participants.SingleAsync(participant => participant.Name == "Alice");
        var second = await context.Participants.SingleAsync(participant => participant.Name == "Bob");
        var third = AutomatedIndividual("Passive Third Trader");
        context.Participants.Add(third);
        var firstAsk = IssuerSell(company.Id, cycle.Id, quantity: 5, price: 100m);
        var higherAsk = IssuerSell(company.Id, cycle.Id, quantity: 4, price: 105m);
        context.Orders.AddRange(firstAsk, higherAsk);
        await context.SaveChangesAsync();

        var engine = new PricePriorityDecisionEngine(company.Id);
        var matchingEngine = new MatchingEngine(context);
        var service = new MarketService(
            context,
            matchingEngine,
            engine,
            new MarketCycleLock(),
            new Random(1));

        var generated = await service.GenerateDecisionsAsync();
        var fillCount = await matchingEngine.RunAsync(cycle);
        await context.SaveChangesAsync();

        Assert.True(generated.Success);
        Assert.Equal(3, generated.OrdersPlaced);
        Assert.Equal(2, fillCount);
        var firstBuy = await context.Orders.SingleAsync(order => order.ParticipantId == first.Id);
        var secondBuy = await context.Orders.SingleAsync(order => order.ParticipantId == second.Id);
        var passiveBuy = await context.Orders.SingleAsync(order => order.ParticipantId == third.Id);
        Assert.Equal(firstBuy.Quantity, firstBuy.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, firstBuy.Status);
        Assert.Equal(secondBuy.Quantity, secondBuy.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, secondBuy.Status);
        Assert.Equal(99m, passiveBuy.LimitPrice);
        Assert.Equal(0, passiveBuy.FilledQuantity);
        Assert.Equal(OrderStatus.Open, passiveBuy.Status);
        Assert.Equal(OrderStatus.Filled, firstAsk.Status);
        Assert.Equal(5, firstAsk.FilledQuantity);
        Assert.Equal(OrderStatus.Open, higherAsk.Status);
        Assert.Equal(0, higherAsk.FilledQuantity);
    }

    [Fact]
    public async Task RealIndividualBatchDoesNotTurnAHiddenHigherAskIntoACrossingPassiveBid()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        var firstBuyer = await context.Participants.SingleAsync(participant => participant.Name == "Bob");
        firstBuyer.InitialBalance = 10_000m;
        firstBuyer.CurrentBalance = 10_000m;
        firstBuyer.SettledCashBalance = 10_000m;
        company.IssuedSharesCount = 2_000;
        var laterBuyer = AutomatedIndividual("Later Buyer");
        laterBuyer.InitialBalance = 20_000m;
        laterBuyer.CurrentBalance = 20_000m;
        laterBuyer.SettledCashBalance = 20_000m;
        context.Participants.Add(laterBuyer);
        var firstAsk = IssuerSell(company.Id, cycle.Id, quantity: 2, price: 100m);
        var higherAsk = IssuerSell(company.Id, cycle.Id, quantity: 2, price: 102m);
        context.Orders.AddRange(firstAsk, higherAsk);
        await context.SaveChangesAsync();

        var decisionRandom = new QueuedDecisionRandom([0d, 0d, 0d, 0.25d]);
        var decisionEngine = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            decisionRandom);
        var matchingEngine = new MatchingEngine(context);
        var service = new MarketService(
            context,
            matchingEngine,
            decisionEngine,
            new MarketCycleLock(),
            new Random(1));

        var generated = await service.GenerateDecisionsAsync();
        await matchingEngine.RunAsync(cycle);
        await context.SaveChangesAsync();

        Assert.True(generated.Success);
        Assert.Equal(1, generated.OrdersPlaced);
        Assert.Empty(await context.Orders.Where(order => order.ParticipantId == laterBuyer.Id).ToListAsync());
        var firstBuy = await context.Orders.SingleAsync(order => order.ParticipantId == firstBuyer.Id);
        Assert.Equal(2, firstBuy.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, firstBuy.Status);
        Assert.Equal(OrderStatus.Filled, firstAsk.Status);
        Assert.Equal(2, firstAsk.FilledQuantity);
        Assert.Equal(OrderStatus.Open, higherAsk.Status);
        Assert.Equal(0, higherAsk.FilledQuantity);
        Assert.Equal(1, decisionRandom.RemainingDoubleDraws);
    }

    [Fact]
    public async Task ExhaustedSingleAskBlocksALaterIndividualPassiveBid()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        var firstBuyer = await context.Participants.SingleAsync(participant => participant.Name == "Bob");
        firstBuyer.InitialBalance = 10_000m;
        firstBuyer.CurrentBalance = 10_000m;
        firstBuyer.SettledCashBalance = 10_000m;
        company.IssuedSharesCount = 2_000;
        var laterBuyer = AutomatedIndividual("Later Buyer");
        laterBuyer.InitialBalance = 20_000m;
        laterBuyer.CurrentBalance = 20_000m;
        laterBuyer.SettledCashBalance = 20_000m;
        context.Participants.Add(laterBuyer);
        var ask = IssuerSell(company.Id, cycle.Id, quantity: 2, price: 100m);
        context.Orders.Add(ask);
        await context.SaveChangesAsync();

        var decisionRandom = new QueuedDecisionRandom([0d, 0d, 0d, 0.25d]);
        var decisionEngine = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            decisionRandom);
        var matchingEngine = new MatchingEngine(context);
        var service = new MarketService(
            context,
            matchingEngine,
            decisionEngine,
            new MarketCycleLock(),
            new Random(1));

        var generated = await service.GenerateDecisionsAsync();
        await matchingEngine.RunAsync(cycle);
        await context.SaveChangesAsync();

        Assert.True(generated.Success);
        Assert.Equal(1, generated.OrdersPlaced);
        Assert.Empty(await context.Orders.Where(order => order.ParticipantId == laterBuyer.Id).ToListAsync());
        var firstBuy = await context.Orders.SingleAsync(order => order.ParticipantId == firstBuyer.Id);
        Assert.Equal(2, firstBuy.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, firstBuy.Status);
        Assert.Equal(OrderStatus.Filled, ask.Status);
        Assert.Equal(2, ask.FilledQuantity);
        Assert.Equal(1, decisionRandom.RemainingDoubleDraws);
    }

    [Fact]
    public async Task PreExistingCrossingBuyThatExhaustsAskBlocksAnIndividualPassiveBid()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        company.IssuedSharesCount = 2_000;
        var priorityBuyer = new Participant
        {
            Name = "Priority Buyer",
            Type = ParticipantType.Player,
            InitialBalance = 10_000m,
            CurrentBalance = 10_000m,
            SettledCashBalance = 10_000m,
            ReservedBalance = 200m,
            IsActive = true,
        };
        context.Participants.Add(priorityBuyer);
        await context.SaveChangesAsync();
        var priorityBuy = OpenBuy(priorityBuyer.Id, company.Id, cycle.Id, quantity: 2, price: 100m);
        var ask = IssuerSell(company.Id, cycle.Id, quantity: 2, price: 100m);
        context.Orders.AddRange(priorityBuy, ask);
        await context.SaveChangesAsync();

        var decisionRandom = new QueuedDecisionRandom([0.50d, 0d, 0.25d]);
        var decisionEngine = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            decisionRandom);
        var matchingEngine = new MatchingEngine(context);
        var service = new MarketService(
            context,
            matchingEngine,
            decisionEngine,
            new MarketCycleLock(),
            new Random(1));

        var generated = await service.GenerateDecisionsAsync();
        await matchingEngine.RunAsync(cycle);
        await context.SaveChangesAsync();

        Assert.True(generated.Success);
        Assert.Equal(0, generated.OrdersPlaced);
        Assert.Empty(await context.Orders
            .Where(order => order.Type == OrderType.Buy
                && order.ParticipantId != null
                && order.ParticipantId != priorityBuyer.Id)
            .ToListAsync());
        Assert.Equal(2, priorityBuy.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, priorityBuy.Status);
        Assert.Equal(OrderStatus.Filled, ask.Status);
        Assert.Equal(2, ask.FilledQuantity);
        Assert.Equal(1, decisionRandom.RemainingDoubleDraws);
    }

    [Fact]
    public async Task LaterIndividualCannotJumpTheLowestPriorityBuyThatConsumedAskSupply()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        company.IssuedSharesCount = 2_000;
        var highPriorityBuyer = new Participant
        {
            Name = "High Priority Buyer",
            Type = ParticipantType.Player,
            InitialBalance = 10_000m,
            CurrentBalance = 10_000m,
            SettledCashBalance = 10_000m,
            ReservedBalance = 110m,
            IsActive = true,
        };
        var lowPriorityBuyer = new Participant
        {
            Name = "Low Priority Buyer",
            Type = ParticipantType.Player,
            InitialBalance = 10_000m,
            CurrentBalance = 10_000m,
            SettledCashBalance = 10_000m,
            ReservedBalance = 100m,
            IsActive = true,
        };
        context.Participants.AddRange(highPriorityBuyer, lowPriorityBuyer);
        await context.SaveChangesAsync();
        var highPriorityBuy = OpenBuy(highPriorityBuyer.Id, company.Id, cycle.Id, quantity: 1, price: 110m);
        var lowPriorityBuy = OpenBuy(lowPriorityBuyer.Id, company.Id, cycle.Id, quantity: 1, price: 100m);
        var firstAsk = IssuerSell(company.Id, cycle.Id, quantity: 2, price: 100m);
        var higherAsk = IssuerSell(company.Id, cycle.Id, quantity: 1, price: 105m);
        context.Orders.AddRange(highPriorityBuy, lowPriorityBuy, firstAsk, higherAsk);
        await context.SaveChangesAsync();

        var decisionRandom = new QueuedDecisionRandom([0.50d, 0d]);
        var decisionEngine = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            decisionRandom);
        var matchingEngine = new MatchingEngine(context);
        var service = new MarketService(
            context,
            matchingEngine,
            decisionEngine,
            new MarketCycleLock(),
            new Random(1));

        var generated = await service.GenerateDecisionsAsync();
        await matchingEngine.RunAsync(cycle);
        await context.SaveChangesAsync();

        Assert.True(generated.Success);
        Assert.Equal(0, generated.OrdersPlaced);
        Assert.Empty(await context.Orders
            .Where(order => order.Type == OrderType.Buy
                && order.ParticipantId != highPriorityBuyer.Id
                && order.ParticipantId != lowPriorityBuyer.Id)
            .ToListAsync());
        Assert.Equal(OrderStatus.Filled, highPriorityBuy.Status);
        Assert.Equal(1, highPriorityBuy.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, lowPriorityBuy.Status);
        Assert.Equal(1, lowPriorityBuy.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, firstAsk.Status);
        Assert.Equal(2, firstAsk.FilledQuantity);
        Assert.Equal(OrderStatus.Open, higherAsk.Status);
        Assert.Equal(0, higherAsk.FilledQuantity);
        Assert.Equal(0, decisionRandom.RemainingDoubleDraws);
    }

    [Fact]
    public async Task PreExistingCrossingBuysReserveAskLevelsBeforeIndividualDecisions()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        var player = new Participant
        {
            Name = "Resting Buyer",
            Type = ParticipantType.Player,
            CurrentBalance = 100_000m,
            SettledCashBalance = 100_000m,
            IsActive = true,
        };
        context.Participants.Add(player);
        await context.SaveChangesAsync();

        var highPriorityPartial = OpenBuy(player.Id, company.Id, cycle.Id, quantity: 6, price: 110m);
        highPriorityPartial.Status = OrderStatus.PartiallyFilled;
        highPriorityPartial.FilledQuantity = 2;
        context.Orders.AddRange(
            IssuerSell(company.Id, cycle.Id, quantity: 3, price: 100m),
            IssuerSell(company.Id, cycle.Id, quantity: 4, price: 108m),
            highPriorityPartial,
            OpenBuy(player.Id, company.Id, cycle.Id, quantity: 4, price: 105m),
            OpenBuy(player.Id, company.Id, cycle.Id, quantity: 100, price: 90m),
            OpenBuy(player.Id, company.Id, cycle.Id, quantity: 100, price: 120m));
        await context.SaveChangesAsync();

        var engine = new ContextCapturingDecisionEngine();
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            engine,
            new MarketCycleLock(),
            new Random(1));

        await service.GenerateDecisionsAsync();

        var individualQuotes = engine.Contexts
            .Where(candidate => candidate.Participant.Type == ParticipantType.Individual)
            .Select(candidate => candidate.Companies.Single(quote => quote.CompanyId == company.Id))
            .ToList();
        Assert.NotEmpty(individualQuotes);
        Assert.All(individualQuotes, quote =>
        {
            Assert.Equal(108m, quote.BestExecutableSellPrice);
            Assert.Equal(3, quote.BestExecutableSellQuantity);
        });
        Assert.Equal(
            7,
            await context.Orders
                .Where(order => order.ParticipantId == null && order.Type == OrderType.Sell)
                .SumAsync(order => order.Quantity));
        Assert.Equal(
            0,
            await context.Orders
                .Where(order => order.ParticipantId == null && order.Type == OrderType.Sell)
                .SumAsync(order => order.FilledQuantity));
    }

    [Fact]
    public async Task EarlierFundCrossingBuyConsumesAskLevelsBeforeLaterIndividualContext()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        var founder = await context.Participants.FirstAsync();
        var fundParticipant = new Participant
        {
            Name = "Legacy Fund",
            Type = ParticipantType.CollectiveFund,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 10_000m,
            CurrentBalance = 10_000m,
            SettledCashBalance = 10_000m,
            IsActive = true,
        };
        var laterIndividual = AutomatedIndividual("Later Individual");
        context.Participants.AddRange(fundParticipant, laterIndividual);
        await context.SaveChangesAsync();
        context.CollectiveFunds.Add(new CollectiveFund
        {
            ParticipantId = fundParticipant.Id,
            FoundedByParticipantId = founder.Id,
            Status = CollectiveFundStatus.Active,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        context.Orders.AddRange(
            IssuerSell(company.Id, cycle.Id, quantity: 3, price: 100m),
            IssuerSell(company.Id, cycle.Id, quantity: 4, price: 105m));
        await context.SaveChangesAsync();

        var engine = new FundThenIndividualDecisionEngine(fundParticipant.Id, laterIndividual.Id, company.Id);
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            engine,
            new MarketCycleLock(),
            new Random(1));

        var result = await service.GenerateDecisionsAsync();

        Assert.True(result.Success);
        Assert.Equal((100m, 3), engine.FundExecutableLevel);
        Assert.Equal((105m, 2), engine.LaterIndividualExecutableLevel);
        Assert.Equal(1, result.OrdersPlaced);
        var fundOrder = await context.Orders.SingleAsync(order => order.ParticipantId == fundParticipant.Id);
        Assert.Equal(5, fundOrder.Quantity);
        Assert.Equal(105m, fundOrder.LimitPrice);
        var issuerSells = await context.Orders
            .Where(order => order.ParticipantId == null && order.Type == OrderType.Sell)
            .ToListAsync();
        Assert.Equal(7, issuerSells.Sum(order => order.Quantity));
        Assert.All(issuerSells, order =>
        {
            Assert.Equal(0, order.FilledQuantity);
            Assert.Equal(OrderStatus.Open, order.Status);
        });
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

    private sealed class ContextCapturingDecisionEngine : IDecisionEngine
    {
        public List<DecisionContext> Contexts { get; } = [];

        public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
        {
            Contexts.Add(context);
            return [];
        }
    }

    private sealed class ExecutableSupplyConsumingDecisionEngine(int companyId) : IDecisionEngine
    {
        public List<int> SeenExecutableQuantities { get; } = [];

        public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
        {
            var quote = context.Companies.Single(candidate => candidate.CompanyId == companyId);
            SeenExecutableQuantities.Add(quote.BestExecutableSellQuantity);
            return quote.BestExecutableSellPrice is decimal price && quote.BestExecutableSellQuantity > 0
                ? [new OrderIntent(OrderType.Buy, companyId, quote.BestExecutableSellQuantity, price)]
                : [];
        }
    }

    private sealed class MultiLevelSupplyConsumingDecisionEngine(int companyId) : IDecisionEngine
    {
        private int decisionIndex;

        public List<(decimal? Price, int Quantity)> SeenExecutableLevels { get; } = [];

        public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
        {
            var quote = context.Companies.Single(candidate => candidate.CompanyId == companyId);
            SeenExecutableLevels.Add((quote.BestExecutableSellPrice, quote.BestExecutableSellQuantity));
            var quantity = decisionIndex++ == 0
                ? Math.Min(3, quote.BestExecutableSellQuantity)
                : quote.BestExecutableSellQuantity;
            return quote.BestExecutableSellPrice is decimal price && quantity > 0
                ? [new OrderIntent(OrderType.Buy, companyId, quantity, price)]
                : [];
        }
    }

    private sealed class PricePriorityDecisionEngine(int companyId) : IDecisionEngine
    {
        private int decisionIndex;

        public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
        {
            var quote = context.Companies.Single(candidate => candidate.CompanyId == companyId);
            return decisionIndex++ switch
            {
                0 => [new OrderIntent(OrderType.Buy, companyId, 3, 100m)],
                1 => [new OrderIntent(OrderType.Buy, companyId, 2, 100m)],
                _ when quote.BestExecutableSellPrice is decimal price =>
                    [new OrderIntent(OrderType.Buy, companyId, quote.BestExecutableSellQuantity, price)],
                _ => [new OrderIntent(OrderType.Buy, companyId, 1, 99m)],
            };
        }
    }

    private sealed class FundThenIndividualDecisionEngine(int fundId, int individualId, int companyId) : IDecisionEngine
    {
        public (decimal? Price, int Quantity) FundExecutableLevel { get; private set; }

        public (decimal? Price, int Quantity) LaterIndividualExecutableLevel { get; private set; }

        public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
        {
            var quote = context.Companies.Single(candidate => candidate.CompanyId == companyId);
            if (context.Participant.Id == fundId)
            {
                FundExecutableLevel = (quote.BestExecutableSellPrice, quote.BestExecutableSellQuantity);
                return [new OrderIntent(OrderType.Buy, companyId, 5, 105m)];
            }

            if (context.Participant.Id == individualId)
            {
                LaterIndividualExecutableLevel = (quote.BestExecutableSellPrice, quote.BestExecutableSellQuantity);
            }

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

    private static Order IssuerSell(int companyId, int cycleId, int quantity, decimal price) => new()
    {
        CompanyId = companyId,
        Type = OrderType.Sell,
        Status = OrderStatus.Open,
        Quantity = quantity,
        LimitPrice = price,
        CreatedInCycleId = cycleId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Order OpenBuy(int participantId, int companyId, int cycleId, int quantity, decimal price) => new()
    {
        ParticipantId = participantId,
        CompanyId = companyId,
        Type = OrderType.Buy,
        Status = OrderStatus.Open,
        Quantity = quantity,
        LimitPrice = price,
        ReservedCashAmount = quantity * price,
        CreatedInCycleId = cycleId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Participant AutomatedIndividual(string name) => new()
    {
        Name = name,
        Type = ParticipantType.Individual,
        Temperament = Temperament.Balanced,
        RiskProfile = RiskProfile.Medium,
        InitialBalance = 10_000m,
        CurrentBalance = 10_000m,
        SettledCashBalance = 10_000m,
        IsActive = true,
    };

    private sealed class ZeroRandom : Random
    {
        public override double NextDouble() => 0d;

        public override int Next(int maxValue) => 0;
    }

    private sealed class QueuedDecisionRandom(IEnumerable<double> doubles) : Random
    {
        private readonly Queue<double> doubles = new(doubles);

        public int RemainingDoubleDraws => doubles.Count;

        public override double NextDouble() => doubles.Dequeue();

        public override int Next(int maxValue) => 0;
    }
}
