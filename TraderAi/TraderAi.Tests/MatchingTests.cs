using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class MatchingTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly MarketService marketService;

    public MatchingTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
        var loanOptions = Options.Create(new LoanOptions { Enabled = true });
        var marginService = new MarginService(context, Options.Create(new MarginOptions()));
        marketService = new MarketService(
            context,
            new MatchingEngine(context, marginService: marginService),
            new DeterministicDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            loanService: new LoanService(context, loanOptions),
            loanOptions: loanOptions,
            marginService: marginService);
    }

    [Fact]
    public async Task FullMatchUsesMidpointPriceAndTransfersSharesAndCash()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        // Buy 110 crosses sell 100; execution is the 105 midpoint regardless of which order rested first.
        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 110m);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 5, 100m);

        var result = await marketService.AdvanceCycleAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.FillCount);

        await context.Entry(seed.Buyer).ReloadAsync();
        await context.Entry(seed.Seller).ReloadAsync();

        Assert.Equal(4475m, seed.Buyer.CurrentBalance);
        Assert.Equal(0m, seed.Buyer.ReservedBalance);
        Assert.Equal(1525m, seed.Seller.CurrentBalance);

        Assert.Equal(5, await context.Holdings.Where(holding => holding.ParticipantId == seed.Buyer.Id).SumAsync(holding => holding.Quantity));
        Assert.Equal(5, await context.Holdings.Where(holding => holding.ParticipantId == seed.Seller.Id).SumAsync(holding => holding.Quantity));

        var transaction = await context.ShareTransactions.SingleAsync();
        Assert.Equal(5, transaction.Quantity);
        Assert.Equal(105m, transaction.Price);
        Assert.Equal(100m, transaction.SellerAverageCost);
        Assert.Equal(500m, transaction.SellerCostBasis);
        Assert.Equal(0m, transaction.SellerTradeFee);
        Assert.Equal(0m, transaction.SellerManagerFee);
        Assert.Equal(25m, transaction.SellerGrossRealizedPnl);
        Assert.Equal(25m, transaction.SellerNetRealizedPnl);

        var latestSnapshot = await context.PriceSnapshots.OrderByDescending(snapshot => snapshot.Id).FirstAsync();
        Assert.Equal(105m, latestSnapshot.Price);
    }

    [Fact]
    public async Task TradeCreditRecordsBuyerAsSourceAndDebitHasNone()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 110m);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 5, 100m);

        await marketService.AdvanceCycleAsync();

        // The seller's proceeds came from the buyer, so the credit records the buyer as the source.
        var credit = await context.MoneyTransactions
            .SingleAsync(money => money.ParticipantId == seed.Seller.Id && money.Type == MoneyTransactionType.Credit);
        Assert.Equal(seed.Buyer.Id, credit.FromWhomId);
        Assert.False(string.IsNullOrWhiteSpace(credit.Description));

        // The buyer's payment is an outflow, so nobody sent it money: no participant source.
        var debit = await context.MoneyTransactions
            .SingleAsync(money => money.ParticipantId == seed.Buyer.Id && money.Type == MoneyTransactionType.Debit);
        Assert.Null(debit.FromWhomId);
        Assert.False(string.IsNullOrWhiteSpace(debit.Description));
    }

    [Fact]
    public async Task MidpointMatchReleasesUnusedBuyReservation()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        // Buyer reserves at its 110 limit but pays the 105 midpoint, so the 5/share overbid is released.
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 5, 100m);
        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 110m);

        var result = await marketService.AdvanceCycleAsync();

        Assert.Equal(1, result.FillCount);

        await context.Entry(seed.Buyer).ReloadAsync();

        Assert.Equal(4475m, seed.Buyer.CurrentBalance);
        Assert.Equal(0m, seed.Buyer.ReservedBalance);

        var releaseTotal = await context.MoneyTransactions
            .Where(money => money.ParticipantId == seed.Buyer.Id && money.Type == MoneyTransactionType.Release)
            .SumAsync(money => money.Amount);
        Assert.Equal(25m, releaseTotal);
    }

    [Fact]
    public async Task ExecutionPriceIsTheMidpointOfCrossingLimits()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 3, 100m);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 3, 90m);

        await marketService.AdvanceCycleAsync();

        var transaction = await context.ShareTransactions.SingleAsync();
        Assert.Equal(95m, transaction.Price);

        var latestSnapshot = await context.PriceSnapshots.OrderByDescending(snapshot => snapshot.Id).FirstAsync();
        Assert.Equal(95m, latestSnapshot.Price);
    }

    [Theory]
    [InlineData(110, 100)]
    [InlineData(104, 96)]
    [InlineData(101, 100)]
    public async Task MirroredCrossingLimitsPreserveTheDocumentedMidpointRule(
        int buyLimit,
        int sellLimit)
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 1, buyLimit);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 1, sellLimit);

        var result = await marketService.AdvanceCycleAsync();

        Assert.Equal(1, result.FillCount);
        var expectedMidpoint = Math.Round((buyLimit + sellLimit) / 2m, 2, MidpointRounding.AwayFromZero);
        var transaction = await context.ShareTransactions.SingleAsync();
        Assert.Equal(expectedMidpoint, transaction.Price);
        var latestSnapshot = await context.PriceSnapshots.OrderByDescending(snapshot => snapshot.Id).FirstAsync();
        Assert.Equal(expectedMidpoint, latestSnapshot.Price);
    }

    [Fact]
    public async Task PartialFillLeavesRemainderOpenWithRemainingReservation()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        var buyResult = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 10, 100m);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 4, 100m);

        await marketService.AdvanceCycleAsync();

        var buyOrder = await context.Orders.FindAsync(buyResult.Order!.Id);
        Assert.Equal(OrderStatus.PartiallyFilled, buyOrder!.Status);
        Assert.Equal(4, buyOrder.FilledQuantity);
        Assert.Equal(600m, buyOrder.ReservedCashAmount);

        await context.Entry(seed.Buyer).ReloadAsync();
        Assert.Equal(4600m, seed.Buyer.CurrentBalance);
        Assert.Equal(600m, seed.Buyer.ReservedBalance);
    }

    [Fact]
    public async Task NoMatchWhenBuyPriceBelowSellPrice()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        var buyResult = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 90m);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 5, 100m);

        var result = await marketService.AdvanceCycleAsync();

        Assert.Equal(0, result.FillCount);
        Assert.Equal(0, await context.ShareTransactions.CountAsync());

        var buyOrder = await context.Orders.FindAsync(buyResult.Order!.Id);
        Assert.Equal(OrderStatus.Open, buyOrder!.Status);

        await context.Entry(seed.Buyer).ReloadAsync();
        Assert.Equal(450m, seed.Buyer.ReservedBalance);
    }

    [Fact]
    public async Task HighestBuyPriceIsMatchedFirst()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);
        var secondBuyer = await AddParticipantAsync("Carol", cash: 5000m);

        var lowBuy = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 105m);
        var highBuy = await marketService.PlaceOrderAsync(secondBuyer.Id, seed.Company.Id, OrderType.Buy, 5, 110m);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 5, 100m);

        await marketService.AdvanceCycleAsync();

        var highBuyOrder = await context.Orders.FindAsync(highBuy.Order!.Id);
        var lowBuyOrder = await context.Orders.FindAsync(lowBuy.Order!.Id);

        Assert.Equal(OrderStatus.Filled, highBuyOrder!.Status);
        Assert.Equal(OrderStatus.Open, lowBuyOrder!.Status);
        Assert.Equal(5, await context.Holdings.Where(holding => holding.ParticipantId == secondBuyer.Id).SumAsync(holding => holding.Quantity));
    }

    [Fact]
    public async Task BuyOrderReservesCashOnPlacement()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 100m);

        await context.Entry(seed.Buyer).ReloadAsync();
        Assert.Equal(500m, seed.Buyer.ReservedBalance);
        Assert.Equal(4500m, seed.Buyer.AvailableBalance);

        var reserve = await context.MoneyTransactions.SingleAsync(money => money.Type == MoneyTransactionType.Reserve);
        Assert.Equal(500m, reserve.Amount);
    }

    [Fact]
    public async Task BuyOrderRejectedWhenCashIsInsufficient()
    {
        // A 50% initial requirement gives 400 cash exactly 800 of buying power.
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 400m, sellerShares: 10, sharePrice: 100m);

        var result = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 9, 100m);

        Assert.False(result.Success);
        Assert.Equal(0, await context.Orders.CountAsync());
    }

    [Fact]
    public async Task SellOrderRejectedWhenSharesAreNotOwned()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 3, sharePrice: 100m);

        var result = await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 5, 100m);

        Assert.False(result.Success);
        Assert.Equal(0, await context.Orders.CountAsync());
    }

    [Fact]
    public async Task SharesInAnOpenSellOrderCannotBeSoldAgain()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 5, sharePrice: 100m);

        var first = await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 5, 100m);
        Assert.True(first.Success);

        var second = await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 1, 100m);
        Assert.False(second.Success);
    }

    [Fact]
    public async Task BuyOrderAllowedWithinInitialMarginBuyingPower()
    {
        // At a 50% initial requirement, 1,000 cash provides 2,000 of total buying power.
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 1000m, sellerShares: 12, sharePrice: 100m);

        var result = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 12, 100m);

        Assert.True(result.Success);

        await context.Entry(seed.Buyer).ReloadAsync();
        Assert.Equal(1200m, seed.Buyer.ReservedBalance);
        Assert.Equal(-200m, seed.Buyer.AvailableBalance);
    }

    [Fact]
    public async Task BuyOrderRejectedBeyondInitialMarginBuyingPower()
    {
        // A 50% initial requirement gives 1,000 cash exactly 2,000 of buying power.
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 1000m, sellerShares: 15, sharePrice: 100m);

        var result = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 21, 100m);

        Assert.False(result.Success);
        Assert.Equal(0, await context.Orders.CountAsync());
    }

    [Fact]
    public async Task MarginFillIncreasesAccountDebitWithoutOpeningATermLoan()
    {
        var seed = await SeedAsync(sellerCash: 100000m, buyerCash: 1000m, sellerShares: 12, sharePrice: 100m);

        // Buyer spends 1200 against 1000 cash: the 200 shortfall becomes account-level margin debit.
        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 12, 100m);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 12, 100m);
        await marketService.AdvanceCycleAsync();

        await context.Entry(seed.Buyer).ReloadAsync();
        Assert.Equal(0m, seed.Buyer.CurrentBalance);
        Assert.Equal(200m, (await context.MarginAccounts.SingleAsync(candidate => candidate.ParticipantId == seed.Buyer.Id)).DebitBalance);
        Assert.Empty(await context.Loans.ToListAsync());
        Assert.Equal(200m, (await context.MoneyTransactions.SingleAsync(money => money.Type == MoneyTransactionType.MarginAdvance)).Amount);
    }

    [Fact]
    public async Task HaltedCompanyDoesNotMatch()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        // Orders rest while the company is open, then it is frozen before matching runs.
        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 110m);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 5, 100m);
        context.PriceBandStates.Add(new PriceBandState
        {
            CompanyId = seed.Company.Id,
            State = LuldState.TradingPause,
            ReferencePrice = 100m,
            LowerBandPrice = 95m,
            UpperBandPrice = 105m,
            PauseUntilCycleNumber = 151,
            UpdatedInCycleId = (await context.MarketCycles.SingleAsync()).Id,
        });
        await context.SaveChangesAsync();

        var result = await marketService.AdvanceCycleAsync();

        Assert.Equal(0, result.FillCount);
        Assert.Equal(0, await context.ShareTransactions.CountAsync());
    }

    [Fact]
    public async Task OrderRejectedForHaltedCompany()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);
        context.PriceBandStates.Add(new PriceBandState
        {
            CompanyId = seed.Company.Id,
            State = LuldState.LimitState,
            ReferencePrice = 100m,
            LowerBandPrice = 95m,
            UpperBandPrice = 105m,
            LimitStateStartedCycleNumber = 1,
            UpdatedInCycleId = (await context.MarketCycles.SingleAsync()).Id,
        });
        await context.SaveChangesAsync();

        var result = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 110m);

        Assert.False(result.Success);
        Assert.Equal(0, await context.Orders.CountAsync());
    }

    [Fact]
    public async Task OppositeOrderFromSameParticipantIsRejectedBeforeReservation()
    {
        var seed = await SeedAsync(sellerCash: 2_000m, buyerCash: 5_000m, sellerShares: 10, sharePrice: 100m);
        var sell = await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 2, 100m);

        var buy = await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Buy, 2, 110m);

        Assert.True(sell.Success);
        Assert.False(buy.Success);
        Assert.Contains("open sell order", buy.Error, StringComparison.OrdinalIgnoreCase);
        await context.Entry(seed.Seller).ReloadAsync();
        Assert.Equal(0m, seed.Seller.ReservedBalance);
        Assert.Single(await context.Orders.ToListAsync());
    }

    [Fact]
    public async Task LegacySelfCrossCancelsNewerBuyAndReleasesReservationOnce()
    {
        var seed = await SeedAsync(sellerCash: 2_000m, buyerCash: 5_000m, sellerShares: 10, sharePrice: 100m);
        var cycle = await context.MarketCycles.SingleAsync();
        var sell = new Order
        {
            ParticipantId = seed.Seller.Id,
            CompanyId = seed.Company.Id,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = 5,
            LimitPrice = 100m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow.AddSeconds(-1),
            UpdatedAt = DateTime.UtcNow.AddSeconds(-1),
        };
        var buy = new Order
        {
            ParticipantId = seed.Seller.Id,
            CompanyId = seed.Company.Id,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 5,
            LimitPrice = 110m,
            ReservedCashAmount = 550m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        seed.Seller.ReservedBalance = 550m;
        context.Orders.AddRange(sell, buy);
        await context.SaveChangesAsync();
        var initialPriceCount = await context.PriceSnapshots.CountAsync();

        var fillCount = await new MatchingEngine(context).RunAsync(cycle);
        await context.SaveChangesAsync();

        Assert.Equal(0, fillCount);
        Assert.Equal(OrderStatus.Open, sell.Status);
        Assert.Equal(OrderStatus.Cancelled, buy.Status);
        Assert.Equal(0m, buy.ReservedCashAmount);
        Assert.Equal(0m, seed.Seller.ReservedBalance);
        Assert.Empty(await context.ShareTransactions.ToListAsync());
        Assert.Empty(await context.OrderFills.ToListAsync());
        Assert.Equal(initialPriceCount, await context.PriceSnapshots.CountAsync());
        var release = await context.MoneyTransactions.SingleAsync(transaction => transaction.Type == MoneyTransactionType.Release);
        Assert.Equal(550m, release.Amount);
    }

    [Fact]
    public async Task ClosedCompanyRejectsBuyAndSellOrders()
    {
        var seed = await SeedAsync(sellerCash: 2_000m, buyerCash: 5_000m, sellerShares: 10, sharePrice: 100m);
        var cycle = await context.MarketCycles.SingleAsync();
        seed.Company.ClosedInCycleId = cycle.Id;
        await context.SaveChangesAsync();

        var buy = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 1, 100m);
        var sell = await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 1, 100m);

        Assert.False(buy.Success);
        Assert.False(sell.Success);
        Assert.Contains("delisted", buy.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("delisted", sell.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await context.Orders.ToListAsync());
        Assert.Empty(await context.MarginAccounts.ToListAsync());
    }

    [Fact]
    public async Task LegacyCycleWithoutTradingDayMatchesWithoutCreatingSettlementInstruction()
    {
        var seed = await SeedAsync(sellerCash: 1_000m, buyerCash: 5_000m, sellerShares: 10, sharePrice: 100m);
        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 1, 100m);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 1, 100m);
        var settlement = new SettlementService(context, Options.Create(new SettlementOptions()));
        var cycle = await context.MarketCycles.SingleAsync();

        var fills = await new MatchingEngine(context, settlementService: settlement).RunAsync(cycle);
        await context.SaveChangesAsync();

        Assert.Equal(1, fills);
        Assert.Empty(await context.SettlementInstructions.ToListAsync());
    }

    [Fact]
    public async Task MatchingExcludesAValidWaitingOuterSellEvenWhenABidWouldCrossIt()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);
        context.PriceBandStates.Add(new PriceBandState
        {
            CompanyId = seed.Company.Id,
            State = LuldState.Normal,
            ReferencePrice = 100m,
            LowerBandPrice = 85m,
            UpperBandPrice = 110m,
        });
        await context.SaveChangesAsync();

        // The sell rests at 80: inside the 75-115 allowed range but below the 85 active band, so it waits.
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 5, 80m);
        var buy = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 100m);

        var result = await marketService.AdvanceCycleAsync();

        Assert.Equal(0, result.FillCount);
        Assert.Empty(await context.ShareTransactions.ToListAsync());
        var restingBuy = await context.Orders.FindAsync(buy.Order!.Id);
        Assert.Equal(OrderStatus.Open, restingBuy!.Status);
    }

    [Fact]
    public async Task HoldingNewOrdersDefersMatchingUntilALaterCycle()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);
        var firstCycle = await context.MarketCycles.SingleAsync();

        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 100m);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 5, 100m);

        // Both orders belong to the cycle being matched, so holding new orders leaves them resting untouched.
        var heldFills = await new MatchingEngine(context).RunAsync(firstCycle, holdOrdersCreatedThisCycle: true);
        await context.SaveChangesAsync();

        Assert.Equal(0, heldFills);
        Assert.Empty(await context.ShareTransactions.ToListAsync());

        // A later cycle sees them as prior-cycle orders and crosses them, even with the hold still applied.
        var nextCycle = new MarketCycle { CycleNumber = 2, Status = CycleStatus.Running, StartedAt = DateTime.UtcNow };
        context.MarketCycles.Add(nextCycle);
        await context.SaveChangesAsync();

        var nextFills = await new MatchingEngine(context).RunAsync(nextCycle, holdOrdersCreatedThisCycle: true);
        await context.SaveChangesAsync();

        Assert.Equal(1, nextFills);
        Assert.Single(await context.ShareTransactions.ToListAsync());
    }

    private async Task<SeedResult> SeedAsync(decimal sellerCash, decimal buyerCash, int sellerShares, decimal sharePrice)
    {
        var now = DateTime.UtcNow;

        var cycle = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);

        var market = new Market { Name = "Test Market", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(market);

        var company = new Company { Name = "Test Co", IssuedSharesCount = sellerShares, CreatedAt = now, UpdatedAt = now };
        context.Companies.Add(company);

        var seller = new Participant
        {
            Name = "Seller",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = sellerCash,
            CurrentBalance = sellerCash,
            IsActive = true,
        };
        var buyer = new Participant
        {
            Name = "Buyer",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Aggressive,
            RiskProfile = RiskProfile.High,
            InitialBalance = buyerCash,
            CurrentBalance = buyerCash,
            IsActive = true,
        };
        context.Participants.Add(seller);
        context.Participants.Add(buyer);

        await context.SaveChangesAsync();

        if (sellerShares > 0)
        {
            context.Holdings.Add(new Holding
            {
                ParticipantId = seller.Id,
                CompanyId = company.Id,
                Quantity = sellerShares,
                AverageCost = sharePrice,
            });
        }

        // A listed company always carries a price snapshot; without one, order entry has no reference to bound against.
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = sharePrice,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });

        market.CurrentCycleId = cycle.Id;
        await context.SaveChangesAsync();

        return new SeedResult(market, company, seller, buyer);
    }

    private async Task<Participant> AddParticipantAsync(string name, decimal cash)
    {
        var participant = new Participant
        {
            Name = name,
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = cash,
            CurrentBalance = cash,
            IsActive = true,
        };
        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        return participant;
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed record SeedResult(Market Market, Company Company, Participant Seller, Participant Buyer);
}
