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
        marketService = new MarketService(
            context,
            new MatchingEngine(context),
            new DeterministicDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            loanService: new LoanService(context, loanOptions),
            loanOptions: loanOptions);
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

        var latestSnapshot = await context.PriceSnapshots.OrderByDescending(snapshot => snapshot.Id).FirstAsync();
        Assert.Equal(105m, latestSnapshot.Price);
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
        var highBuy = await marketService.PlaceOrderAsync(secondBuyer.Id, seed.Company.Id, OrderType.Buy, 5, 120m);
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
        // Worth is 400 cash, so the 40% cap is a 160 loan-principal ceiling. Buying 6 at 100 needs a 200
        // shortfall funded by a 230 loan (200 × 1.15), which overshoots the ceiling.
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 400m, sellerShares: 10, sharePrice: 100m);

        var result = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 6, 100m);

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
    public async Task BuyOrderAllowedIntoDebtWithinWorthFraction()
    {
        // Worth is 1000 cash, so the 40% cap is a 400 loan-principal ceiling. Reserving 1200 needs only a 230
        // loan (200 shortfall × 1.15), well inside the cap; the reservation still shows a -200 available balance.
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 1000m, sellerShares: 12, sharePrice: 100m);

        var result = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 12, 100m);

        Assert.True(result.Success);

        await context.Entry(seed.Buyer).ReloadAsync();
        Assert.Equal(1200m, seed.Buyer.ReservedBalance);
        Assert.Equal(-200m, seed.Buyer.AvailableBalance);
    }

    [Fact]
    public async Task BuyOrderRejectedBeyondDebtAllowance()
    {
        // Worth is 1000 cash → a 400 loan-principal ceiling. Reserving 1500 needs a 575 loan (500 shortfall ×
        // 1.15), which overshoots the ceiling.
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 1000m, sellerShares: 15, sharePrice: 100m);

        var result = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 15, 100m);

        Assert.False(result.Success);
        Assert.Equal(0, await context.Orders.CountAsync());
    }

    [Fact]
    public async Task MarginFillOpensLoanAndKeepsBalanceNonNegative()
    {
        var seed = await SeedAsync(sellerCash: 100000m, buyerCash: 1000m, sellerShares: 12, sharePrice: 100m);

        // Buyer spends 1200 against 1000 cash: the 200 shortfall becomes a loan for 200 × 1.15 = 230, and the
        // 30 cash buffer is left in the balance so it never goes negative.
        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 12, 100m);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 12, 100m);
        await marketService.AdvanceCycleAsync();

        await context.Entry(seed.Buyer).ReloadAsync();
        Assert.Equal(30m, seed.Buyer.CurrentBalance);

        var loan = await context.Loans.SingleAsync(candidate => candidate.ParticipantId == seed.Buyer.Id);
        Assert.Equal(LoanStatus.Open, loan.Status);
        Assert.Equal(230m, loan.Principal);
        Assert.Equal(230m, loan.RemainingPrincipal);

        var disbursement = await context.MoneyTransactions.SingleAsync(money => money.Type == MoneyTransactionType.LoanDisbursement);
        Assert.Equal(230m, disbursement.Amount);
        Assert.Equal(loan.Id, disbursement.RelatedLoanId);
    }

    [Fact]
    public async Task HaltedCompanyDoesNotMatch()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        // Orders rest while the company is open, then it is frozen before matching runs.
        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 110m);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 5, 100m);
        seed.Company.TradingHaltedUntilCycleNumber = 1;
        await context.SaveChangesAsync();

        var result = await marketService.AdvanceCycleAsync();

        Assert.Equal(0, result.FillCount);
        Assert.Equal(0, await context.ShareTransactions.CountAsync());
    }

    [Fact]
    public async Task OrderRejectedForHaltedCompany()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);
        seed.Company.TradingHaltedUntilCycleNumber = 1;
        await context.SaveChangesAsync();

        var result = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 110m);

        Assert.False(result.Success);
        Assert.Equal(0, await context.Orders.CountAsync());
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
