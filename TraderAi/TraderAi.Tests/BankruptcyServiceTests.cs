using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the bankruptcy roll with a scripted Random so the wealth ramp and the forced sell-down are forced
// deterministically. The gate consumes one draw per share-wealthy trader (in id order); a trader that
// fires then draws once more to pick its headline template. A trader below the wealth line draws nothing.
public sealed class BankruptcyServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public BankruptcyServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private BankruptcyService Service(bool enabled, Random random) =>
        new(context, Options.Create(new BankruptcyOptions { Enabled = enabled }), random);

    [Fact]
    public async Task DisabledDoesNotTrigger()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 1_500_000_000m);
        await AddSharesAsync(trader.Id, company.Id, count: 10, price: 100m);

        await Service(enabled: false, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.Bankruptcies.CountAsync());
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == trader.Id);
        Assert.False(refreshed.IsBankrupt);
        Assert.Equal(0, refreshed.WealthyCycles);
    }

    [Fact]
    public async Task TraderBelowTheWealthLineDoesNotTrigger()
    {
        var (_, cycle, company) = await SeedAsync(price: 100_000_000m);
        var trader = await AddTraderAsync(currentBalance: 5_000_000_000m);
        await AddSharesAsync(trader.Id, company.Id, count: 5, price: 100_000_000m);

        // Cash no longer counts: despite 5B in cash, the ~500M of share holdings sits well below the
        // two-billion line, so the ramp stays at zero and no draw is taken.
        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.Bankruptcies.CountAsync());
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == trader.Id);
        Assert.False(refreshed.IsBankrupt);
        Assert.Equal(0, refreshed.WealthyCycles);
    }

    [Fact]
    public async Task WealthyTraderStaysSafeDuringTheOpeningWindow()
    {
        var (_, cycle, company) = await SeedAsync(price: 200_000_000m, cycleNumber: 10);
        var trader = await AddTraderAsync(currentBalance: 0m);
        await AddSharesAsync(trader.Id, company.Id, count: 10, price: 200_000_000m);

        // Share holdings worth 2B would otherwise put the trader at risk, but inside the 500-cycle protection
        // window no roll is taken, so the wealth ramp never even starts.
        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.Bankruptcies.CountAsync());
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == trader.Id);
        Assert.False(refreshed.IsBankrupt);
        Assert.Equal(0, refreshed.WealthyCycles);
    }

    [Fact]
    public async Task WealthyTraderGoesBankruptWipesCashAndListsForcedSells()
    {
        var (_, cycle, company) = await SeedAsync(price: 200_000_000m);
        var trader = await AddTraderAsync(currentBalance: 1_500_000_000m, reserved: 200m);
        await AddSharesAsync(trader.Id, company.Id, count: 10, price: 200_000_000m);
        var buy = await AddOpenBuyOrderAsync(trader.Id, company.Id, quantity: 2, price: 100m, reserved: 200m, cycle.Id);

        // doubles: gate at 0.2% chance after one wealthy cycle (0.0 → fire). ints: one headline template pick.
        await Service(enabled: true, new ScriptedRandom([0.0d], [0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var bankruptcy = await context.Bankruptcies.AsNoTracking().SingleAsync();
        Assert.Equal(trader.Id, bankruptcy.ParticipantId);
        Assert.Equal(1_500_000_000m, bankruptcy.CashLost);
        Assert.Equal(2_000_000_000m, bankruptcy.ShareWorth);

        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == trader.Id);
        Assert.True(refreshed.IsBankrupt);
        Assert.False(refreshed.IsActive);
        Assert.Equal(0m, refreshed.CurrentBalance);
        Assert.Equal(0m, refreshed.ReservedBalance);
        Assert.Equal(10, refreshed.BankruptcyOwnedAtStart);
        Assert.Equal(0, refreshed.BankruptcyDiscountStep);

        var refreshedBuy = await context.Orders.AsNoTracking().FirstAsync(order => order.Id == buy.Id);
        Assert.Equal(OrderStatus.Cancelled, refreshedBuy.Status);
        Assert.Equal(0m, refreshedBuy.ReservedCashAmount);

        // 65% of the ten held shares (seven, rounded up) listed at 20% below the current price.
        var sell = await context.Orders.AsNoTracking()
            .SingleAsync(order => order.ParticipantId == trader.Id && order.Type == OrderType.Sell);
        Assert.Equal(OrderStatus.Open, sell.Status);
        Assert.Equal(7, sell.Quantity);
        Assert.Equal(160_000_000m, sell.LimitPrice);
        // Listing a forced sale does not reduce the holding; all ten shares remain owned until sold.
        Assert.Equal(10, await context.Holdings.Where(holding => holding.ParticipantId == trader.Id).SumAsync(holding => holding.Quantity));

        Assert.True(await context.MoneyTransactions.AnyAsync(transaction =>
            transaction.ParticipantId == trader.Id
            && transaction.Type == MoneyTransactionType.Bankruptcy
            && transaction.Amount == 1_500_000_000m));
    }

    [Fact]
    public async Task UnsoldForcedSaleIsReListedAStepCheaper()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 0m, bankrupt: true, ownedAtStart: 10, discountStep: 0);
        await AddSharesAsync(trader.Id, company.Id, count: 10, price: 100m);
        var oldSell = await AddOpenSellOrderAsync(trader.Id, company.Id, shareCount: 7, price: 80m, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedOld = await context.Orders.AsNoTracking().FirstAsync(order => order.Id == oldSell.Id);
        Assert.Equal(OrderStatus.Cancelled, refreshedOld.Status);

        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == trader.Id);
        Assert.True(refreshed.IsBankrupt);
        Assert.Equal(1, refreshed.BankruptcyDiscountStep);

        // Deeper discount: 25% below 100 on the re-listing of the seven still-unsold shares.
        var newSell = await context.Orders.AsNoTracking()
            .SingleAsync(order => order.ParticipantId == trader.Id && order.Type == OrderType.Sell && order.Status == OrderStatus.Open);
        Assert.Equal(7, newSell.Quantity);
        Assert.Equal(75m, newSell.LimitPrice);
    }

    [Fact]
    public async Task SellDownCompletesAndClearsTheBankruptFlag()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 0m, bankrupt: true, ownedAtStart: 10, discountStep: 2);
        // Only two of the ten held at bankruptcy remain, so the 65% target (seven shares) is already met.
        await AddSharesAsync(trader.Id, company.Id, count: 2, price: 100m);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == trader.Id);
        Assert.False(refreshed.IsBankrupt);
        Assert.Equal(0, await context.Orders.CountAsync(order => order.ParticipantId == trader.Id));
    }

    [Fact]
    public async Task IndebtedTraderBelowWealthLineGoesBankruptAndDebtIsDischarged()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: -180m);
        await AddSharesAsync(trader.Id, company.Id, count: 10, price: 100m);

        // Worth is 820 (1000 in shares less 180 debt), so debt sits at the 20% ceiling → 5% chance; 0.0 fires.
        await Service(enabled: true, new ScriptedRandom([0.0d], [0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var bankruptcy = await context.Bankruptcies.AsNoTracking().SingleAsync();
        Assert.Equal(trader.Id, bankruptcy.ParticipantId);
        Assert.Equal(0m, bankruptcy.CashLost);

        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == trader.Id);
        Assert.True(refreshed.IsBankrupt);
        Assert.False(refreshed.IsActive);
        Assert.Equal(0m, refreshed.CurrentBalance);

        // A debtor loses no cash on the wipe, so no bankruptcy loss is recorded in the ledger.
        Assert.False(await context.MoneyTransactions.AnyAsync(transaction =>
            transaction.ParticipantId == trader.Id && transaction.Type == MoneyTransactionType.Bankruptcy));
    }

    [Fact]
    public async Task DebtBankruptcyChanceScalesWithDebtDepth()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var deep = await AddTraderAsync(currentBalance: -180m);
        await AddSharesAsync(deep.Id, company.Id, count: 10, price: 100m);
        var shallow = await AddTraderAsync(currentBalance: -30m);
        await AddSharesAsync(shallow.Id, company.Id, count: 10, price: 100m);

        // One 0.04 roll each in id order: under the deep trader's ~5% chance but over the shallow trader's ~0.8%.
        await Service(enabled: true, new ScriptedRandom([0.04d, 0.04d], [0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedDeep = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == deep.Id);
        var refreshedShallow = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == shallow.Id);

        Assert.True(refreshedDeep.IsBankrupt);
        Assert.False(refreshedShallow.IsBankrupt);
        Assert.Equal(-30m, refreshedShallow.CurrentBalance);
    }

    // Default cycle is past the 500-cycle opening protection so a trigger test can actually fire; tests of the
    // protection itself pass a low cycle number explicitly.
    private async Task<(Market Market, MarketCycle Cycle, Company Company)> SeedAsync(decimal price, int cycleNumber = 600)
    {
        var now = DateTime.UtcNow;

        var cycle = new MarketCycle { CycleNumber = cycleNumber, Status = CycleStatus.Running, StartedAt = now };
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
            IssuedSharesCount = 1000,
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
        return (market, cycle, company);
    }

    private async Task<Participant> AddTraderAsync(
        decimal currentBalance,
        decimal reserved = 0m,
        bool bankrupt = false,
        int ownedAtStart = 0,
        int discountStep = 0)
    {
        var trader = new Participant
        {
            Name = "Whale",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Aggressive,
            RiskProfile = RiskProfile.High,
            InitialBalance = currentBalance,
            CurrentBalance = currentBalance,
            ReservedBalance = reserved,
            IsActive = !bankrupt,
            IsBankrupt = bankrupt,
            BankruptcyOwnedAtStart = ownedAtStart,
            BankruptcyDiscountStep = discountStep,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();
        return trader;
    }

    private async Task AddSharesAsync(int ownerId, int companyId, int count, decimal price)
    {
        context.Holdings.Add(new Holding
        {
            ParticipantId = ownerId,
            CompanyId = companyId,
            Quantity = count,
            AverageCost = price,
        });

        await context.SaveChangesAsync();
    }

    private async Task<Order> AddOpenBuyOrderAsync(int participantId, int companyId, int quantity, decimal price, decimal reserved, int cycleId)
    {
        var now = DateTime.UtcNow;
        var order = new Order
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = quantity,
            FilledQuantity = 0,
            LimitPrice = price,
            ReservedCashAmount = reserved,
            CreatedInCycleId = cycleId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return order;
    }

    private async Task<Order> AddOpenSellOrderAsync(int participantId, int companyId, int shareCount, decimal price, int cycleId)
    {
        var now = DateTime.UtcNow;
        var order = new Order
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = shareCount,
            FilledQuantity = 0,
            LimitPrice = price,
            ReservedCashAmount = 0m,
            CreatedInCycleId = cycleId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return order;
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    // Returns queued draws so every random branch is forced; throws if drawn past the script.
    private sealed class ScriptedRandom(double[] doubles, int[] ints) : Random
    {
        private readonly Queue<double> doubles = new(doubles);
        private readonly Queue<int> ints = new(ints);

        public override double NextDouble() => doubles.Dequeue();

        public override int Next(int maxValue) => ints.Dequeue();

        public override int Next(int minValue, int maxValue) => ints.Dequeue();
    }
}
