using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Splits and merges are deterministic (no random draws): a company at or above the high threshold splits
// 4-for-1 (cap and worth unchanged), and one below the low threshold merges 4-to-1, flooring fractional
// holdings while lifting the per-share price.
public sealed class StockSplitServiceTests : IDisposable
{
    private const decimal Threshold = 1000m;
    private const decimal MergeThreshold = 5m;
    private const int Ratio = 4;

    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public StockSplitServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private StockSplitService Service(bool enabled) =>
        new(context, Options.Create(new StockSplitOptions { Enabled = enabled }));

    [Fact]
    public async Task DisabledDoesNotSplit()
    {
        var (cycle, company) = await SeedAsync(price: 1200m);

        await Service(enabled: false).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(1000, refreshed.IssuedSharesCount);
        Assert.Equal(1, await context.PriceSnapshots.CountAsync(snapshot => snapshot.CompanyId == company.Id));
    }

    [Fact]
    public async Task BelowThresholdDoesNotSplit()
    {
        var (cycle, company) = await SeedAsync(price: Threshold - 1m);

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(1000, refreshed.IssuedSharesCount);
        Assert.Equal(1, await context.PriceSnapshots.CountAsync(snapshot => snapshot.CompanyId == company.Id));
    }

    [Fact]
    public async Task SplitPreservesHolderWorthAndMarketCap()
    {
        var (cycle, company) = await SeedAsync(price: 1200m);
        var holder = await AddParticipantAsync(balance: 10_000m);
        await AddHoldingAsync(holder.Id, company.Id, quantity: 100, averageCost: 800m);

        var worthBefore = 100 * 1200m;
        var capBefore = company.IssuedSharesCount * 1200m;

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedCompany = await context.Companies.AsNoTracking().SingleAsync();
        var newPrice = await LatestPriceAsync(company.Id);
        var refreshedHolding = await context.Holdings.AsNoTracking().SingleAsync(holding => holding.ParticipantId == holder.Id);

        Assert.Equal(4000, refreshedCompany.IssuedSharesCount);
        Assert.Equal(300m, newPrice);
        Assert.Equal(400, refreshedHolding.Quantity);
        Assert.Equal(200m, refreshedHolding.AverageCost);
        // Cap and holder worth are unchanged — only the denomination shrank.
        Assert.Equal(capBefore, refreshedCompany.IssuedSharesCount * newPrice);
        Assert.Equal(worthBefore, refreshedHolding.Quantity * newPrice);
    }

