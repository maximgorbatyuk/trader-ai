using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// The volatility halt is deterministic (no random draws): a company whose price moved past the up or down
// band over the observation window is frozen for the halt duration, its whole book cancelled. The freeze is
// symmetric, and a company already halted or trading only on stale history is left alone.
public sealed class VolatilityHaltServiceTests : IDisposable
{
    private const int CurrentCycle = 10;

    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly Dictionary<int, int> cycleIdByNumber = new();
    private Market market = null!;
    private Company company = null!;

    public VolatilityHaltServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private VolatilityHaltService Service(
        bool enabled = true, int window = 3, int duration = 15, decimal up = 25m, decimal down = 40m) =>
        new(context, Options.Create(new VolatilityHaltOptions
        {
            Enabled = enabled,
            ObservationWindowCycles = window,
            HaltDurationCycles = duration,
            UpBandPercent = up,
            DownBandPercent = down,
        }));

    private Task Process(bool enabled = true) =>
        Service(enabled).ProcessForCycleAsync(cycleIdByNumber[CurrentCycle], CurrentCycle, DateTime.UtcNow);

    [Fact]
    public async Task DisabledDoesNotHalt()
    {
        await SeedAsync();
        AddSnapshot(6, 100m);
        AddSnapshot(9, 200m);
        await context.SaveChangesAsync();

        await Process(enabled: false);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Null(refreshed.TradingHaltedUntilCycleNumber);
    }

    [Fact]
    public async Task UpBreachFreezesForTheHaltDuration()
    {
        await SeedAsync();
        AddSnapshot(6, 100m);
        AddSnapshot(9, 200m); // +100% over the 3-cycle window, well past the 25% up band.
        await context.SaveChangesAsync();

        await Process();
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(CurrentCycle + 15, refreshed.TradingHaltedUntilCycleNumber);

        var news = await context.NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal(NewsImpactScope.None, news.Scope);
        Assert.Contains(company.Name, news.Title);
    }

    [Fact]
    public async Task DownBreachFreezes()
    {
        await SeedAsync();
        AddSnapshot(6, 100m);
        AddSnapshot(9, 40m); // -60% over the window, past the 40% down band.
        await context.SaveChangesAsync();

        await Process();
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(CurrentCycle + 15, refreshed.TradingHaltedUntilCycleNumber);
    }

    [Fact]
    public async Task MoveWithinBothBandsDoesNotHalt()
    {
        await SeedAsync();
        AddSnapshot(6, 100m);
        AddSnapshot(9, 110m); // +10% up, inside both bands.
        await context.SaveChangesAsync();

        await Process();
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Null(refreshed.TradingHaltedUntilCycleNumber);
    }

    [Fact]
    public async Task ADropSmallerThanTheDownBandDoesNotHalt()
    {
        await SeedAsync();
        AddSnapshot(6, 100m);
        AddSnapshot(9, 70m); // -30%, inside the looser 40% down band (a concentration cut must not self-trip).
        await context.SaveChangesAsync();

        await Process();
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Null(refreshed.TradingHaltedUntilCycleNumber);
    }

    [Fact]
    public async Task StaleHistoryDoesNotHalt()
    {
        await SeedAsync();
        AddSnapshot(1, 100m);
        AddSnapshot(3, 500m); // A huge move, but the latest close predates the window by more than 3 cycles.
        await context.SaveChangesAsync();

        await Process();
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Null(refreshed.TradingHaltedUntilCycleNumber);
    }

