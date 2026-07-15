using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class StockDenominationIntegrationTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public StockDenominationIntegrationTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task FirstAcceptedOrderAndTradeAfterSplitUseTheNewPriceScale()
    {
        var seed = await SeedAsync(price: 1200m, sellerShares: 100);
        var market = Service();

        var splitTick = await market.RunCycleTickAsync();

        Assert.True(splitTick.Ran);
        var denominationEvent = await context.StockDenominationEvents.AsNoTracking().SingleAsync();
        Assert.Equal(StockDenominationActionType.Split, denominationEvent.ActionType);
        Assert.Equal(300m, (await context.PriceBandStates.AsNoTracking().SingleAsync()).ReferencePrice);

        var oldScaleOrder = await market.PlaceOrderAsync(
            seed.Buyer.Id, seed.Company.Id, OrderType.Buy, quantity: 1, limitPrice: 1200m);
        Assert.False(oldScaleOrder.Success);
        Assert.Contains("Limit price must be between", oldScaleOrder.Error);

        var firstAcceptedOrder = await market.PlaceOrderAsync(
            seed.Buyer.Id, seed.Company.Id, OrderType.Buy, quantity: 1, limitPrice: 315m);
        var crossingSell = await market.PlaceOrderAsync(
            seed.Seller.Id, seed.Company.Id, OrderType.Sell, quantity: 1, limitPrice: 285m);
        Assert.True(firstAcceptedOrder.Success);
        Assert.Equal(315m, firstAcceptedOrder.Order!.LimitPrice);
        Assert.True(crossingSell.Success);

        var tradingTick = await market.RunCycleTickAsync();

        Assert.Equal(1, tradingTick.FillCount);
        var firstPostActionTrade = await context.ShareTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.CreatedInCycleId != seed.PreviousCycle.Id);
        var state = await context.PriceBandStates.AsNoTracking().SingleAsync();
        Assert.Equal(300m, state.ReferencePrice);
        Assert.Equal(300m, firstPostActionTrade.Price);
        Assert.InRange(firstPostActionTrade.Price, state.LowerBandPrice, state.UpperBandPrice);
    }

    [Fact]
    public async Task FirstAcceptedOrderAndTradeAfterReverseSplitUseTheNewPriceScale()
    {
        var seed = await SeedAsync(price: 4m, sellerShares: 100);
        var market = Service();

        var reverseSplitTick = await market.RunCycleTickAsync();

        Assert.True(reverseSplitTick.Ran);
        var denominationEvent = await context.StockDenominationEvents.AsNoTracking().SingleAsync();
        Assert.Equal(StockDenominationActionType.ReverseSplit, denominationEvent.ActionType);
        Assert.Equal(16m, (await context.PriceBandStates.AsNoTracking().SingleAsync()).ReferencePrice);

        var oldScaleOrder = await market.PlaceOrderAsync(
            seed.Buyer.Id, seed.Company.Id, OrderType.Buy, quantity: 1, limitPrice: 4m);
        Assert.False(oldScaleOrder.Success);
        Assert.Contains("Limit price must be between", oldScaleOrder.Error);

        var firstAcceptedOrder = await market.PlaceOrderAsync(
            seed.Buyer.Id, seed.Company.Id, OrderType.Buy, quantity: 1, limitPrice: 16.8m);
        var crossingSell = await market.PlaceOrderAsync(
            seed.Seller.Id, seed.Company.Id, OrderType.Sell, quantity: 1, limitPrice: 15.2m);
        Assert.True(firstAcceptedOrder.Success);
        Assert.Equal(16.8m, firstAcceptedOrder.Order!.LimitPrice);
        Assert.True(crossingSell.Success);

        var tradingTick = await market.RunCycleTickAsync();

        Assert.Equal(1, tradingTick.FillCount);
        var firstPostActionTrade = await context.ShareTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.CreatedInCycleId != seed.PreviousCycle.Id);
        var state = await context.PriceBandStates.AsNoTracking().SingleAsync();
        Assert.Equal(16m, state.ReferencePrice);
        Assert.Equal(16m, firstPostActionTrade.Price);
        Assert.InRange(firstPostActionTrade.Price, state.LowerBandPrice, state.UpperBandPrice);
    }

    private MarketService Service()
    {
        var volatilityOptions = Options.Create(new VolatilityHaltOptions
        {
            Enabled = true,
            DemandRatchetStepPercent = 0m,
        });
        var clock = new TradingClockService(
            context,
            Options.Create(new TradingClockOptions { TradingCycleSeconds = 2 }));
        return new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            stockSplitService: new StockSplitService(
                context,
                Options.Create(new StockSplitOptions { Enabled = true })),
            volatilityHaltService: new VolatilityHaltService(context, volatilityOptions, clock),
            volatilityHaltOptions: volatilityOptions);
    }

    private async Task<Seed> SeedAsync(decimal price, int sellerShares)
    {
        var now = DateTime.UtcNow;
        var previousCycle = new MarketCycle
        {
            CycleNumber = 1,
            Status = CycleStatus.Completed,
            StartedAt = now.AddSeconds(-2),
            CompletedAt = now,
        };
        var currentCycle = new MarketCycle
        {
            CycleNumber = 2,
            Status = CycleStatus.Running,
            StartedAt = now,
        };
        context.MarketCycles.AddRange(previousCycle, currentCycle);
        var company = new Company
        {
            Name = "Acme",
            IssuedSharesCount = 1_000,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var seller = Participant("Seller", 10_000m);
        var buyer = Participant("Buyer", 100_000m);
        context.Companies.Add(company);
        context.Participants.AddRange(seller, buyer);
        await context.SaveChangesAsync();

        context.Holdings.Add(new Holding
        {
            ParticipantId = seller.Id,
            CompanyId = company.Id,
            Quantity = sellerShares,
            SettledQuantity = sellerShares,
            AverageCost = price,
        });
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = price,
            Capitalization = price * company.IssuedSharesCount,
            CreatedInCycleId = previousCycle.Id,
            CreatedAt = now,
        });
        context.ShareTransactions.Add(new ShareTransaction
        {
            SellerId = seller.Id,
            BuyerId = buyer.Id,
            CompanyId = company.Id,
            Quantity = 1,
            Price = price,
            TotalCost = price,
            CreatedInCycleId = previousCycle.Id,
            CreatedAt = now,
            UpdatedAt = now,
        });
        context.Markets.Add(new Market
        {
            Name = "Test Market",
            Status = MarketStatus.Running,
            CurrentCycleId = currentCycle.Id,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync();

        return new Seed(previousCycle, company, seller, buyer);
    }

    private static Participant Participant(string name, decimal balance) => new()
    {
        Name = name,
        Type = ParticipantType.Player,
        Temperament = Temperament.Balanced,
        RiskProfile = RiskProfile.Medium,
        InitialBalance = balance,
        CurrentBalance = balance,
        SettledCashBalance = balance,
        IsActive = true,
    };

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed record Seed(
        MarketCycle PreviousCycle,
        Company Company,
        Participant Seller,
        Participant Buyer);
}