    [Fact]
    public async Task SplitCancelsParticipantOrdersAndRedenominatesTheFloat()
    {
        var (cycle, company) = await SeedAsync(price: 1200m);

        // Issuer float, a participant sell, and a participant buy with reserved cash.
        AddOrder(company.Id, participantId: null, OrderType.Sell, quantity: 860, filled: 0, limit: 1000m, reserved: 0m, cycle.Id);
        var seller = await AddParticipantAsync(balance: 10_000m);
        await AddHoldingAsync(seller.Id, company.Id, quantity: 100, averageCost: 1000m);
        AddOrder(company.Id, seller.Id, OrderType.Sell, quantity: 40, filled: 0, limit: 1160m, reserved: 0m, cycle.Id);
        var buyer = await AddParticipantAsync(balance: 100_000m, reserved: 12_500m);
        AddOrder(company.Id, buyer.Id, OrderType.Buy, quantity: 10, filled: 0, limit: 1250m, reserved: 12_500m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // The float keeps standing so the unsold supply stays listed, only re-denominated.
        var floatOrder = await context.Orders.AsNoTracking().SingleAsync(order => order.ParticipantId == null);
        Assert.Equal(OrderStatus.Open, floatOrder.Status);
        Assert.Equal(3440, floatOrder.Quantity);
        Assert.Equal(250m, floatOrder.LimitPrice);

        // The participant sell is cancelled outright.
        var sell = await context.Orders.AsNoTracking().SingleAsync(order => order.ParticipantId == seller.Id);
        Assert.Equal(OrderStatus.Cancelled, sell.Status);

        // The participant buy is cancelled and its reservation released back to the owner's balance.
        var buy = await context.Orders.AsNoTracking().SingleAsync(order => order.ParticipantId == buyer.Id);
        Assert.Equal(OrderStatus.Cancelled, buy.Status);
        Assert.Equal(0m, buy.ReservedCashAmount);
        var refreshedBuyer = await context.Participants.AsNoTracking().SingleAsync(participant => participant.Id == buyer.Id);
        Assert.Equal(0m, refreshedBuyer.ReservedBalance);
        Assert.True(await context.MoneyTransactions.AnyAsync(money =>
            money.ParticipantId == buyer.Id && money.Type == MoneyTransactionType.Release && money.Amount == 12_500m));
    }

    [Fact]
    public async Task SplitCancelsThePlayersOrders()
    {
        var (cycle, company) = await SeedAsync(price: 1200m);
        var player = await AddParticipantAsync(balance: 100_000m, reserved: 12_500m, type: ParticipantType.Player);
        AddOrder(company.Id, player.Id, OrderType.Buy, quantity: 10, filled: 0, limit: 1250m, reserved: 12_500m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // The player is not exempt from a split: its order is cancelled and its reservation released.
        var buy = await context.Orders.AsNoTracking().SingleAsync(order => order.ParticipantId == player.Id);
        Assert.Equal(OrderStatus.Cancelled, buy.Status);
        Assert.Equal(0m, buy.ReservedCashAmount);
        var refreshedPlayer = await context.Participants.AsNoTracking().SingleAsync(participant => participant.Id == player.Id);
        Assert.Equal(0m, refreshedPlayer.ReservedBalance);
    }

    [Fact]
    public async Task SplitPostsAnImpactFreeNews()
    {
        var (cycle, company) = await SeedAsync(price: 1200m);

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var news = await context.NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal(NewsImpactScope.None, news.Scope);
        Assert.Null(news.Direction);
        Assert.Null(news.ImpactPercent);
        Assert.Null(news.TargetCompanyId);
        Assert.Equal(cycle.Id, news.PublishedInCycleId);
        Assert.Contains(company.Name, news.Title);
        Assert.Equal(0, await context.NewsPostIndustries.CountAsync());
    }

    [Fact]
    public async Task OverflowGuardSkipsSplitAtTheShareCeiling()
    {
        var (cycle, company) = await SeedAsync(price: 1200m, issuedShares: 300_000_000);

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(300_000_000, refreshed.IssuedSharesCount);
        Assert.Equal(1, await context.PriceSnapshots.CountAsync(snapshot => snapshot.CompanyId == company.Id));
    }

    [Fact]
    public async Task AtMergeThresholdDoesNotMerge()
    {
        var (cycle, company) = await SeedAsync(price: MergeThreshold);

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(1000, refreshed.IssuedSharesCount);
        Assert.Equal(1, await context.PriceSnapshots.CountAsync(snapshot => snapshot.CompanyId == company.Id));
    }

    [Fact]
    public async Task MergePreservesHolderWorthAndMarketCapWhenDivisible()
    {
        var (cycle, company) = await SeedAsync(price: 4m);
        var holder = await AddParticipantAsync(balance: 10_000m);
        await AddHoldingAsync(holder.Id, company.Id, quantity: 100, averageCost: 200m);

        var worthBefore = 100 * 4m;
        var capBefore = company.IssuedSharesCount * 4m;

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedCompany = await context.Companies.AsNoTracking().SingleAsync();
        var newPrice = await LatestPriceAsync(company.Id);
        var refreshedHolding = await context.Holdings.AsNoTracking().SingleAsync(holding => holding.ParticipantId == holder.Id);

        // 100 held + 900 implicit float, both divide cleanly: 25 held + 225 float = 250 issued.
        Assert.Equal(250, refreshedCompany.IssuedSharesCount);
        Assert.Equal(16m, newPrice);
        Assert.Equal(25, refreshedHolding.Quantity);
        Assert.Equal(800m, refreshedHolding.AverageCost);
        Assert.Equal(capBefore, refreshedCompany.IssuedSharesCount * newPrice);
        Assert.Equal(worthBefore, refreshedHolding.Quantity * newPrice);
    }

    [Fact]
    public async Task MergeFloorsFractionalHoldingsAndDropsTheRemainder()
    {
        var (cycle, company) = await SeedAsync(price: 4m);
        var holder = await AddParticipantAsync(balance: 10_000m);
        await AddHoldingAsync(holder.Id, company.Id, quantity: 10, averageCost: 200m);

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var newPrice = await LatestPriceAsync(company.Id);
        var refreshedHolding = await context.Holdings.AsNoTracking().SingleAsync(holding => holding.ParticipantId == holder.Id);
        var refreshedCompany = await context.Companies.AsNoTracking().SingleAsync();

        // 10 floors to 2 (the half-share is dropped, not paid out), so worth ticks down from 40 to 32.
        Assert.Equal(2, refreshedHolding.Quantity);
        Assert.Equal(800m, refreshedHolding.AverageCost);
        Assert.Equal(16m, newPrice);
        Assert.True(refreshedHolding.Quantity * newPrice < 10 * 4m);
        // 2 held + floor(990 / 4) = 2 + 247 = 249 issued, so the implicit float stays consistent.
        Assert.Equal(249, refreshedCompany.IssuedSharesCount);
    }

    [Fact]
    public async Task MergeCancelsParticipantOrdersAndRedenominatesTheFloat()
    {
        var (cycle, company) = await SeedAsync(price: 4m);

        AddOrder(company.Id, participantId: null, OrderType.Sell, quantity: 860, filled: 0, limit: 4m, reserved: 0m, cycle.Id);
        var seller = await AddParticipantAsync(balance: 10_000m);
        await AddHoldingAsync(seller.Id, company.Id, quantity: 100, averageCost: 4m);
        AddOrder(company.Id, seller.Id, OrderType.Sell, quantity: 40, filled: 0, limit: 4.4m, reserved: 0m, cycle.Id);
        var buyer = await AddParticipantAsync(balance: 100_000m, reserved: 45m);
        AddOrder(company.Id, buyer.Id, OrderType.Buy, quantity: 10, filled: 0, limit: 4.5m, reserved: 45m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // The float keeps standing, only re-denominated: quantity divided, limit multiplied.
        var floatOrder = await context.Orders.AsNoTracking().SingleAsync(order => order.ParticipantId == null);
        Assert.Equal(OrderStatus.Open, floatOrder.Status);
        Assert.Equal(215, floatOrder.Quantity);
        Assert.Equal(16m, floatOrder.LimitPrice);

        var sell = await context.Orders.AsNoTracking().SingleAsync(order => order.ParticipantId == seller.Id);
        Assert.Equal(OrderStatus.Cancelled, sell.Status);

        var buy = await context.Orders.AsNoTracking().SingleAsync(order => order.ParticipantId == buyer.Id);
        Assert.Equal(OrderStatus.Cancelled, buy.Status);
        Assert.Equal(0m, buy.ReservedCashAmount);
        var refreshedBuyer = await context.Participants.AsNoTracking().SingleAsync(participant => participant.Id == buyer.Id);
        Assert.Equal(0m, refreshedBuyer.ReservedBalance);
        Assert.True(await context.MoneyTransactions.AnyAsync(money =>
            money.ParticipantId == buyer.Id && money.Type == MoneyTransactionType.Release && money.Amount == 45m));
    }

    [Fact]
    public async Task MergeCancelsThePlayersOrders()
    {
        var (cycle, company) = await SeedAsync(price: 4m);
        var player = await AddParticipantAsync(balance: 100_000m, reserved: 45m, type: ParticipantType.Player);
        AddOrder(company.Id, player.Id, OrderType.Buy, quantity: 10, filled: 0, limit: 4.5m, reserved: 45m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var buy = await context.Orders.AsNoTracking().SingleAsync(order => order.ParticipantId == player.Id);
        Assert.Equal(OrderStatus.Cancelled, buy.Status);
        Assert.Equal(0m, buy.ReservedCashAmount);
        var refreshedPlayer = await context.Participants.AsNoTracking().SingleAsync(participant => participant.Id == player.Id);
        Assert.Equal(0m, refreshedPlayer.ReservedBalance);
    }

    [Fact]
    public async Task MergePostsAnImpactFreeNews()
    {
        var (cycle, company) = await SeedAsync(price: 4m);

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var news = await context.NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal(NewsImpactScope.None, news.Scope);
        Assert.Null(news.Direction);
        Assert.Null(news.ImpactPercent);
        Assert.Null(news.TargetCompanyId);
        Assert.Contains(company.Name, news.Title);
        Assert.Equal(0, await context.NewsPostIndustries.CountAsync());
    }

    [Fact]
    public async Task FloorGuardSkipsMergeAtTheShareFloor()
    {
        var (cycle, company) = await SeedAsync(price: 4m, issuedShares: 60);

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(60, refreshed.IssuedSharesCount);
        Assert.Equal(1, await context.PriceSnapshots.CountAsync(snapshot => snapshot.CompanyId == company.Id));
    }

    private async Task<(MarketCycle Cycle, Company Company)> SeedAsync(decimal price, int issuedShares = 1000)
    {
        var now = DateTime.UtcNow;

        var cycle = new MarketCycle { CycleNumber = 100, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);

        var market = new Market { Name = "Demo Market", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(market);

        var industry = new Industry { Name = "Tech" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();

        var company = new Company
        {
            Name = "Acme",
            IndustryId = industry.Id,
            IssuedSharesCount = issuedShares,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();

        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = price,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });

        market.CurrentCycleId = cycle.Id;
        await context.SaveChangesAsync();
        return (cycle, company);
    }

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

    private async Task AddHoldingAsync(int participantId, int companyId, int quantity, decimal averageCost)
    {
        context.Holdings.Add(new Holding
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Quantity = quantity,
            AverageCost = averageCost,
        });
        await context.SaveChangesAsync();
    }

    private void AddOrder(int companyId, int? participantId, OrderType type, int quantity, int filled, decimal limit, decimal reserved, int cycleId)
    {
        var now = DateTime.UtcNow;
        context.Orders.Add(new Order
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Type = type,
            Status = OrderStatus.Open,
            Quantity = quantity,
            FilledQuantity = filled,
            LimitPrice = limit,
            ReservedCashAmount = reserved,
            CreatedInCycleId = cycleId,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private async Task<decimal> LatestPriceAsync(int companyId) =>
        (await context.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == companyId)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync())
        .Price;

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
