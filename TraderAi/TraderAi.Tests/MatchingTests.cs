using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
        marketService = new MarketService(context, new MatchingEngine(context), new DeterministicDecisionEngine(), new MarketCycleLock());
    }

    [Fact]
    public async Task FullMatchUsesOlderBuyPriceAndTransfersSharesAndCash()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        // Buy is placed first, so it is the older order and sets the execution price.
        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 110m);
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 5, 100m);

        var result = await marketService.AdvanceCycleAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.FillCount);

        await context.Entry(seed.Buyer).ReloadAsync();
        await context.Entry(seed.Seller).ReloadAsync();

        Assert.Equal(4450m, seed.Buyer.CurrentBalance);
        Assert.Equal(0m, seed.Buyer.ReservedBalance);
        Assert.Equal(1550m, seed.Seller.CurrentBalance);

        Assert.Equal(5, await context.Shares.CountAsync(share => share.OwnerId == seed.Buyer.Id));
        Assert.Equal(5, await context.Shares.CountAsync(share => share.OwnerId == seed.Seller.Id));

        var transaction = await context.ShareTransactions.SingleAsync();
        Assert.Equal(5, transaction.Quantity);
        Assert.Equal(110m, transaction.Price);

        var latestSnapshot = await context.PriceSnapshots.OrderByDescending(snapshot => snapshot.Id).FirstAsync();
        Assert.Equal(110m, latestSnapshot.Price);
    }

    [Fact]
    public async Task OlderSellPriceWinsAndReleasesUnusedBuyReservation()
    {
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        // Sell is placed first, so the cheaper sell price executes and the buyer gets cash released.
        await marketService.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 5, 100m);
        await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 110m);

        var result = await marketService.AdvanceCycleAsync();

        Assert.Equal(1, result.FillCount);

        await context.Entry(seed.Buyer).ReloadAsync();

        Assert.Equal(4500m, seed.Buyer.CurrentBalance);
        Assert.Equal(0m, seed.Buyer.ReservedBalance);

        var releaseTotal = await context.MoneyTransactions
            .Where(money => money.ParticipantId == seed.Buyer.Id && money.Type == MoneyTransactionType.Release)
            .SumAsync(money => money.Amount);
        Assert.Equal(50m, releaseTotal);
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
        Assert.Equal(5, await context.Shares.CountAsync(share => share.OwnerId == secondBuyer.Id));
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
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 400m, sellerShares: 10, sharePrice: 100m);

        var result = await marketService.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, 100m);

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

        for (var index = 0; index < sellerShares; index++)
        {
            context.Shares.Add(new Share
            {
                CompanyId = company.Id,
                OwnerId = seller.Id,
                InitialPrice = sharePrice,
                CurrentPrice = sharePrice,
                LastUpdatedAt = now,
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
