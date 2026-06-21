using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the collective-fund roll with a scripted Random so joining, opening, and leaving are forced
// deterministically. One NextDouble() is drawn per active-fund member sitting at or above the leave line, then
// one per eligible independent trader for the join/open band; closing and deposit returns draw nothing.
public sealed class CollectiveFundServiceTests : IDisposable
{
    private const decimal LeaveThreshold = 100_000_000m;

    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public CollectiveFundServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private CollectiveFundService Service(bool enabled, Random random) =>
        new(context, Options.Create(new CollectiveFundOptions { Enabled = enabled }), random);

    [Fact]
    public async Task DisabledDoesNothing()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        await AddTraderAsync(currentBalance: 100_000m);

        await Service(enabled: false, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.CollectiveFunds.CountAsync());
        Assert.Equal(0, await context.CollectiveFundParticipants.CountAsync());
    }

    [Fact]
    public async Task EligibleTraderOpensFundAndContributesNinetyPercent()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var founder = await AddTraderAsync(currentBalance: 100_000m, reserved: 100m);
        var buy = await AddOpenBuyOrderAsync(founder.Id, company.Id, quantity: 1, price: 100m, reserved: 100m, cycle.Id);

        // No fund exists, so the join band cannot apply; 0.0 falls into the 3% open band.
        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var fund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.Active, fund.Status);
        Assert.Equal(founder.Id, fund.FoundedByParticipantId);

        var fundParticipant = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fund.ParticipantId);
        Assert.Equal(ParticipantType.CollectiveFund, fundParticipant.Type);
        Assert.Equal("Trader's Fund #600", fundParticipant.Name);
        Assert.Equal(90_000m, fundParticipant.CurrentBalance);

        var membership = await context.CollectiveFundParticipants.AsNoTracking().SingleAsync();
        Assert.Equal(founder.Id, membership.ParticipantId);
        Assert.Equal(90_000m, membership.DepositAmount);

        var refreshedFounder = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == founder.Id);
        Assert.Equal(10_000m, refreshedFounder.CurrentBalance);

        // Joining frees the cash tied up in the founder's standing bid.
        var refreshedBuy = await context.Orders.AsNoTracking().FirstAsync(order => order.Id == buy.Id);
        Assert.Equal(OrderStatus.Cancelled, refreshedBuy.Status);
    }

    [Fact]
    public async Task NoFundIsOpenedDuringTheOpeningWindow()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m, cycleNumber: 10);
        await AddTraderAsync(currentBalance: 100_000m);

        // Inside the 50-cycle protection window the join/open phase is skipped, so 0.0 buys nothing.
        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.CollectiveFunds.CountAsync());
        Assert.Equal(0, await context.CollectiveFundParticipants.CountAsync());
    }

    [Fact]
    public async Task EligibleTraderJoinsExistingFund()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 50_000m);
        var existing = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, existing, deposit: 50_000m, cycle.Id);

        var joiner = await AddTraderAsync(currentBalance: 100_000m);

        // The pre-existing member sits below the leave line (no draw); the joiner's 0.0 lands in the 5% join band.
        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(2, await context.CollectiveFundParticipants.CountAsync(member => member.CollectiveFundId == fund.Id));

        var membership = await context.CollectiveFundParticipants.AsNoTracking()
            .FirstAsync(member => member.ParticipantId == joiner.Id);
        Assert.Equal(90_000m, membership.DepositAmount);

        var refreshedFund = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fundParticipant.Id);
        Assert.Equal(140_000m, refreshedFund.CurrentBalance);
    }

    [Fact]
    public async Task FullFundIsNotJoined()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, _) = await AddFundAsync(balance: 0m);
        for (var index = 0; index < 20; index++)
        {
            var member = await AddTraderAsync(currentBalance: 1_000m);
            await AddMembershipAsync(fund, member, deposit: 1_000m, cycle.Id);
        }

        var joiner = await AddTraderAsync(currentBalance: 100_000m);

        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // The capped fund gains no 21st member; the would-be joiner opens its own fund instead.
        Assert.Equal(20, await context.CollectiveFundParticipants.CountAsync(member => member.CollectiveFundId == fund.Id));
        Assert.Equal(2, await context.CollectiveFunds.CountAsync());
        var joinerMembership = await context.CollectiveFundParticipants.AsNoTracking()
            .FirstAsync(member => member.ParticipantId == joiner.Id);
        Assert.NotEqual(fund.Id, joinerMembership.CollectiveFundId);
    }

    [Fact]
    public async Task ExistingMemberNeverJoinsASecondFund()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fundA, _) = await AddFundAsync(balance: 0m);
        var member = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fundA, member, deposit: 90_000m, cycle.Id);

        // A second open fund with capacity exists, founded by someone else.
        var (fundB, _) = await AddFundAsync(balance: 0m);
        var otherFounder = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fundB, otherFounder, deposit: 90_000m, cycle.Id);

        // Both seeded participants are already members, so the loop draws nothing; an empty script would throw on a stray draw.
        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.CollectiveFundParticipants.CountAsync(membership => membership.ParticipantId == member.Id));
        Assert.Equal(1, await context.CollectiveFundParticipants.CountAsync(membership => membership.CollectiveFundId == fundB.Id));
    }

    [Fact]
    public async Task MemberAtThresholdLeavesAndDepositIsReturned()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 200_000m);
        var leaver = await AddTraderAsync(currentBalance: 150_000_000m);
        await AddMembershipAsync(fund, leaver, deposit: 90_000m, cycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        // Only the above-the-line member draws; at ramp 1 the 20% chance fires on 0.0.
        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(2, await context.CollectiveFundParticipants.CountAsync(member => member.CollectiveFundId == fund.Id));
        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == leaver.Id));

        var refreshedLeaver = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == leaver.Id);
        Assert.Equal(150_090_000m, refreshedLeaver.CurrentBalance);

        var refreshedFund = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fundParticipant.Id);
        Assert.Equal(110_000m, refreshedFund.CurrentBalance);
    }

    [Fact]
    public async Task LeaveRaisesCashBySellingWhenFundIsShort()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 1_000m);
        await AddSharesAsync(fundParticipant.Id, company.Id, count: 20, price: 100m);
        var leaver = await AddTraderAsync(currentBalance: 150_000_000m);
        await AddMembershipAsync(fund, leaver, deposit: 90_000m, cycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // The deposit is not yet covered, so the member stays flagged as leaving and the fund lists shares to raise cash.
        var membership = await context.CollectiveFundParticipants.AsNoTracking().FirstAsync(member => member.ParticipantId == leaver.Id);
        Assert.True(membership.IsLeaving);

        var sell = await context.Orders.AsNoTracking()
            .SingleAsync(order => order.ParticipantId == fundParticipant.Id && order.Type == OrderType.Sell);
        Assert.Equal(OrderStatus.Open, sell.Status);
        Assert.Equal(20, sell.Quantity);
        Assert.Equal(90m, sell.LimitPrice);
    }

    [Fact]
    public async Task LastPairClosesSplitsCashAndReactivatesMembers()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 200_000m);
        var leaver = await AddTraderAsync(currentBalance: 150_000_000m);
        await AddMembershipAsync(fund, leaver, deposit: 90_000m, cycle.Id);
        var partner = await AddTraderAsync(currentBalance: 450_000m);
        await AddMembershipAsync(fund, partner, deposit: 80_000m, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedFund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.Closed, refreshedFund.Status);
        Assert.Equal(0, await context.CollectiveFundParticipants.CountAsync());

        // The 200k pot splits evenly between the pair, and the fund participant settles flat and inactive.
        var refreshedLeaver = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == leaver.Id);
        var refreshedPartner = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == partner.Id);
        Assert.Equal(150_100_000m, refreshedLeaver.CurrentBalance);
        Assert.Equal(550_000m, refreshedPartner.CurrentBalance);

        var refreshedFundParticipant = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fundParticipant.Id);
        Assert.False(refreshedFundParticipant.IsActive);
        Assert.Equal(0m, refreshedFundParticipant.CurrentBalance);
    }

    [Fact]
    public async Task ClosingFundListsAllItsSharesForSale()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(status: CollectiveFundStatus.GoingToBeClosed, balance: 0m);
        await AddSharesAsync(fundParticipant.Id, company.Id, count: 15, price: 100m);
        var memberOne = await AddTraderAsync(currentBalance: 10_000m);
        var memberTwo = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, memberOne, deposit: 10_000m, cycle.Id);
        await AddMembershipAsync(fund, memberTwo, deposit: 10_000m, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedFund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.GoingToBeClosed, refreshedFund.Status);

        var sell = await context.Orders.AsNoTracking()
            .SingleAsync(order => order.ParticipantId == fundParticipant.Id && order.Type == OrderType.Sell);
        Assert.Equal(15, sell.Quantity);
        Assert.Equal(90m, sell.LimitPrice);
        Assert.Equal(15, await context.OrderShares.CountAsync(link => link.OrderId == sell.Id));
    }

    [Fact]
    public async Task DividendPassesThroughToMembersByDeposit()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 1_000m);
        var memberOne = await AddTraderAsync(currentBalance: 0m);
        var memberTwo = await AddTraderAsync(currentBalance: 0m);
        await AddMembershipAsync(fund, memberOne, deposit: 90_000m, cycle.Id);
        await AddMembershipAsync(fund, memberTwo, deposit: 270_000m, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([], []))
            .DistributeDividendToMembersAsync(fundParticipant, payout: 1_000m, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // Half of the 1,000 receipt (500) is shared 1:3 by deposit.
        var refreshedOne = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == memberOne.Id);
        var refreshedTwo = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == memberTwo.Id);
        Assert.Equal(125m, refreshedOne.CurrentBalance);
        Assert.Equal(375m, refreshedTwo.CurrentBalance);

        var refreshedFund = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fundParticipant.Id);
        Assert.Equal(500m, refreshedFund.CurrentBalance);

        // Member payouts carry their own transaction type so they can be totalled separately from share dividends.
        Assert.Equal(2, await context.MoneyTransactions.CountAsync(transaction => transaction.Type == MoneyTransactionType.CollectiveFundDividend));
    }

    [Fact]
    public async Task CollectiveFundNeverGoesBankrupt()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var (_, fundParticipant) = await AddFundAsync(balance: 2_000_000_000m);
        await AddSharesAsync(fundParticipant.Id, company.Id, count: 100, price: 100m);

        // A fund sits far above the wealth line, yet the bankruptcy gate skips every non-trader participant type.
        var bankruptcy = new BankruptcyService(
            context,
            Options.Create(new BankruptcyOptions { Enabled = true }),
            new ScriptedRandom([], []));
        await bankruptcy.ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.Bankruptcies.CountAsync());
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fundParticipant.Id);
        Assert.False(refreshed.IsBankrupt);
        Assert.Equal(0, refreshed.WealthyCycles);
    }

    [Fact]
    public async Task BuyingDroughtRaisesTheJoinChance()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, _) = await AddFundAsync(balance: 0m);
        var founder = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, founder, deposit: 90_000m, cycle.Id);

        var joiner = await AddTraderAsync(currentBalance: 100_000m, cannotBuyCycles: 10);

        // 0.20 is above the 5% base join chance but below the 25% a ten-cycle drought raises it to, so it joins.
        await Service(enabled: true, new ScriptedRandom([0.20d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(2, await context.CollectiveFundParticipants.CountAsync(member => member.CollectiveFundId == fund.Id));
        Assert.True(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == joiner.Id));
    }

    [Fact]
    public async Task JoinChanceIsCappedAtTheTwentyCycleTier()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, _) = await AddFundAsync(balance: 0m);
        var founder = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, founder, deposit: 90_000m, cycle.Id);

        var joiner = await AddTraderAsync(currentBalance: 100_000m, cannotBuyCycles: 50);

        // The join bonus tops out at the 20-cycle tier (45%), so 0.46 stays outside it: the joiner opens its own
        // fund rather than joining this one.
        await Service(enabled: true, new ScriptedRandom([0.46d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.CollectiveFundParticipants.CountAsync(member => member.CollectiveFundId == fund.Id));
        var joinerMembership = await context.CollectiveFundParticipants.AsNoTracking()
            .FirstAsync(member => member.ParticipantId == joiner.Id);
        Assert.NotEqual(fund.Id, joinerMembership.CollectiveFundId);
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
        decimal reserved = 0m)
    {
        var trader = new Participant
        {
            Name = "Trader",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = currentBalance,
            CurrentBalance = currentBalance,
            ReservedBalance = reserved,
            IsActive = true,
            CannotBuyCycles = cannotBuyCycles,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();
        return trader;
    }

    private async Task<(CollectiveFund Fund, Participant Participant)> AddFundAsync(
        CollectiveFundStatus status = CollectiveFundStatus.Active,
        decimal balance = 0m)
    {
        var fundParticipant = new Participant
        {
            Name = "Collective Fund",
            Type = ParticipantType.CollectiveFund,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 0m,
            CurrentBalance = balance,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(fundParticipant);
        await context.SaveChangesAsync();

        var fund = new CollectiveFund
        {
            ParticipantId = fundParticipant.Id,
            FoundedByParticipantId = fundParticipant.Id,
            Status = status,
            CreatedInCycleId = 0,
            CreatedAt = DateTime.UtcNow,
        };
        context.CollectiveFunds.Add(fund);
        await context.SaveChangesAsync();
        return (fund, fundParticipant);
    }

    private async Task<CollectiveFundParticipant> AddMembershipAsync(
        CollectiveFund fund,
        Participant member,
        decimal deposit,
        int joinedCycleId,
        bool isLeaving = false,
        int leaveRamp = 0)
    {
        var membership = new CollectiveFundParticipant
        {
            CollectiveFundId = fund.Id,
            ParticipantId = member.Id,
            JoinedAt = DateTime.UtcNow,
            JoinedInCycleId = joinedCycleId,
            DepositAmount = deposit,
            LeaveRampCycles = leaveRamp,
            IsLeaving = isLeaving,
        };
        context.CollectiveFundParticipants.Add(membership);
        await context.SaveChangesAsync();
        return membership;
    }

    private async Task AddSharesAsync(int ownerId, int companyId, int count, decimal price)
    {
        var now = DateTime.UtcNow;
        for (var index = 0; index < count; index++)
        {
            context.Shares.Add(new Share
            {
                CompanyId = companyId,
                OwnerId = ownerId,
                InitialPrice = price,
                CurrentPrice = price,
                LastUpdatedAt = now,
            });
        }

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