    [Fact]
    public async Task HaltCancelsEveryOrderIncludingThePlayerAndTheFloat()
    {
        await SeedAsync();
        AddSnapshot(6, 100m);
        AddSnapshot(9, 200m);

        AddOrder(participantId: null, OrderType.Sell, quantity: 500, limit: 100m, reserved: 0m);
        var seller = await AddParticipantAsync(balance: 10_000m);
        AddOrder(seller.Id, OrderType.Sell, quantity: 40, limit: 210m, reserved: 0m);
        var buyer = await AddParticipantAsync(balance: 100_000m, reserved: 2_000m);
        AddOrder(buyer.Id, OrderType.Buy, quantity: 10, limit: 200m, reserved: 2_000m);
        var player = await AddParticipantAsync(balance: 100_000m, reserved: 2_000m, type: ParticipantType.Player);
        AddOrder(player.Id, OrderType.Buy, quantity: 10, limit: 200m, reserved: 2_000m);
        await context.SaveChangesAsync();

        await Process();
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.Orders.CountAsync(order =>
            order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled));

        var refreshedBuyer = await context.Participants.AsNoTracking().SingleAsync(p => p.Id == buyer.Id);
        var refreshedPlayer = await context.Participants.AsNoTracking().SingleAsync(p => p.Id == player.Id);
        Assert.Equal(0m, refreshedBuyer.ReservedBalance);
        Assert.Equal(0m, refreshedPlayer.ReservedBalance);

        Assert.True(await context.MoneyTransactions.AnyAsync(money =>
            money.ParticipantId == player.Id && money.Type == MoneyTransactionType.Release && money.Amount == 2_000m));
    }

    [Fact]
    public async Task AnAlreadyHaltedCompanyIsNotReprocessed()
    {
        await SeedAsync();
        AddSnapshot(6, 100m);
        AddSnapshot(9, 200m); // Would breach, but the company is already frozen.
        company.TradingHaltedUntilCycleNumber = 20;
        AddOrder(participantId: null, OrderType.Sell, quantity: 500, limit: 100m, reserved: 0m);
        await context.SaveChangesAsync();

        await Process();
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(20, refreshed.TradingHaltedUntilCycleNumber);
        // The book is left intact — the existing halt already cleared it once.
        Assert.Equal(1, await context.Orders.CountAsync(order => order.Status == OrderStatus.Open));
        Assert.Equal(0, await context.NewsPosts.CountAsync());
    }

    private async Task SeedAsync()
    {
        var now = DateTime.UtcNow;
        for (var number = 1; number <= CurrentCycle; number++)
        {
            context.MarketCycles.Add(new MarketCycle { CycleNumber = number, Status = CycleStatus.Running, StartedAt = now });
        }

        var industry = new Industry { Name = "Tech" };
        context.Industries.Add(industry);
        market = new Market { Name = "Demo", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(market);
        await context.SaveChangesAsync();

        foreach (var cycle in await context.MarketCycles.ToListAsync())
        {
            cycleIdByNumber[cycle.CycleNumber] = cycle.Id;
        }

        company = new Company { Name = "Acme", IndustryId = industry.Id, IssuedSharesCount = 1000, CreatedAt = now, UpdatedAt = now };
        context.Companies.Add(company);
        await context.SaveChangesAsync();

        market.CurrentCycleId = cycleIdByNumber[CurrentCycle];
        await context.SaveChangesAsync();
    }

    private void AddSnapshot(int cycleNumber, decimal price) =>
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = price,
            CreatedInCycleId = cycleIdByNumber[cycleNumber],
            CreatedAt = DateTime.UtcNow,
        });

    private async Task<Participant> AddParticipantAsync(decimal balance, decimal reserved = 0m, ParticipantType type = ParticipantType.Individual)
    {
        var participant = new Participant
        {
            Name = "Trader",
            Type = type,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = balance,
            CurrentBalance = balance,
            ReservedBalance = reserved,
            IsActive = true,
        };
        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        return participant;
    }

    private void AddOrder(int? participantId, OrderType type, int quantity, decimal limit, decimal reserved)
    {
        var now = DateTime.UtcNow;
        context.Orders.Add(new Order
        {
            ParticipantId = participantId,
            CompanyId = company.Id,
            Type = type,
            Status = OrderStatus.Open,
            Quantity = quantity,
            FilledQuantity = 0,
            LimitPrice = limit,
            ReservedCashAmount = reserved,
            CreatedInCycleId = cycleIdByNumber[CurrentCycle],
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
