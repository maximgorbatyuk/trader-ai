using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the market-exit rolls with a scripted Random. At most one NextDouble() is drawn per exit candidate in
// id order (fund-loss holders before starvation); a confirmed departure then draws its replacement's fixed run
// of ints — balance, type, temperament, risk, name. The max-worth ratchet draws nothing.
public sealed class MarketExitServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public MarketExitServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private MarketExitService Service(bool enabled, Random random) =>
        new(context, Options.Create(new MarketExitOptions { Enabled = enabled }), random);

    [Fact]
    public async Task DisabledDoesNothing()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 10_000m, cannotBuyCycles: 30);

        await Service(enabled: false, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.MarketExits.CountAsync());
        Assert.True(await context.Participants.AnyAsync(participant => participant.Id == trader.Id));
    }

    [Fact]
    public async Task StarvedTraderDepartsAndIsReplaced()
    {
        var now = DateTime.UtcNow;
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(
            currentBalance: 40_000m,
            cannotBuyCycles: 20,
            joinedInCycleId: 500,
            reserved: 5_000m,
            name: "Departing Trader");
        await AddHistoricalOrderAsync(trader.Id, company.Id, cycle.Id);
        await AddHistoricalOrderAsync(trader.Id, company.Id, cycle.Id);
        var openBuy = await AddOpenBuyOrderAsync(trader.Id, company.Id, quantity: 1, price: 100m, reserved: 5_000m, cycle.Id);

        // Roll 0.10 clears the 0.25 starvation chance; the replacement then draws balance, Individual, Balanced,
        // Medium, and name combo 0 ("Olivia Marsh").
        await Service(enabled: true, new ScriptedRandom([0.10d], [55_000, 0, 1, 1, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, now);
        await context.SaveChangesAsync();

        Assert.False(await context.Participants.AnyAsync(participant => participant.Id == trader.Id));

        var exit = await context.MarketExits.AsNoTracking().SingleAsync();
        Assert.Equal(trader.Id, exit.ParticipantId);
        Assert.Equal("Departing Trader", exit.Name);
        Assert.Equal(MarketExitReason.Starvation, exit.Reason);
        Assert.Equal(500, exit.JoinedInCycleId);
        Assert.Equal(cycle.Id, exit.LeftInCycleId);
        Assert.Equal(3, exit.OrdersPlaced);
        Assert.Equal(40_000m, exit.InitialBalance);
        Assert.Equal(40_000m, exit.MaxTotalWorth);
        Assert.Equal(40_000m, exit.QuitBalance);
        Assert.Equal(now, exit.LeftAt);

        var refreshedBuy = await context.Orders.AsNoTracking().FirstAsync(order => order.Id == openBuy.Id);
        Assert.Equal(OrderStatus.Cancelled, refreshedBuy.Status);
        Assert.True(await context.MoneyTransactions.AnyAsync(transaction =>
            transaction.ParticipantId == trader.Id
            && transaction.Type == MoneyTransactionType.Release
            && transaction.Amount == 5_000m));

        var replacement = await context.Participants.AsNoTracking().SingleAsync();
        Assert.NotEqual(ParticipantType.Player, replacement.Type);
        Assert.Equal(ParticipantType.Individual, replacement.Type);
        Assert.Equal(Temperament.Balanced, replacement.Temperament);
        Assert.Equal(RiskProfile.Medium, replacement.RiskProfile);
        Assert.Equal("Olivia Marsh", replacement.Name);
        Assert.Equal(55_000m, replacement.InitialBalance);
        Assert.Equal(55_000m, replacement.CurrentBalance);
        Assert.Equal(55_000m, replacement.MaxTotalWorth);
        Assert.Equal(cycle.Id, replacement.JoinedInCycleId);
        Assert.True(replacement.IsActive);
    }

    [Fact]
    public async Task StarvationRollAtChanceBoundaryDoesNotFire()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 10_000m, cannotBuyCycles: 20);

        // At a 20-cycle drought the chance is exactly 0.25; the comparison is strict, so a 0.25 roll stays.
        await Service(enabled: true, new ScriptedRandom([0.25d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.MarketExits.CountAsync());
        Assert.True(await context.Participants.AnyAsync(participant => participant.Id == trader.Id));
    }

    [Fact]
    public async Task StarvationChanceRampsWithDrought()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 10_000m, cannotBuyCycles: 45);

        // At a 45-cycle drought the chance has ramped to 0.50, so a 0.49 roll — which would miss the 0.25 base —
        // now departs.
        await Service(enabled: true, new ScriptedRandom([0.49d], [20_000, 0, 0, 0, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.MarketExits.CountAsync());
        Assert.False(await context.Participants.AnyAsync(participant => participant.Id == trader.Id));
    }

    [Fact]
    public async Task StarvationChanceCapsAtOne()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        await AddTraderAsync(currentBalance: 10_000m, cannotBuyCycles: 1_000);

        // A very long drought would push the raw chance far past 1.0; capped at 1.0 it still departs on 0.99.
        await Service(enabled: true, new ScriptedRandom([0.99d], [20_000, 1, 2, 2, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.MarketExits.CountAsync());
        var replacement = await context.Participants.AsNoTracking().SingleAsync();
        Assert.Equal(ParticipantType.AIAgent, replacement.Type);
    }

    [Fact]
    public async Task IneligibleTradersDrawNothing()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);

        var wealthy = await AddTraderAsync(currentBalance: 60_000m, cannotBuyCycles: 25);
        var holder = await AddTraderAsync(currentBalance: 10_000m, cannotBuyCycles: 25);
        await AddSharesAsync(holder.Id, company.Id, count: 1, price: 100m);
        var shortDrought = await AddTraderAsync(currentBalance: 10_000m, cannotBuyCycles: 19);
        var inactive = await AddTraderAsync(currentBalance: 10_000m, cannotBuyCycles: 25, isActive: false);
        var bankrupt = await AddTraderAsync(currentBalance: 10_000m, cannotBuyCycles: 25, isBankrupt: true);
        var member = await AddTraderAsync(currentBalance: 10_000m, cannotBuyCycles: 25);
        await MakeFundMemberAsync(member, cycle.Id);
        var player = await AddTraderAsync(currentBalance: 10_000m, cannotBuyCycles: 25, type: ParticipantType.Player);

        // Every seeded trader fails at least one starvation gate, so an empty script must never be drawn from.
        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.MarketExits.CountAsync());
        foreach (var id in new[] { wealthy.Id, holder.Id, shortDrought.Id, inactive.Id, bankrupt.Id, member.Id, player.Id })
        {
            Assert.True(await context.Participants.AnyAsync(participant => participant.Id == id));
        }
    }

    [Fact]
    public async Task MaxTotalWorthOnlyRatchetsUp()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var peaked = await AddTraderAsync(currentBalance: 40_000m, maxTotalWorth: 200_000m);
        var climbing = await AddTraderAsync(currentBalance: 40_000m, maxTotalWorth: 0m);
        await AddSharesAsync(climbing.Id, company.Id, count: 100, price: 100m);

        // Neither trader is a starvation candidate (no drought), so nothing is drawn while worth is ratcheted.
        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedPeaked = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == peaked.Id);
        Assert.Equal(200_000m, refreshedPeaked.MaxTotalWorth);

        var refreshedClimbing = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == climbing.Id);
        Assert.Equal(50_000m, refreshedClimbing.MaxTotalWorth);
    }

    [Fact]
    public async Task FundLossFlagDefersWhileHoldingShares()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var flagged = await AddTraderAsync(currentBalance: 10_000m, pendingFundLossExitRoll: true);
        await AddSharesAsync(flagged.Id, company.Id, count: 5, price: 100m);

        // Holding shares, the flag is kept and no roll is drawn.
        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.MarketExits.CountAsync());
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == flagged.Id);
        Assert.True(refreshed.PendingFundLossExitRoll);
    }

    [Fact]
    public async Task FundLossFlagDefersWhileInAFund()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        // Shareless and solvent, but back in a fund: departing now would delete the participant out from under a
        // live membership, so the flag is kept and no roll is drawn.
        var flagged = await AddTraderAsync(currentBalance: 10_000m, pendingFundLossExitRoll: true);
        await MakeFundMemberAsync(flagged, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.MarketExits.CountAsync());
        Assert.True(await context.Participants.AnyAsync(participant => participant.Id == flagged.Id));
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == flagged.Id);
        Assert.True(refreshed.PendingFundLossExitRoll);
    }

    [Fact]
    public async Task FundLossFlagClearsOnFailedRoll()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        // Also starvation-eligible: a single 0.99 roll proves the fund-loss branch consumes the only draw and
        // does not fall through to a second starvation roll.
        var flagged = await AddTraderAsync(currentBalance: 10_000m, cannotBuyCycles: 20, pendingFundLossExitRoll: true);

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.MarketExits.CountAsync());
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == flagged.Id);
        Assert.False(refreshed.PendingFundLossExitRoll);
    }

    [Fact]
    public async Task FundLossDepartsOnSuccess()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var flagged = await AddTraderAsync(currentBalance: 8_000m, pendingFundLossExitRoll: true, name: "Wiped Out");

        // Shareless and solvent, a 0.10 roll clears the 0.25 fund-loss chance and the trader departs.
        await Service(enabled: true, new ScriptedRandom([0.10d], [30_000, 0, 0, 0, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var exit = await context.MarketExits.AsNoTracking().SingleAsync();
        Assert.Equal(MarketExitReason.FundLoss, exit.Reason);
        Assert.Equal("Wiped Out", exit.Name);
        Assert.False(await context.Participants.AnyAsync(participant => participant.Id == flagged.Id));
        Assert.Equal(1, await context.Participants.CountAsync());
    }

    [Fact]
    public async Task ReplacementNameProbesPastTakenCombos()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        // The departing trader owns the first name combo, so the name draw of 0 lands on a taken slot and the
        // deterministic probe steps to the next combo.
        var trader = await AddTraderAsync(currentBalance: 10_000m, cannotBuyCycles: 20, name: "Olivia Marsh");

        await Service(enabled: true, new ScriptedRandom([0.10d], [20_000, 0, 0, 0, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.False(await context.Participants.AnyAsync(participant => participant.Id == trader.Id));
        var replacement = await context.Participants.AsNoTracking().SingleAsync();
        Assert.Equal("Olivia Okonkwo", replacement.Name);
    }

    [Fact]
    public async Task LocalCrisisDoublesFundLossChanceSoAHigherRollDeparts()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var flagged = await AddTraderAsync(currentBalance: 8_000m, pendingFundLossExitRoll: true, name: "Wiped Out");
        var crisis = await AddCrisisAsync(cycle, CrisisScope.Local);

        // Without a crisis a 0.40 roll misses the 0.25 fund-loss chance; a local crisis doubles it to 0.50, so it
        // now departs — and the exit leaves no crisis-timeline entry.
        await Service(enabled: true, new ScriptedRandom([0.40d], [30_000, 0, 0, 0, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow, crisis);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.MarketExits.CountAsync());
        Assert.False(await context.Participants.AnyAsync(participant => participant.Id == flagged.Id));
        Assert.Equal(0, await context.CrisisEvents.CountAsync());
    }

    [Fact]
    public async Task GlobalCrisisQuintuplesFundLossChanceBeyondLocal()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var flagged = await AddTraderAsync(currentBalance: 8_000m, pendingFundLossExitRoll: true);
        var crisis = await AddCrisisAsync(cycle, CrisisScope.Global);

        // A 0.60 roll would still miss a local crisis's 0.50 chance; a global crisis quintuples the 0.25 base past
        // 1.0 (clamped), so it departs — proving the global 5x, not the local 2x.
        await Service(enabled: true, new ScriptedRandom([0.60d], [30_000, 0, 0, 0, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow, crisis);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.MarketExits.CountAsync());
        Assert.False(await context.Participants.AnyAsync(participant => participant.Id == flagged.Id));
    }

    [Fact]
    public async Task LocalCrisisAlsoScalesStarvationChance()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 10_000m, cannotBuyCycles: 20);
        var crisis = await AddCrisisAsync(cycle, CrisisScope.Local);

        // At a 20-cycle drought the base starvation chance is 0.25; a local crisis doubles it to 0.50, so a 0.40
        // roll that would otherwise miss now departs the starved trader.
        await Service(enabled: true, new ScriptedRandom([0.40d], [20_000, 0, 0, 0, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow, crisis);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.MarketExits.CountAsync());
        Assert.False(await context.Participants.AnyAsync(participant => participant.Id == trader.Id));
    }

    private async Task<Crisis> AddCrisisAsync(MarketCycle cycle, CrisisScope scope)
    {
        var crisis = new Crisis
        {
            Title = "Shock",
            Content = "Body",
            Scope = scope,
            TriggeredInCycleId = cycle.Id,
            TriggeredInCycleNumber = cycle.CycleNumber,
            DurationCycles = 20,
            TriggeredAt = DateTime.UtcNow,
        };
        context.Crises.Add(crisis);
        await context.SaveChangesAsync();
        return crisis;
    }

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
        int cannotBuyCycles = 0,
        ParticipantType type = ParticipantType.Individual,
        bool isActive = true,
        bool isBankrupt = false,
        bool pendingFundLossExitRoll = false,
        int joinedInCycleId = 0,
        decimal maxTotalWorth = 0m,
        decimal reserved = 0m,
        string name = "Trader")
    {
        var trader = new Participant
        {
            Name = name,
            Type = type,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = currentBalance,
            CurrentBalance = currentBalance,
            ReservedBalance = reserved,
            IsActive = isActive,
            IsBankrupt = isBankrupt,
            CannotBuyCycles = cannotBuyCycles,
            JoinedInCycleId = joinedInCycleId,
            MaxTotalWorth = maxTotalWorth,
            PendingFundLossExitRoll = pendingFundLossExitRoll,
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

    private async Task AddHistoricalOrderAsync(int participantId, int companyId, int cycleId)
    {
        var now = DateTime.UtcNow;
        context.Orders.Add(new Order
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Type = OrderType.Buy,
            Status = OrderStatus.Cancelled,
            Quantity = 1,
            FilledQuantity = 0,
            LimitPrice = 100m,
            ReservedCashAmount = 0m,
            CreatedInCycleId = cycleId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync();
    }

    private async Task MakeFundMemberAsync(Participant member, int cycleId)
    {
        var now = DateTime.UtcNow;
        var fundParticipant = new Participant
        {
            Name = "Fund",
            Type = ParticipantType.CollectiveFund,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 0m,
            CurrentBalance = 0m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(fundParticipant);
        await context.SaveChangesAsync();

        var fund = new CollectiveFund
        {
            ParticipantId = fundParticipant.Id,
            FoundedByParticipantId = fundParticipant.Id,
            Status = CollectiveFundStatus.Active,
            CreatedInCycleId = cycleId,
            CreatedAt = now,
        };
        context.CollectiveFunds.Add(fund);
        await context.SaveChangesAsync();

        context.CollectiveFundParticipants.Add(new CollectiveFundParticipant
        {
            CollectiveFundId = fund.Id,
            ParticipantId = member.Id,
            JoinedAt = now,
            JoinedInCycleId = cycleId,
            DepositAmount = 0m,
        });
        await context.SaveChangesAsync();
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
