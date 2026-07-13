using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class LuldStateMachineTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly TradingClockService clock;

    public LuldStateMachineTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        clock = new TradingClockService(context, Options.Create(new TradingClockOptions { TradingCycleSeconds = 2 }));
    }

    private VolatilityHaltService Service() => new(
        context,
        Options.Create(new VolatilityHaltOptions
        {
            Enabled = true,
            ReferenceWindowSeconds = 300,
            LimitStateDurationSeconds = 15,
            TradingPauseDurationSeconds = 300,
            UpperBandPercent = 5m,
            LowerBandPercent = 5m,
        }),
        clock);

    [Fact]
    public async Task ReferenceAndBandsUseEligibleTradesInsideRollingWindow()
    {
        var seed = await SeedAsync(201);
        AddTrade(seed, 1, 50m);
        AddTrade(seed, 100, 90m);
        AddTrade(seed, 200, 110m);
        await context.SaveChangesAsync();

        await Service().ProcessForCycleAsync(seed.CycleIds[201], 201, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var state = await context.PriceBandStates.SingleAsync();
        Assert.Equal(100m, state.ReferencePrice);
        Assert.Equal(95m, state.LowerBandPrice);
        Assert.Equal(105m, state.UpperBandPrice);
        Assert.Equal(LuldState.Normal, state.State);
    }

    [Fact]
    public async Task EightLimitCyclesStartA150CyclePauseWithoutCancellingOrders()
    {
        var seed = await SeedAsync(20);
        var buyer = await AddParticipantAsync("Buyer", 10_000m, reserved: 1_050m);
        context.Orders.Add(new Order
        {
            ParticipantId = buyer.Id,
            CompanyId = seed.Company.Id,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 10,
            LimitPrice = 105m,
            ReservedCashAmount = 1_050m,
            CreatedInCycleId = seed.CycleIds[1],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        context.Orders.Add(new Order
        {
            ParticipantId = null,
            CompanyId = seed.Company.Id,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = 10,
            LimitPrice = 105m,
            CreatedInCycleId = seed.CycleIds[1],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        for (var cycle = 1; cycle <= 8; cycle++)
        {
            await Service().ProcessForCycleAsync(seed.CycleIds[cycle], cycle, DateTime.UtcNow);
        }
        await context.SaveChangesAsync();

        var state = await context.PriceBandStates.SingleAsync();
        Assert.Equal(LuldState.TradingPause, state.State);
        Assert.Equal(158, state.PauseUntilCycleNumber);
        Assert.Equal(2, await context.Orders.CountAsync(order => order.Status == OrderStatus.Open));
        Assert.Equal(1_050m, (await context.Participants.SingleAsync()).ReservedBalance);
        Assert.False(await context.MoneyTransactions.AnyAsync(transaction => transaction.Type == MoneyTransactionType.Release));
    }

    [Fact]
    public async Task ReopeningAuctionUsesLowerPriceAsFinalTieBreaker()
    {
        var seed = await SeedAsync(1);
        var buyer = await AddParticipantAsync("Buyer", 10_000m, reserved: 1_010m);
        var seller = await AddParticipantAsync("Seller", 0m);
        context.Holdings.Add(new Holding
        {
            ParticipantId = seller.Id,
            CompanyId = seed.Company.Id,
            Quantity = 10,
            SettledQuantity = 10,
            AverageCost = 100m,
        });
        context.PriceBandStates.Add(new PriceBandState
        {
            CompanyId = seed.Company.Id,
            State = LuldState.Reopening,
            ReferencePrice = 100m,
            LowerBandPrice = 90m,
            UpperBandPrice = 110m,
            UpdatedInCycleId = seed.CycleIds[1],
        });
        AddOrder(seed, buyer.Id, OrderType.Buy, 10, 101m, 1_010m);
        AddOrder(seed, seller.Id, OrderType.Sell, 10, 99m, 0m);
        await context.SaveChangesAsync();

        var fills = await new MatchingEngine(context).RunAsync(await context.MarketCycles.SingleAsync());
        await context.SaveChangesAsync();

        Assert.Equal(1, fills);
        Assert.Equal(99m, (await context.ShareTransactions.SingleAsync()).Price);
        Assert.Equal(LuldState.Normal, (await context.PriceBandStates.SingleAsync()).State);
    }

    [Fact]
    public async Task DirectImpactIsClampedToTheUpperBandAndPreservesPausedOrders()
    {
        var seed = await SeedAsync(1);
        var buyer = await AddParticipantAsync("Buyer", 10_000m, reserved: 1_000m);
        AddOrder(seed, buyer.Id, OrderType.Buy, 10, 100m, 1_000m);
        context.PriceBandStates.Add(new PriceBandState
        {
            CompanyId = seed.Company.Id,
            State = LuldState.TradingPause,
            ReferencePrice = 100m,
            LowerBandPrice = 95m,
            UpperBandPrice = 105m,
            PauseUntilCycleNumber = 151,
            UpdatedInCycleId = seed.CycleIds[1],
        });
        await context.SaveChangesAsync();

        await new MarketImpactService(context).ApplyImpactAsync(
            NewsImpactDirection.Increase, [seed.Company.Id], 50m, seed.CycleIds[1], DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(105m, await context.PriceSnapshots.OrderByDescending(snapshot => snapshot.Id).Select(snapshot => snapshot.Price).FirstAsync());
        Assert.Equal(OrderStatus.Open, (await context.Orders.SingleAsync()).Status);
        Assert.Equal(1_000m, (await context.Participants.SingleAsync()).ReservedBalance);
    }

    private async Task<Seed> SeedAsync(int cycleCount)
    {
        var cycles = Enumerable.Range(1, cycleCount)
            .Select(number => new MarketCycle { CycleNumber = number, Status = CycleStatus.Running, StartedAt = DateTime.UtcNow })
            .ToList();
        context.MarketCycles.AddRange(cycles);
        var company = new Company { Name = "Acme", IssuedSharesCount = 1_000, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = 100m,
            Capitalization = 100_000m,
            CreatedInCycleId = cycles[0].Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        return new Seed(company, cycles.ToDictionary(cycle => cycle.CycleNumber, cycle => cycle.Id));
    }

    private void AddTrade(Seed seed, int cycleNumber, decimal price) => context.ShareTransactions.Add(new ShareTransaction
    {
        BuyerId = 1,
        CompanyId = seed.Company.Id,
        Quantity = 1,
        Price = price,
        TotalCost = price,
        CreatedInCycleId = seed.CycleIds[cycleNumber],
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    });

    private async Task<Participant> AddParticipantAsync(string name, decimal balance, decimal reserved = 0m)
    {
        var participant = new Participant
        {
            Name = name,
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = balance,
            CurrentBalance = balance,
            SettledCashBalance = balance,
            ReservedBalance = reserved,
            IsActive = true,
        };
        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        return participant;
    }

    private void AddOrder(Seed seed, int participantId, OrderType type, int quantity, decimal price, decimal reserved) =>
        context.Orders.Add(new Order
        {
            ParticipantId = participantId,
            CompanyId = seed.Company.Id,
            Type = type,
            Status = OrderStatus.Open,
            Quantity = quantity,
            LimitPrice = price,
            ReservedCashAmount = reserved,
            CreatedInCycleId = seed.CycleIds[1],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed record Seed(Company Company, Dictionary<int, int> CycleIds);
}
