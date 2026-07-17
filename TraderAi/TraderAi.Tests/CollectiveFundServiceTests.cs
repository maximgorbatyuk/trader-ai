using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the collective-fund roll with a scripted Random so joining, opening, and leaving are forced
// deterministically. A tenure-eligible member draws for an independent exit and, after a miss, may draw again
// for a switch; eligible independent traders draw once for the join/open band.
public sealed class CollectiveFundServiceTests : IDisposable
{
    private const decimal ReferenceLargeBalance = 100_000_000m;

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

    private CollectiveFundService Service(
        bool enabled,
        Random random,
        bool loansEnabled = true,
        int? softCloseMembers = null,
        int minimumMembershipTradingDays = 7,
        decimal cashBufferFraction = 0.10m,
        decimal preLeaveCashBufferFraction = 0.15m)
    {
        var loan = Options.Create(new LoanOptions { Enabled = loansEnabled });
        var fundOptions = new CollectiveFundOptions
        {
            Enabled = enabled,
            MinimumMembershipTradingDays = minimumMembershipTradingDays,
            CashBufferFraction = cashBufferFraction,
            PreLeaveCashBufferFraction = preLeaveCashBufferFraction,
        };
        if (softCloseMembers is int capacity)
        {
            fundOptions.SoftCloseMembers = capacity;
        }

        return new CollectiveFundService(
            context,
            Options.Create(fundOptions),
            Options.Create(new RandomChanceRatesOptions()),
            loan,
            new LoanService(context, loan),
            random);
    }

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
    public async Task NewFundSnapshotsItsQuotaBeforeSameCycleJoinersArrive()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m, tradingDayNumber: 20);
        await AddTraderAsync(currentBalance: 100_000m);
        await AddTraderAsync(currentBalance: 100_000m);

        // The first trader opens the fund and the second joins it later in the same join/open pass.
        await Service(enabled: true, new ScriptedRandom([0.0d, 0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var fund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(2, await context.CollectiveFundParticipants.CountAsync(member => member.CollectiveFundId == fund.Id));
        Assert.Equal(20, fund.LastVoluntaryLeaveTradingDayNumber);
        Assert.Equal(1, fund.VoluntaryLeaveQuota);
        Assert.Equal(0, fund.VoluntaryLeavesUsed);
    }

    [Fact]
    public async Task NewFundInheritsFounderCharacteristics()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        await AddTraderAsync(currentBalance: 100_000m, temperament: Temperament.Aggressive, riskProfile: RiskProfile.High);

        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var fund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        var fundParticipant = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fund.ParticipantId);
        Assert.Equal(Temperament.Aggressive, fundParticipant.Temperament);
        Assert.Equal(RiskProfile.High, fundParticipant.RiskProfile);
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

        // The pre-existing member is below the tenure gate; the joiner's 0.0 lands in the 5% join band.
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
    public async Task JoinContributionUsesOnlySettledCash()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, _) = await AddFundAsync(balance: 50_000m);
        var existing = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(fund, existing, deposit: 1_000m, cycle.Id);
        var joiner = await AddTraderAsync(currentBalance: 100_000m);
        joiner.SettledCashBalance = 10_000m;
        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var membership = await context.CollectiveFundParticipants.AsNoTracking()
            .SingleAsync(member => member.ParticipantId == joiner.Id);
        Assert.Equal(9_000m, membership.DepositAmount);
        Assert.Equal(1_000m, (await context.Participants.AsNoTracking()
            .SingleAsync(participant => participant.Id == joiner.Id)).SettledCashBalance);
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
    public async Task OverCapacityFundEvictsItsNewestMember()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);

        // Capacity is two, so a three-member fund is over the line and returns its most recently joined member.
        var (fund, _) = await AddFundAsync(balance: 200_000m);
        Participant? newest = null;
        for (var index = 1; index <= 3; index++)
        {
            // Each member is below the tenure gate and above the join ceiling, so none rolls in either pass.
            var member = await AddTraderAsync(currentBalance: 600_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, joinedCycleId: index);
            newest = member;
        }

        // No draws: the members neither leave nor re-pool, the eviction is deterministic, and no free trader joins.
        await Service(enabled: true, new ScriptedRandom([], []), softCloseMembers: 2)
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // The newest member is dropped, the fund settles at capacity, and the deposit is returned in full.
        Assert.Equal(2, await context.CollectiveFundParticipants.CountAsync(member => member.CollectiveFundId == fund.Id));
        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == newest!.Id));

        var refreshedNewest = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == newest!.Id);
        Assert.Equal(610_000m, refreshedNewest.CurrentBalance);

        var leaveEvent = await context.CollectiveFundMembershipEvents.AsNoTracking()
            .SingleAsync(membershipEvent => membershipEvent.ParticipantId == newest!.Id
                && membershipEvent.Type == CollectiveFundMembershipEventType.Left);
        Assert.Equal(10_000m, leaveEvent.Amount);

        Assert.Equal(CollectiveFundStatus.Active, (await context.CollectiveFunds.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task FundAtCapacityTakesNoNewJoiner()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);

        // The fund sits exactly at capacity, so it is closed to new joiners and the would-be joiner opens its own.
        var (fund, _) = await AddFundAsync(balance: 50_000m);
        for (var index = 0; index < 2; index++)
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, joinedCycleId: cycle.Id);
        }

        var joiner = await AddTraderAsync(currentBalance: 100_000m);

        // Only the joiner draws; with the fund closed there is nothing to join, so 0.0 falls into the open band.
        await Service(enabled: true, new ScriptedRandom([0.0d], []), softCloseMembers: 2)
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(2, await context.CollectiveFundParticipants.CountAsync(member => member.CollectiveFundId == fund.Id));
        Assert.Equal(2, await context.CollectiveFunds.CountAsync());
        var joinerMembership = await context.CollectiveFundParticipants.AsNoTracking()
            .FirstAsync(member => member.ParticipantId == joiner.Id);
        Assert.NotEqual(fund.Id, joinerMembership.CollectiveFundId);
    }

    [Fact]
    public async Task PlayerManagedFundIsAlsoShedWhenOverCapacity()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);

        // Capacity is enforced uniformly: a player-managed fund over the cap also returns its newest member.
        var (fund, _) = await AddFundAsync(balance: 200_000m);
        fund.IsPlayerManaged = true;
        await context.SaveChangesAsync();

        Participant? newest = null;
        for (var index = 1; index <= 3; index++)
        {
            var member = await AddTraderAsync(currentBalance: 600_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, joinedCycleId: index);
            newest = member;
        }

        await Service(enabled: true, new ScriptedRandom([], []), softCloseMembers: 2)
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(2, await context.CollectiveFundParticipants.CountAsync(member => member.CollectiveFundId == fund.Id));
        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == newest!.Id));
        Assert.Equal(CollectiveFundStatus.Active, (await context.CollectiveFunds.AsNoTracking().SingleAsync()).Status);
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
    public async Task MemberWithLargeBalanceLeavesAndDepositIsReturned()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, fundParticipant) = await AddFundAsync(balance: 200_000m);
        var leaver = await AddTraderAsync(currentBalance: 150_000_000m);
        await AddMembershipAsync(fund, leaver, deposit: 90_000m, joinedCycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        // Only the tenure-eligible member draws; at ramp 1 the 20% chance fires on 0.0.
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
    public async Task MemberBelowFormerCashThresholdLeavesWithoutRejoiningInTheSameCycle()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, _) = await AddFundAsync(balance: 200_000m);
        var leaver = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, leaver, deposit: 90_000m, joinedCycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == leaver.Id));
        var refreshedLeaver = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == leaver.Id);
        Assert.False(refreshedLeaver.PendingFundSwitch);
        Assert.Equal(100_000m, refreshedLeaver.CurrentBalance);
    }

    [Fact]
    public async Task SmallFundAllowsOneVoluntaryDepartureAndResetsTheQuotaNextTradingDay()
    {
        var (_, dayTwentyCycle, _) = await SeedAsync(price: 100m, tradingDayNumber: 20);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, _) = await AddFundAsync(balance: 200_000m);

        // Two members cleared the tenure gate and both want out on the same day.
        var firstLeaver = await AddTraderAsync(currentBalance: ReferenceLargeBalance);
        await AddMembershipAsync(fund, firstLeaver, deposit: 90_000m, joinedCycle.Id);
        var secondLeaver = await AddTraderAsync(currentBalance: ReferenceLargeBalance);
        await AddMembershipAsync(fund, secondLeaver, deposit: 90_000m, joinedCycle.Id);

        // Two short-tenure members keep the fund above the pair-close floor so a departure does not unwind it.
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, dayTwentyCycle.Id);
        }

        // Four members produce a quota of one, so only the lowest-id leaver draws and departs on day 20.
        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(dayTwentyCycle.Id, dayTwentyCycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == firstLeaver.Id));
        var held = await context.CollectiveFundParticipants.AsNoTracking()
            .SingleAsync(member => member.ParticipantId == secondLeaver.Id);
        Assert.False(held.IsLeaving);
        var dayTwentyFund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(20, dayTwentyFund.LastVoluntaryLeaveTradingDayNumber);
        Assert.Equal(1, dayTwentyFund.VoluntaryLeaveQuota);
        Assert.Equal(1, dayTwentyFund.VoluntaryLeavesUsed);

        // Day 21 snapshots a fresh quota of one from the three remaining members.
        var dayTwentyOneCycle = await AddCycleForTradingDayAsync(dayNumber: 21, cycleNumber: 601);
        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(dayTwentyOneCycle.Id, dayTwentyOneCycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == secondLeaver.Id));
        var dayTwentyOneFund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(21, dayTwentyOneFund.LastVoluntaryLeaveTradingDayNumber);
        Assert.Equal(1, dayTwentyOneFund.VoluntaryLeaveQuota);
        Assert.Equal(1, dayTwentyOneFund.VoluntaryLeavesUsed);
    }

    [Fact]
    public async Task TenMemberFundAllowsTwoVoluntaryDeparturesPerTradingDay()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m, tradingDayNumber: 20);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, _) = await AddFundAsync(balance: 200_000m);
        var eligibleLeavers = new List<Participant>();
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, joinedCycle.Id);
            eligibleLeavers.Add(member);
        }

        foreach (var _ in Enumerable.Range(0, 7))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        await Service(enabled: true, new ScriptedRandom([0.0d, 0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == eligibleLeavers[0].Id));
        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == eligibleLeavers[1].Id));
        Assert.True(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == eligibleLeavers[2].Id));
        Assert.Equal(8, await context.CollectiveFundParticipants.CountAsync(member => member.CollectiveFundId == fund.Id));

        var quotaState = await context.CollectiveFunds.AsNoTracking().SingleAsync(candidate => candidate.Id == fund.Id);
        Assert.Equal(2, quotaState.VoluntaryLeaveQuota);
        Assert.Equal(2, quotaState.VoluntaryLeavesUsed);

        // A later pass on the same trading day cannot release the third eligible member. The two independent
        // leavers miss their ordinary join rolls during this synthetic second invocation of the same cycle.
        await Service(enabled: true, new ScriptedRandom([1.0d, 1.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.True(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == eligibleLeavers[2].Id));
    }

    [Fact]
    public async Task DailyQuotaUsesMembershipBeforeAdministrativeCapacityEviction()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m, tradingDayNumber: 20);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var newestCycle = await AddCycleForTradingDayAsync(dayNumber: 21, cycleNumber: 601);
        var (fund, _) = await AddFundAsync(balance: 200_000m);
        var eligibleLeavers = new List<Participant>();
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, joinedCycle.Id);
            eligibleLeavers.Add(member);
        }

        foreach (var _ in Enumerable.Range(0, 3))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        var capacityEvictee = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, capacityEvictee, deposit: 10_000m, newestCycle.Id);

        // Two exit rolls fire; the administratively evicted member then misses its ordinary join roll.
        await Service(enabled: true, new ScriptedRandom([0.0d, 0.0d, 1.0d], []), softCloseMembers: 6)
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == capacityEvictee.Id));
        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == eligibleLeavers[0].Id));
        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == eligibleLeavers[1].Id));
        Assert.True(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == eligibleLeavers[2].Id));
        var quotaState = await context.CollectiveFunds.AsNoTracking().SingleAsync(candidate => candidate.Id == fund.Id);
        Assert.Equal(2, quotaState.VoluntaryLeaveQuota);
        Assert.Equal(2, quotaState.VoluntaryLeavesUsed);
    }

    [Fact]
    public async Task LegacySameDayLeaveMarkerSeedsOneUsedQuotaSlot()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m, tradingDayNumber: 20);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, _) = await AddFundAsync(balance: 200_000m);
        fund.LastVoluntaryLeaveTradingDayNumber = 20;
        var eligibleLeavers = new List<Participant>();
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, joinedCycle.Id);
            eligibleLeavers.Add(member);
        }

        foreach (var _ in Enumerable.Range(0, 7))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == eligibleLeavers[0].Id));
        Assert.True(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == eligibleLeavers[1].Id));
        Assert.True(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == eligibleLeavers[2].Id));
        var quotaState = await context.CollectiveFunds.AsNoTracking().SingleAsync(candidate => candidate.Id == fund.Id);
        Assert.Equal(2, quotaState.VoluntaryLeaveQuota);
        Assert.Equal(2, quotaState.VoluntaryLeavesUsed);
    }

    [Fact]
    public async Task CompletedFundSwitchesConsumeTheDailyDepartureQuota()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m, tradingDayNumber: 20);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, _) = await AddFundAsync(balance: 200_000m);
        var eligibleSwitchers = new List<Participant>();
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, joinedCycle.Id);
            eligibleSwitchers.Add(member);
        }

        foreach (var _ in Enumerable.Range(0, 7))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        // The first two members miss independent exit and fire their switch rolls; the quota then skips the third.
        await Service(enabled: true, new ScriptedRandom([1.0d, 0.0d, 1.0d, 0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var quotaState = await context.CollectiveFunds.AsNoTracking().SingleAsync(candidate => candidate.Id == fund.Id);
        Assert.Equal(2, quotaState.VoluntaryLeaveQuota);
        Assert.Equal(2, quotaState.VoluntaryLeavesUsed);
        Assert.Equal(2, await context.CollectiveFundMembershipEvents.CountAsync(membershipEvent =>
            membershipEvent.CollectiveFundId == fund.Id && membershipEvent.Type == CollectiveFundMembershipEventType.Left));
        var heldSwitcher = await context.CollectiveFundParticipants.AsNoTracking()
            .SingleAsync(member => member.ParticipantId == eligibleSwitchers[2].Id);
        Assert.False(heldSwitcher.IsLeaving);
    }

    [Fact]
    public async Task MemberCannotLeaveBeforeConfiguredTradingDayTenure()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m, tradingDayNumber: 20);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 14, cycleNumber: 100);
        var (fund, _) = await AddFundAsync(balance: 200_000m);
        var protectedMember = await AddTraderAsync(currentBalance: ReferenceLargeBalance);
        await AddMembershipAsync(fund, protectedMember, deposit: 90_000m, joinedCycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        // Six trading days have elapsed, so the empty script proves that no leave roll is drawn yet.
        await Service(enabled: true, new ScriptedRandom([], []), minimumMembershipTradingDays: 7)
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var membership = await context.CollectiveFundParticipants.AsNoTracking()
            .SingleAsync(member => member.ParticipantId == protectedMember.Id);
        Assert.False(membership.IsLeaving);
        Assert.Equal(0, membership.LeaveRampCycles);
    }

    [Fact]
    public async Task MemberCanLeaveOnConfiguredTradingDayBoundary()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m, tradingDayNumber: 20);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, _) = await AddFundAsync(balance: 200_000m);
        var eligibleMember = await AddTraderAsync(currentBalance: ReferenceLargeBalance);
        await AddMembershipAsync(fund, eligibleMember, deposit: 90_000m, joinedCycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        await Service(enabled: true, new ScriptedRandom([0.0d], []), minimumMembershipTradingDays: 7)
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.False(await context.CollectiveFundParticipants
            .AnyAsync(member => member.ParticipantId == eligibleMember.Id));
    }

    [Fact]
    public async Task FundRaisesFifteenPercentCashBufferBeforeMemberBecomesEligible()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m, tradingDayNumber: 20);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 14, cycleNumber: 100);
        var (fund, fundParticipant) = await AddFundAsync(balance: 100m);
        await AddSharesAsync(fundParticipant.Id, company.Id, count: 9, price: 100m);
        var protectedMember = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, protectedMember, deposit: 90_000m, joinedCycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var sell = await context.Orders.AsNoTracking()
            .SingleAsync(order => order.ParticipantId == fundParticipant.Id && order.Type == OrderType.Sell);
        Assert.Equal(1, sell.Quantity);
        Assert.Equal(90m, sell.LimitPrice);
    }

    [Fact]
    public async Task FundCancelsBuysBeforeSellingToPrepareLeaveCash()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m, tradingDayNumber: 20);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 14, cycleNumber: 100);
        var (fund, fundParticipant) = await AddFundAsync(balance: 200m);
        fundParticipant.ReservedBalance = 100m;
        await AddSharesAsync(fundParticipant.Id, company.Id, count: 8, price: 100m);
        var buy = await AddOpenBuyOrderAsync(
            fundParticipant.Id,
            company.Id,
            quantity: 1,
            price: 100m,
            reserved: 100m,
            cycle.Id);
        var protectedMember = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, protectedMember, deposit: 90_000m, joinedCycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(OrderStatus.Cancelled, (await context.Orders.AsNoTracking().SingleAsync(order => order.Id == buy.Id)).Status);
        Assert.False(await context.Orders.AnyAsync(order => order.ParticipantId == fundParticipant.Id
            && order.Type == OrderType.Sell));
    }

    [Fact]
    public async Task PlayerManagedFundDoesNotTradeToPrepareLeaveCash()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m, tradingDayNumber: 20);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 14, cycleNumber: 100);
        var (fund, fundParticipant) = await AddFundAsync(balance: 100m);
        fund.IsPlayerManaged = true;
        await AddSharesAsync(fundParticipant.Id, company.Id, count: 9, price: 100m);
        var protectedMember = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, protectedMember, deposit: 90_000m, joinedCycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.False(await context.Orders.AnyAsync(order => order.ParticipantId == fundParticipant.Id));
    }

    [Fact]
    public async Task JoiningAFundRecordsAJoinedEvent()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 50_000m);
        var existing = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, existing, deposit: 50_000m, cycle.Id);
        var joiner = await AddTraderAsync(currentBalance: 100_000m);

        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var joinEvent = await context.CollectiveFundMembershipEvents.AsNoTracking()
            .SingleAsync(membershipEvent => membershipEvent.ParticipantId == joiner.Id);
        Assert.Equal(CollectiveFundMembershipEventType.Joined, joinEvent.Type);
        Assert.Equal(fund.Id, joinEvent.CollectiveFundId);
        Assert.Equal(fundParticipant.Id, joinEvent.FundParticipantId);
        Assert.Equal(90_000m, joinEvent.Amount);
        Assert.Equal(cycle.Id, joinEvent.CreatedInCycleId);
    }

    [Fact]
    public async Task ReturningADepositRecordsALeftEvent()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, fundParticipant) = await AddFundAsync(balance: 200_000m);
        var leaver = await AddTraderAsync(currentBalance: 150_000_000m);
        await AddMembershipAsync(fund, leaver, deposit: 90_000m, joinedCycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var leaveEvent = await context.CollectiveFundMembershipEvents.AsNoTracking()
            .SingleAsync(membershipEvent => membershipEvent.ParticipantId == leaver.Id);
        Assert.Equal(CollectiveFundMembershipEventType.Left, leaveEvent.Type);
        Assert.Equal(fundParticipant.Id, leaveEvent.FundParticipantId);
        Assert.Equal(90_000m, leaveEvent.Amount);
    }

    [Fact]
    public async Task ClosingSplitRecordsLeftEventsForEverySurvivor()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, fundParticipant) = await AddFundAsync(balance: 200_000m);
        var leaver = await AddTraderAsync(currentBalance: 150_000_000m);
        await AddMembershipAsync(fund, leaver, deposit: 90_000m, joinedCycle.Id);
        var partner = await AddTraderAsync(currentBalance: 450_000m);
        await AddMembershipAsync(fund, partner, deposit: 80_000m, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // The last pair unwinds and the 200k pot splits evenly, so both members get a 100k leave event.
        var leaveEvents = await context.CollectiveFundMembershipEvents.AsNoTracking()
            .Where(membershipEvent => membershipEvent.Type == CollectiveFundMembershipEventType.Left
                && membershipEvent.FundParticipantId == fundParticipant.Id)
            .ToListAsync();
        Assert.Equal(2, leaveEvents.Count);
        Assert.All(leaveEvents, membershipEvent => Assert.Equal(100_000m, membershipEvent.Amount));
        Assert.Contains(leaveEvents, membershipEvent => membershipEvent.ParticipantId == leaver.Id);
        Assert.Contains(leaveEvents, membershipEvent => membershipEvent.ParticipantId == partner.Id);
    }

    [Fact]
    public async Task LeaveBorrowsToPayInFullWhenFundIsShort()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, fundParticipant) = await AddFundAsync(balance: 1_000m);
        await AddSharesAsync(fundParticipant.Id, company.Id, count: 20, price: 100m);
        var leaver = await AddTraderAsync(currentBalance: 150_000_000m);
        await AddMembershipAsync(fund, leaver, deposit: 90_000m, joinedCycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // Cash (1,000) cannot cover the 90,000 deposit, so the fund borrows the 89,000 shortfall plus the 10%
        // buffer (97,900) and pays the leaver in full the same cycle rather than listing shares and waiting.
        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == leaver.Id));

        var refreshedLeaver = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == leaver.Id);
        Assert.Equal(150_090_000m, refreshedLeaver.CurrentBalance);

        var loan = await context.Loans.AsNoTracking().SingleAsync(candidate => candidate.ParticipantId == fundParticipant.Id);
        Assert.Equal(LoanStatus.Open, loan.Status);
        Assert.Equal(97_900m, loan.Principal);

        // The fund raises the cash by borrowing, not by dumping its shares, so no forced sell is listed.
        Assert.False(await context.Orders.AnyAsync(order => order.ParticipantId == fundParticipant.Id && order.Type == OrderType.Sell));

        var refreshedFund = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fundParticipant.Id);
        Assert.Equal(8_900m, refreshedFund.CurrentBalance);
    }

    [Fact]
    public async Task PlayerFundBorrowsToPayLeaverWhenUnderwater()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 0m);
        fund.IsPlayerManaged = true;
        await context.SaveChangesAsync();

        var leaver = await AddTraderAsync(currentBalance: 0m);
        // The deposit is large enough that the payout lifts the leaver above the join ceiling, so it does not
        // become a join candidate and no random is drawn in the join pass.
        await AddMembershipAsync(fund, leaver, deposit: 600_000m, cycle.Id, isLeaving: true);

        // No random draws: the member is already flagged leaving, and no eligible trader remains to join.
        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // A player fund never pair-closes; with no cash it borrows the deposit plus buffer (660,000) and pays the
        // member in full, then carries the debt.
        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == leaver.Id));
        var refreshedLeaver = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == leaver.Id);
        Assert.Equal(600_000m, refreshedLeaver.CurrentBalance);

        var loan = await context.Loans.AsNoTracking().SingleAsync(candidate => candidate.ParticipantId == fundParticipant.Id);
        Assert.Equal(LoanStatus.Open, loan.Status);
        Assert.Equal(660_000m, loan.Principal);

        var refreshedFund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.Active, refreshedFund.Status);
    }

    [Fact]
    public async Task MemberLeaveCannotUseUnsettledFundCash()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 90_000m);
        fund.IsPlayerManaged = true;
        fundParticipant.SettledCashBalance = 0m;
        var leaver = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(fund, leaver, deposit: 90_000m, cycle.Id, isLeaving: true);
        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom([], []), loansEnabled: false)
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.True(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == leaver.Id));
        Assert.Equal(0m, (await context.Participants.AsNoTracking()
            .SingleAsync(participant => participant.Id == fundParticipant.Id)).SettledCashBalance);
        Assert.Equal(1_000m, (await context.Participants.AsNoTracking()
            .SingleAsync(participant => participant.Id == leaver.Id)).CurrentBalance);
    }

    [Fact]
    public async Task LeaveFallsBackToSellingSharesWhenLoansDisabled()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, fundParticipant) = await AddFundAsync(balance: 1_000m);
        await AddSharesAsync(fundParticipant.Id, company.Id, count: 20, price: 100m);
        var leaver = await AddTraderAsync(currentBalance: 150_000_000m);
        await AddMembershipAsync(fund, leaver, deposit: 90_000m, joinedCycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        }

        await Service(enabled: true, new ScriptedRandom([0.0d], []), loansEnabled: false)
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // With loans off, the old behavior stands: the member waits and the fund lists shares to raise the cash.
        var membership = await context.CollectiveFundParticipants.AsNoTracking().FirstAsync(member => member.ParticipantId == leaver.Id);
        Assert.True(membership.IsLeaving);

        var sell = await context.Orders.AsNoTracking()
            .SingleAsync(order => order.ParticipantId == fundParticipant.Id && order.Type == OrderType.Sell);
        Assert.Equal(OrderStatus.Open, sell.Status);
        Assert.Equal(20, sell.Quantity);
        Assert.Equal(90m, sell.LimitPrice);
        Assert.False(await context.Loans.AnyAsync());
    }

    [Fact]
    public async Task LastPairClosesSplitsCashAndReactivatesMembers()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, fundParticipant) = await AddFundAsync(balance: 200_000m);
        var leaver = await AddTraderAsync(currentBalance: 150_000_000m);
        await AddMembershipAsync(fund, leaver, deposit: 90_000m, joinedCycle.Id);
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
    public async Task IndependentLeaverDoesNotRejoinWhenDelayedPairCloseFinishes()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m, tradingDayNumber: 20);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, fundParticipant) = await AddFundAsync(balance: 100_000m);
        await AddSharesAsync(fundParticipant.Id, company.Id, count: 10, price: 100m);
        var leaver = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, leaver, deposit: 90_000m, joinedCycle.Id);
        var partner = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, partner, deposit: 10_000m, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(CollectiveFundStatus.GoingToBeClosed, (await context.CollectiveFunds.AsNoTracking().SingleAsync()).Status);
        var holding = await context.Holdings.SingleAsync(candidate => candidate.ParticipantId == fundParticipant.Id);
        holding.Quantity = 0;
        holding.SettledQuantity = 0;
        var sell = await context.Orders.SingleAsync(order => order.ParticipantId == fundParticipant.Id && order.Type == OrderType.Sell);
        sell.Status = OrderStatus.Filled;
        sell.FilledQuantity = sell.Quantity;
        await context.SaveChangesAsync();

        var nextCycle = await AddCycleToTradingDayAsync(cycle.TradingDayId, cycleNumber: 601, tradingCycleNumber: 2);
        await Service(enabled: true, new ScriptedRandom([0.0d, 1.0d], []))
            .ProcessForCycleAsync(nextCycle.Id, nextCycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == leaver.Id));
    }

    [Fact]
    public async Task ClosingFundAllocatesResidualCentWithoutCreatingCash()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, _) = await AddFundAsync(status: CollectiveFundStatus.GoingToBeClosed, balance: 0.01m);
        var firstMember = await AddTraderAsync(currentBalance: 600_000m);
        var secondMember = await AddTraderAsync(currentBalance: 600_000m);
        await AddMembershipAsync(fund, firstMember, deposit: 1m, cycle.Id);
        await AddMembershipAsync(fund, secondMember, deposit: 1m, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        await context.Entry(firstMember).ReloadAsync();
        await context.Entry(secondMember).ReloadAsync();
        Assert.Equal(600_000.01m, firstMember.CurrentBalance);
        Assert.Equal(600_000m, secondMember.CurrentBalance);

        var payouts = await context.CollectiveFundMembershipEvents.AsNoTracking()
            .Where(membershipEvent => membershipEvent.CollectiveFundId == fund.Id
                && membershipEvent.Type == CollectiveFundMembershipEventType.Left)
            .OrderBy(membershipEvent => membershipEvent.ParticipantId)
            .Select(membershipEvent => membershipEvent.Amount)
            .ToListAsync();
        Assert.Equal([0.01m, 0m], payouts);
        Assert.Equal(0.01m, payouts.Sum());
    }

    [Fact]
    public async Task AdministrativelyClosedMatureMemberStillRunsTheJoinOpenPass()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, _) = await AddFundAsync(status: CollectiveFundStatus.GoingToBeClosed, balance: 100_000m);
        var member = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, member, deposit: 90_000m, cycle.Id, leaveRamp: 1);

        // The administrative close releases the member, whose 0.0 roll then opens a new fund.
        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var newMembership = await context.CollectiveFundParticipants.AsNoTracking()
            .SingleAsync(candidate => candidate.ParticipantId == member.Id);
        Assert.NotEqual(fund.Id, newMembership.CollectiveFundId);
    }

    [Fact]
    public async Task ClosingFundExcludesStaleMembershipFromPayoutSplit()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, _) = await AddFundAsync(status: CollectiveFundStatus.GoingToBeClosed, balance: 1_000m);
        var member = await AddTraderAsync(currentBalance: 600_000m);
        await AddMembershipAsync(fund, member, deposit: 1_000m, cycle.Id);
        context.CollectiveFundParticipants.Add(new CollectiveFundParticipant
        {
            CollectiveFundId = fund.Id,
            ParticipantId = 999_999,
            JoinedAt = DateTime.UtcNow,
            JoinedInCycleId = cycle.Id,
            DepositAmount = 1_000m,
        });
        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        await context.Entry(member).ReloadAsync();
        Assert.Equal(601_000m, member.CurrentBalance);
        var payouts = await context.CollectiveFundMembershipEvents.AsNoTracking()
            .Where(membershipEvent => membershipEvent.CollectiveFundId == fund.Id)
            .ToDictionaryAsync(membershipEvent => membershipEvent.ParticipantId, membershipEvent => membershipEvent.Amount);
        Assert.Equal(1_000m, payouts[member.Id]);
        Assert.Equal(0m, payouts[999_999]);
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
        // Listing a fund sell does not reduce the position; all fifteen shares remain held until sold.
        Assert.Equal(15, await context.Holdings.Where(holding => holding.ParticipantId == fundParticipant.Id).SumAsync(holding => holding.Quantity));
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

        // Half of the 1,000 receipt (500) is shared 1:3 by deposit as the gross dividend (125 and 375), then each
        // member pays the 20% fee back, netting 100 and 300.
        var refreshedOne = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == memberOne.Id);
        var refreshedTwo = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == memberTwo.Id);
        Assert.Equal(100m, refreshedOne.CurrentBalance);
        Assert.Equal(300m, refreshedTwo.CurrentBalance);

        // The fund keeps the un-passed half plus the collected fees (25 + 75).
        var refreshedFund = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fundParticipant.Id);
        Assert.Equal(600m, refreshedFund.CurrentBalance);

        // Each member receives the full gross dividend as its own row before the fee is charged.
        var dividendRows = await context.MoneyTransactions.AsNoTracking()
            .Where(transaction => transaction.Type == MoneyTransactionType.CollectiveFundDividend)
            .OrderBy(transaction => transaction.Amount)
            .Select(transaction => transaction.Amount)
            .ToListAsync();
        Assert.Equal([125m, 375m], dividendRows);

        // The fee each member pays back to the fund is recorded on the member's side.
        var feeRows = await context.MoneyTransactions.AsNoTracking()
            .Where(transaction => transaction.Type == MoneyTransactionType.CollectiveFundDividendFee)
            .OrderBy(transaction => transaction.Amount)
            .Select(transaction => transaction.Amount)
            .ToListAsync();
        Assert.Equal([25m, 75m], feeRows);

        // The fund records paying the full gross out and collecting the fees back, so both legs appear in its cash movements.
        var fundOutflow = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.ParticipantId == fundParticipant.Id
                && transaction.Type == MoneyTransactionType.CollectiveFundDividendPaid);
        Assert.Equal(500m, fundOutflow.Amount);
        var fundFee = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.ParticipantId == fundParticipant.Id
                && transaction.Type == MoneyTransactionType.CollectiveFundDividendFeeReceived);
        Assert.Equal(100m, fundFee.Amount);
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
            Options.Create(new RandomChanceRatesOptions()),
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

    [Fact]
    public async Task IdleFundCounterGrowsWhenSharelessAndBroke()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        // Spendable cash 90 (after the 10% buffer) cannot cover the cheapest share at 100, so the fund is idle.
        var (fund, _) = await AddFundAsync(balance: 100m);
        var memberOne = await AddTraderAsync(currentBalance: 1_000m);
        var memberTwo = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(fund, memberOne, deposit: 1_000m, cycle.Id);
        await AddMembershipAsync(fund, memberTwo, deposit: 1_000m, cycle.Id);

        // Members are below the tenure gate and stay members; the idle check draws nothing.
        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.Active, refreshed.Status);
        Assert.Equal(1, refreshed.IdleCycles);
    }

    [Fact]
    public async Task IdleCounterResetsAtBufferBoundary()
    {
        var (_, cycle, _) = await SeedAsync(price: 90m);
        // Spendable cash is exactly 90 (100 minus the 10% buffer), meeting the cheapest share, so it is not idle.
        var (fund, _) = await AddFundAsync(balance: 100m, idleCycles: 5);
        var memberOne = await AddTraderAsync(currentBalance: 1_000m);
        var memberTwo = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(fund, memberOne, deposit: 1_000m, cycle.Id);
        await AddMembershipAsync(fund, memberTwo, deposit: 1_000m, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.Active, refreshed.Status);
        Assert.Equal(0, refreshed.IdleCycles);
    }

    [Fact]
    public async Task IdleFundClosesAtTwentyIdleCycles()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 100m, idleCycles: 19);
        // Wealthy members skip the re-pool draw once the fund releases them, keeping the script empty.
        var memberOne = await AddTraderAsync(currentBalance: 600_000m);
        var memberTwo = await AddTraderAsync(currentBalance: 600_000m);
        await AddMembershipAsync(fund, memberOne, deposit: 1_000m, cycle.Id);
        await AddMembershipAsync(fund, memberTwo, deposit: 1_000m, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.Closed, refreshed.Status);
        Assert.Equal(0, await context.CollectiveFundParticipants.CountAsync());

        var refreshedFundParticipant = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fundParticipant.Id);
        Assert.False(refreshedFundParticipant.IsActive);
    }

    [Fact]
    public async Task FinalizeCloseFlagsDevastatedMembersOnZeroPayout()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        // A broke fund closing with nothing to hand back flags every member as a devastating loss.
        var (fund, _) = await AddFundAsync(status: CollectiveFundStatus.GoingToBeClosed, balance: 0m);
        var memberOne = await AddTraderAsync(currentBalance: 600_000m);
        var memberTwo = await AddTraderAsync(currentBalance: 600_000m);
        await AddMembershipAsync(fund, memberOne, deposit: 10_000m, cycle.Id);
        await AddMembershipAsync(fund, memberTwo, deposit: 10_000m, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedFund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.Closed, refreshedFund.Status);

        var refreshedOne = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == memberOne.Id);
        var refreshedTwo = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == memberTwo.Id);
        Assert.True(refreshedOne.PendingFundLossExitRoll);
        Assert.True(refreshedTwo.PendingFundLossExitRoll);
    }

    [Fact]
    public async Task ClosingFundWaitsForPendingSettlement()
    {
        var (_, cycle, company) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(status: CollectiveFundStatus.GoingToBeClosed, balance: 100m);
        var member = await AddTraderAsync(currentBalance: 600_000m);
        await AddMembershipAsync(fund, member, deposit: 100m, cycle.Id);
        var trade = new ShareTransaction
        {
            SellerId = fundParticipant.Id,
            BuyerId = member.Id,
            CompanyId = company.Id,
            Quantity = 1,
            Price = 100m,
            TotalCost = 100m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.ShareTransactions.Add(trade);
        context.SettlementInstructions.Add(new SettlementInstruction
        {
            ShareTransaction = trade,
            SellerId = fundParticipant.Id,
            BuyerId = member.Id,
            CompanyId = company.Id,
            Quantity = 1,
            CashAmount = 100m,
            TradeDayNumber = 1,
            DueDayNumber = 2,
            Status = SettlementStatus.Pending,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(CollectiveFundStatus.GoingToBeClosed, (await context.CollectiveFunds.AsNoTracking().SingleAsync()).Status);
        Assert.True(await context.CollectiveFundParticipants.AnyAsync(candidate => candidate.CollectiveFundId == fund.Id));
    }

    [Fact]
    public async Task ClosingFundWaitsForMarginLiability()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(status: CollectiveFundStatus.GoingToBeClosed, balance: 100m);
        var member = await AddTraderAsync(currentBalance: 600_000m);
        await AddMembershipAsync(fund, member, deposit: 100m, cycle.Id);
        context.MarginAccounts.Add(new MarginAccount
        {
            ParticipantId = fundParticipant.Id,
            DebitBalance = 100m,
            AccruedInterest = 5m,
            InitialMarginRate = 0.50m,
            MaintenanceMarginRate = 0.25m,
            Status = MarginAccountStatus.Active,
        });
        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(CollectiveFundStatus.GoingToBeClosed, (await context.CollectiveFunds.AsNoTracking().SingleAsync()).Status);
        Assert.True(await context.CollectiveFundParticipants.AnyAsync(candidate => candidate.CollectiveFundId == fund.Id));
    }

    [Fact]
    public async Task ClosingFundWaitsUntilEconomicCashIsSettled()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(status: CollectiveFundStatus.GoingToBeClosed, balance: 100m);
        fundParticipant.SettledCashBalance = 0m;
        var member = await AddTraderAsync(currentBalance: 600_000m);
        await AddMembershipAsync(fund, member, deposit: 100m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(CollectiveFundStatus.GoingToBeClosed, (await context.CollectiveFunds.AsNoTracking().SingleAsync()).Status);
        Assert.True(await context.CollectiveFundParticipants.AnyAsync(candidate => candidate.CollectiveFundId == fund.Id));
    }

    [Fact]
    public async Task FundClosingDuringACrisisIsLoggedToTheTimeline()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(status: CollectiveFundStatus.GoingToBeClosed, balance: 0m);
        var member = await AddTraderAsync(currentBalance: 600_000m);
        await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        var crisis = await AddCrisisAsync(cycle);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow, crisis);
        await context.SaveChangesAsync();

        Assert.Equal(CollectiveFundStatus.Closed, (await context.CollectiveFunds.AsNoTracking().SingleAsync()).Status);

        var timelineEvent = await context.CrisisEvents.AsNoTracking()
            .SingleAsync(row => row.Type == CrisisEventType.FundClosed);
        Assert.Equal(crisis.Id, timelineEvent.CrisisId);
        Assert.Contains(fundParticipant.Name, timelineEvent.Description);
        Assert.Null(timelineEvent.CompanyId); // a fund is a participant, not a company
    }

    [Fact]
    public async Task JoinerPrefersTheBetterScoringFund()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);

        // A tiny fund and a bigger, wealthier one both have room; the joiner should pick the stronger of the two.
        var (smallFund, _) = await AddFundAsync(balance: 10_000m);
        var smallMember = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(smallFund, smallMember, deposit: 10_000m, cycle.Id);

        var (bigFund, _) = await AddFundAsync(balance: 500_000m);
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var member = await AddTraderAsync(currentBalance: 1_000m);
            await AddMembershipAsync(bigFund, member, deposit: 100_000m, cycle.Id);
        }

        var joiner = await AddTraderAsync(currentBalance: 100_000m);

        // Only the joiner draws; 0.0 lands in the 5% join band.
        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var membership = await context.CollectiveFundParticipants.AsNoTracking()
            .FirstAsync(member => member.ParticipantId == joiner.Id);
        Assert.Equal(bigFund.Id, membership.CollectiveFundId);
    }

    [Fact]
    public async Task MemberDoesNotSwitchBeforeMinimumTenure()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, _) = await AddFundAsync(balance: 500_000m);
        // A short-tenure member is not yet eligible to shop around, so the loop draws nothing for it.
        var young = await AddTraderAsync(currentBalance: 50_000m);
        await AddMembershipAsync(fund, young, deposit: 90_000m, cycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 50_000m);
            await AddMembershipAsync(fund, member, deposit: 90_000m, cycle.Id);
        }

        // A second fund with room exists, so a switch would have somewhere to go if one were allowed.
        var (otherFund, _) = await AddFundAsync(balance: 500_000m);
        var otherMember = await AddTraderAsync(currentBalance: 50_000m);
        await AddMembershipAsync(otherFund, otherMember, deposit: 90_000m, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var membership = await context.CollectiveFundParticipants.AsNoTracking().FirstAsync(member => member.ParticipantId == young.Id);
        Assert.Equal(fund.Id, membership.CollectiveFundId);
        Assert.False(membership.IsLeaving);
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == young.Id);
        Assert.False(refreshed.PendingFundSwitch);
    }

    [Fact]
    public async Task AggressiveMemberSwitchesOnRaisedChance()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        // Loans are off here so the switch is observed in isolation: the fund cannot cover the deposit and owns
        // nothing, so the switcher stays in the waiting-to-leave state rather than being borrowed out immediately.
        var (fund, _) = await AddFundAsync(balance: 1_000m);
        var switcher = await AddTraderAsync(currentBalance: 50_000m, temperament: Temperament.Aggressive);
        await AddMembershipAsync(fund, switcher, deposit: 90_000m, joinedCycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 50_000m);
            await AddMembershipAsync(fund, member, deposit: 90_000m, cycle.Id);
        }

        // The independent-exit roll misses, then 0.18 fires the aggressive member's 20% switch chance.
        await Service(enabled: true, new ScriptedRandom([1.0d, 0.18d], []), loansEnabled: false)
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var membership = await context.CollectiveFundParticipants.AsNoTracking().FirstAsync(member => member.ParticipantId == switcher.Id);
        Assert.True(membership.IsLeaving);
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == switcher.Id);
        Assert.True(refreshed.PendingFundSwitch);
    }

    [Fact]
    public async Task ConservativeMemberStaysOnLoweredChance()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, _) = await AddFundAsync(balance: 1_000m);
        var conservative = await AddTraderAsync(currentBalance: 50_000m, temperament: Temperament.Conservative);
        await AddMembershipAsync(fund, conservative, deposit: 90_000m, joinedCycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 50_000m);
            await AddMembershipAsync(fund, member, deposit: 90_000m, cycle.Id);
        }

        // The independent-exit roll misses, then 0.12 misses the conservative member's lowered 10% switch chance.
        await Service(enabled: true, new ScriptedRandom([1.0d, 0.12d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var membership = await context.CollectiveFundParticipants.AsNoTracking().FirstAsync(member => member.ParticipantId == conservative.Id);
        Assert.False(membership.IsLeaving);
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == conservative.Id);
        Assert.False(refreshed.PendingFundSwitch);
    }

    [Fact]
    public async Task FounderNeverSwitches()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        var (fund, _) = await AddFundAsync(balance: 1_000m);
        var founder = await AddTraderAsync(currentBalance: 50_000m, temperament: Temperament.Aggressive);
        await AddMembershipAsync(fund, founder, deposit: 90_000m, joinedCycle.Id);
        var other = await AddTraderAsync(currentBalance: 50_000m);
        await AddMembershipAsync(fund, other, deposit: 90_000m, cycle.Id);

        // Point the fund's founder field at the real member; a founder is excluded from the switch roll.
        fund.FoundedByParticipantId = founder.Id;
        await context.SaveChangesAsync();

        // A second fund gives a switch somewhere to go; the founder misses its exit roll and takes no switch roll.
        var (otherFund, _) = await AddFundAsync(balance: 500_000m);
        var member2 = await AddTraderAsync(currentBalance: 50_000m);
        await AddMembershipAsync(otherFund, member2, deposit: 90_000m, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([1.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var membership = await context.CollectiveFundParticipants.AsNoTracking().FirstAsync(member => member.ParticipantId == founder.Id);
        Assert.Equal(fund.Id, membership.CollectiveFundId);
        Assert.False(membership.IsLeaving);
    }

    [Fact]
    public async Task SwitchingMemberJoinsBestFundIgnoringCashCeiling()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        // A rich, fund-less trader already flagged to switch must land in a fund despite being over the ceiling.
        var switcher = await AddTraderAsync(currentBalance: 600_000m, pendingFundSwitch: true);

        var (target, _) = await AddFundAsync(balance: 500_000m);
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var member = await AddTraderAsync(currentBalance: 1_000m);
            await AddMembershipAsync(target, member, deposit: 100_000m, cycle.Id);
        }

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var membership = await context.CollectiveFundParticipants.AsNoTracking().FirstAsync(member => member.ParticipantId == switcher.Id);
        Assert.Equal(target.Id, membership.CollectiveFundId);
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == switcher.Id);
        Assert.False(refreshed.PendingFundSwitch);
    }

    [Fact]
    public async Task MemberSwitchesToBetterFundInOneCycleWhenDepositIsCovered()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var joinedCycle = await AddCycleForTradingDayAsync(dayNumber: 13, cycleNumber: 100);
        // The current fund holds enough cash to return the deposit immediately, so the switch completes this cycle.
        var (fromFund, _) = await AddFundAsync(balance: 200_000m);
        var switcher = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fromFund, switcher, deposit: 90_000m, joinedCycle.Id);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var member = await AddTraderAsync(currentBalance: 10_000m);
            await AddMembershipAsync(fromFund, member, deposit: 10_000m, cycle.Id);
        }

        var (toFund, _) = await AddFundAsync(balance: 500_000m);
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var member = await AddTraderAsync(currentBalance: 1_000m);
            await AddMembershipAsync(toFund, member, deposit: 100_000m, cycle.Id);
        }

        // The independent-exit roll misses, then 0.0 fires the switch; the covered deposit lets the member rejoin
        // the stronger fund in the same cycle.
        await Service(enabled: true, new ScriptedRandom([1.0d, 0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var membership = await context.CollectiveFundParticipants.AsNoTracking().FirstAsync(member => member.ParticipantId == switcher.Id);
        Assert.Equal(toFund.Id, membership.CollectiveFundId);
        Assert.Equal(2, await context.CollectiveFundParticipants.CountAsync(member => member.CollectiveFundId == fromFund.Id));
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == switcher.Id);
        Assert.False(refreshed.PendingFundSwitch);
    }

    [Fact]
    public async Task FounderClosesFundAfterCollapseFromPeak()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        // Worth of 100k against a one-million peak is well under the 15% floor, so the founder unwinds the fund.
        // Wealthy members skip the re-pool draw once the fund releases them, keeping the script empty.
        var (fund, fundParticipant) = await AddFundAsync(balance: 100_000m, peakNetWorth: 1_000_000m);
        var memberOne = await AddTraderAsync(currentBalance: 600_000m);
        var memberTwo = await AddTraderAsync(currentBalance: 600_000m);
        await AddMembershipAsync(fund, memberOne, deposit: 10_000m, cycle.Id);
        await AddMembershipAsync(fund, memberTwo, deposit: 10_000m, cycle.Id);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.Closed, refreshed.Status);
        var refreshedFundParticipant = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fundParticipant.Id);
        Assert.False(refreshedFundParticipant.IsActive);
    }

    [Fact]
    public async Task FounderClosesFundWhenDividendStarved()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        // The fund was founded before both recent payout cycles yet received nothing from either, so it unwinds.
        // Wealthy members skip the re-pool draw once the fund releases them, keeping the script empty.
        var (fund, _) = await AddFundAsync(balance: 50_000m, createdInCycleId: 0);
        var memberOne = await AddTraderAsync(currentBalance: 600_000m);
        var memberTwo = await AddTraderAsync(currentBalance: 600_000m);
        await AddMembershipAsync(fund, memberOne, deposit: 10_000m, cycle.Id);
        await AddMembershipAsync(fund, memberTwo, deposit: 10_000m, cycle.Id);

        // Two distinct post-founding payout cycles paid a member, never the fund.
        await AddDividendReceiptAsync(memberOne.Id, cycleId: 1, amount: 500m);
        await AddDividendReceiptAsync(memberOne.Id, cycleId: 2, amount: 500m);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.Closed, refreshed.Status);
    }

    [Fact]
    public async Task OrphanedMembershipIsDroppedWhenParticipantIsGone()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        // Enough cash that the fund is neither idle nor collapsed, so it stays active and reaches the member pass.
        var (fund, _) = await AddFundAsync(balance: 500_000m);
        var memberOne = await AddTraderAsync(currentBalance: 1_000m);
        var memberTwo = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(fund, memberOne, deposit: 1_000m, cycle.Id);
        await AddMembershipAsync(fund, memberTwo, deposit: 1_000m, cycle.Id);

        // A member whose participant row was deleted by the market-exit service, leaving its membership behind.
        var ghost = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(fund, ghost, deposit: 1_000m, cycle.Id);
        var ghostId = ghost.Id;
        context.Participants.Remove(ghost);
        await context.SaveChangesAsync();

        // Members sit below the tenure gate, so an empty script must never
        // be drawn from — the orphan is dropped without a roll rather than throwing on the missing key.
        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.False(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == ghostId));
        Assert.Equal(2, await context.CollectiveFundParticipants.CountAsync(member => member.CollectiveFundId == fund.Id));
        var refreshed = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.Active, refreshed.Status);
    }

    [Fact]
    public async Task GrowingFundPostsGrowthNewswire()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 500_000m);
        var member = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, member, deposit: 90_000m, cycle.Id);

        // Net worth doubled across the window, far past the 2% growth threshold.
        await AddRisingWorthSeriesAsync(fundParticipant.Id, from: 100_000m, to: 200_000m);

        // The member is below the tenure gate and skipped as an existing member in the
        // join pass, so nothing draws; posting the growth headline draws nothing either.
        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var post = await context.NewsPosts.AsNoTracking()
            .SingleAsync(newsPost => newsPost.Category == NewsCategory.FundPerformance);
        Assert.Equal(NewsImpactScope.None, post.Scope);
        Assert.Contains(fundParticipant.Name, post.Title);

        var refreshedFund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(cycle.CycleNumber, refreshedFund.LastGrowthNewsInCycleNumber);
    }

    [Fact]
    public async Task GrowthNewswireRespectsCooldown()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 500_000m);
        var member = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, member, deposit: 90_000m, cycle.Id);
        await AddRisingWorthSeriesAsync(fundParticipant.Id, from: 100_000m, to: 200_000m);

        // A headline went out only five cycles ago, inside the 25-cycle cooldown, so no fresh one is posted.
        fund.LastGrowthNewsInCycleNumber = cycle.CycleNumber - 5;
        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.False(await context.NewsPosts.AnyAsync(newsPost => newsPost.Category == NewsCategory.FundPerformance));
        var refreshedFund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(cycle.CycleNumber - 5, refreshedFund.LastGrowthNewsInCycleNumber);
    }

    [Fact]
    public async Task GrowingJoinableFundRaisesTheJoinChance()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 500_000m);
        var founder = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, founder, deposit: 90_000m, cycle.Id);
        await AddRisingWorthSeriesAsync(fundParticipant.Id, from: 100_000m, to: 200_000m);

        var joiner = await AddTraderAsync(currentBalance: 100_000m);

        // 0.10 clears neither the 5% base chance nor the 8% join+open band, but the +15% growth bonus lifts the
        // join chance to 20%, so the growing fund pulls the joiner in rather than it opening its own.
        await Service(enabled: true, new ScriptedRandom([0.10d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.CollectiveFunds.CountAsync());
        Assert.True(await context.CollectiveFundParticipants
            .AnyAsync(membership => membership.ParticipantId == joiner.Id && membership.CollectiveFundId == fund.Id));
    }

    [Fact]
    public async Task JoinerPrefersTheGrowingFund()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);

        // Two funds even on size, worth, and dividends; only the second is growing, so the growth score decides.
        var (flatFund, _) = await AddFundAsync(balance: 100_000m);
        var flatMember = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(flatFund, flatMember, deposit: 10_000m, cycle.Id);

        var (growingFund, growingParticipant) = await AddFundAsync(balance: 100_000m);
        var growingMember = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(growingFund, growingMember, deposit: 10_000m, cycle.Id);
        await AddRisingWorthSeriesAsync(growingParticipant.Id, from: 100_000m, to: 200_000m);

        var joiner = await AddTraderAsync(currentBalance: 100_000m);

        // Only the joiner draws; a growing fund is joinable so 0.0 lands inside the raised band, and the growth
        // score breaks the otherwise-even tie toward the growing fund.
        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var membership = await context.CollectiveFundParticipants.AsNoTracking()
            .FirstAsync(member => member.ParticipantId == joiner.Id);
        Assert.Equal(growingFund.Id, membership.CollectiveFundId);
    }

    [Fact]
    public async Task JoinerPrefersTheMorePopularFund()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);

        // Two funds even on size, worth, dividends, and growth; only the second is advertised, so the popularity
        // score breaks the otherwise-even tie toward it.
        var (plainFund, _) = await AddFundAsync(balance: 100_000m);
        var plainMember = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(plainFund, plainMember, deposit: 10_000m, cycle.Id);

        var (popularFund, _) = await AddFundAsync(balance: 100_000m);
        var popularMember = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(popularFund, popularMember, deposit: 10_000m, cycle.Id);
        popularFund.PopularityIndex = 5;
        await context.SaveChangesAsync();

        var joiner = await AddTraderAsync(currentBalance: 100_000m);

        // Only the joiner draws, and 0.0 lands inside the 5% join band.
        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var membership = await context.CollectiveFundParticipants.AsNoTracking()
            .FirstAsync(member => member.ParticipantId == joiner.Id);
        Assert.Equal(popularFund.Id, membership.CollectiveFundId);
    }

    [Fact]
    public async Task JoinerAvoidsACrowdedFundForARoomierPeerOfEqualStrength()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);

        // Two funds of equal worth; one sits near its member cap and one has room. Capacity damping should send
        // the joiner to the roomier fund even though the crowded one carries far more members.
        var (crowdedFund, _) = await AddFundAsync(balance: 100_000m);
        for (var index = 0; index < 18; index++)
        {
            var member = await AddTraderAsync(currentBalance: 1_000m);
            await AddMembershipAsync(crowdedFund, member, deposit: 1_000m, cycle.Id);
        }

        var (roomyFund, _) = await AddFundAsync(balance: 100_000m);
        var roomyMember = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(roomyFund, roomyMember, deposit: 1_000m, cycle.Id);

        var joiner = await AddTraderAsync(currentBalance: 100_000m);

        // Only the joiner draws; 0.0 lands in the 5% join band and on the top-weighted (roomiest) fund.
        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var membership = await context.CollectiveFundParticipants.AsNoTracking()
            .FirstAsync(member => member.ParticipantId == joiner.Id);
        Assert.Equal(roomyFund.Id, membership.CollectiveFundId);
    }

    [Fact]
    public async Task JoinWheelCanSendAJoinerToALowerScoringFund()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);

        // A wealthier and a leaner fund both have room. The wheel makes the strong fund likeliest, not certain: a
        // join roll landing high in the band walks past it onto the weaker fund, so joiners spread rather than all
        // piling into the single strongest fund.
        var (strongFund, _) = await AddFundAsync(balance: 500_000m);
        var strongMember = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(strongFund, strongMember, deposit: 10_000m, cycle.Id);

        var (weakFund, _) = await AddFundAsync(balance: 100_000m);
        var weakMember = await AddTraderAsync(currentBalance: 1_000m);
        await AddMembershipAsync(weakFund, weakMember, deposit: 10_000m, cycle.Id);

        var joiner = await AddTraderAsync(currentBalance: 100_000m);

        // 0.04 clears the join test (below the 5% band) but sits near its top, so selectionPoint ≈ 0.8 walks past
        // the strong fund's slice of the wheel onto the weaker fund.
        await Service(enabled: true, new ScriptedRandom([0.04d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var membership = await context.CollectiveFundParticipants.AsNoTracking()
            .FirstAsync(member => member.ParticipantId == joiner.Id);
        Assert.Equal(weakFund.Id, membership.CollectiveFundId);
    }

    // Popularity ebbs one point per cycle once the last advertisement is more than the idle window behind.
    [Fact]
    public async Task PopularityDecaysWhenTheLastAdvertisementIsBeyondTheIdleWindow()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m, cycleNumber: 100);
        var (fund, _) = await AddFundAsync(balance: 500_000m);
        var member = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        fund.PopularityIndex = 3;
        fund.LastAdvertisedInCycleNumber = cycle.CycleNumber - 21;
        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(2, refreshed.PopularityIndex);
    }

    // Advertising within the idle window holds popularity steady.
    [Fact]
    public async Task PopularityHoldsWhenAdvertisedWithinTheIdleWindow()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m, cycleNumber: 100);
        var (fund, _) = await AddFundAsync(balance: 500_000m);
        var member = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);
        fund.PopularityIndex = 3;
        fund.LastAdvertisedInCycleNumber = cycle.CycleNumber - 20;
        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(3, refreshed.PopularityIndex);
    }

    [Fact]
    public async Task RaisedJoinCeilingLetsRicherTraderJoin()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, _) = await AddFundAsync(balance: 500_000m);
        var member = await AddTraderAsync(currentBalance: 10_000m);
        await AddMembershipAsync(fund, member, deposit: 90_000m, cycle.Id);

        // 1,000,000 cash is above the default 500k ceiling but below the raised 2M one, so the trader joins.
        var richJoiner = await AddTraderAsync(currentBalance: 1_000_000m);

        var loanOptions = Options.Create(new LoanOptions { Enabled = true });
        var service = new CollectiveFundService(
            context,
            Options.Create(new CollectiveFundOptions { Enabled = true, JoinBalanceCeiling = 2_000_000m }),
            Options.Create(new RandomChanceRatesOptions()),
            loanOptions,
            new LoanService(context, loanOptions),
            new ScriptedRandom([0.0d], []));
        await service.ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.True(await context.CollectiveFundParticipants
            .AnyAsync(membership => membership.ParticipantId == richJoiner.Id && membership.CollectiveFundId == fund.Id));
    }

    private async Task<Crisis> AddCrisisAsync(MarketCycle cycle)
    {
        var crisis = new Crisis
        {
            Title = "Shock",
            Content = "Body",
            Scope = CrisisScope.Global,
            TriggeredInCycleId = cycle.Id,
            TriggeredInCycleNumber = cycle.CycleNumber,
            DurationCycles = 20,
            TriggeredAt = DateTime.UtcNow,
        };
        context.Crises.Add(crisis);
        await context.SaveChangesAsync();
        return crisis;
    }

    [Fact]
    public async Task ManagerDrawsTenPercentOfDailyFeesAtDayClose()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 1_000m);
        var manager = await AddTraderAsync(currentBalance: 5_000m);
        fund.FoundedByParticipantId = manager.Id;
        await context.SaveChangesAsync();

        // The fund collected 400 in payout fees across the trading day.
        await AddFeeReceiptAsync(fundParticipant.Id, cycle.Id, 400m);

        await Service(enabled: true, new ScriptedRandom([], []))
            .PayManagerFeesForTradingDayAsync(cycle.TradingDayId, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // 10% of the day's fees leaves the fund and reaches the manager.
        var refreshedFund = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fundParticipant.Id);
        var refreshedManager = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == manager.Id);
        Assert.Equal(960m, refreshedFund.CurrentBalance);
        Assert.Equal(5_040m, refreshedManager.CurrentBalance);

        var fundRow = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.ParticipantId == fundParticipant.Id
                && transaction.Type == MoneyTransactionType.CollectiveFundManagerFee);
        Assert.Equal(40m, fundRow.Amount);
        var managerRow = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.ParticipantId == manager.Id
                && transaction.Type == MoneyTransactionType.CollectiveFundManagerFeeReceived);
        Assert.Equal(40m, managerRow.Amount);
    }

    [Fact]
    public async Task ManagerFeeSkippedWhenFundLacksCash()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 20m);
        var manager = await AddTraderAsync(currentBalance: 5_000m);
        fund.FoundedByParticipantId = manager.Id;
        await context.SaveChangesAsync();

        // 10% of the day's 400 fees is 40, but the fund holds only 20 in cash, so the payment is skipped.
        await AddFeeReceiptAsync(fundParticipant.Id, cycle.Id, 400m);

        await Service(enabled: true, new ScriptedRandom([], []))
            .PayManagerFeesForTradingDayAsync(cycle.TradingDayId, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedFund = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fundParticipant.Id);
        var refreshedManager = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == manager.Id);
        Assert.Equal(20m, refreshedFund.CurrentBalance);
        Assert.Equal(5_000m, refreshedManager.CurrentBalance);
        Assert.False(await context.MoneyTransactions.AnyAsync(transaction => transaction.Type == MoneyTransactionType.CollectiveFundManagerFee));
    }

    [Fact]
    public async Task PlayerManagedFundAlsoPaysManagerFee()
    {
        var (_, cycle, _) = await SeedAsync(price: 100m);
        var (fund, fundParticipant) = await AddFundAsync(balance: 1_000m);
        fund.IsPlayerManaged = true;
        var owner = await AddTraderAsync(currentBalance: 5_000m);
        fund.FoundedByParticipantId = owner.Id;
        await context.SaveChangesAsync();

        await AddFeeReceiptAsync(fundParticipant.Id, cycle.Id, 400m);

        await Service(enabled: true, new ScriptedRandom([], []))
            .PayManagerFeesForTradingDayAsync(cycle.TradingDayId, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // A player fund is swept like any other: its owner draws 10% of the day's fees on top of manual withdrawal.
        var refreshedFund = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fundParticipant.Id);
        var refreshedOwner = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == owner.Id);
        Assert.Equal(960m, refreshedFund.CurrentBalance);
        Assert.Equal(5_040m, refreshedOwner.CurrentBalance);
        var ownerRow = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.ParticipantId == owner.Id
                && transaction.Type == MoneyTransactionType.CollectiveFundManagerFeeReceived);
        Assert.Equal(40m, ownerRow.Amount);
    }

    private async Task<(Market Market, MarketCycle Cycle, Company Company)> SeedAsync(
        decimal price,
        int cycleNumber = 600,
        int tradingDayNumber = 20)
    {
        var now = DateTime.UtcNow;

        var cycle = await AddCycleForTradingDayAsync(tradingDayNumber, cycleNumber);

        var market = new Market
        {
            Name = "Demo Market",
            Status = MarketStatus.Running,
            CurrentCycleId = cycle.Id,
            CurrentTradingDayId = cycle.TradingDayId,
            CreatedAt = now,
            UpdatedAt = now,
        };
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

        await context.SaveChangesAsync();
        return (market, cycle, company);
    }

    private async Task<MarketCycle> AddCycleForTradingDayAsync(int dayNumber, int cycleNumber)
    {
        var day = new TradingDay
        {
            DayNumber = dayNumber,
            State = TradingSessionState.Trading,
            OpenedInCycleId = 0,
        };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();

        var cycle = new MarketCycle
        {
            CycleNumber = cycleNumber,
            TradingDayId = day.Id,
            TradingCycleNumber = 1,
            Status = CycleStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();

        day.OpenedInCycleId = cycle.Id;
        await context.SaveChangesAsync();
        return cycle;
    }

    private async Task<MarketCycle> AddCycleToTradingDayAsync(int tradingDayId, int cycleNumber, int tradingCycleNumber)
    {
        var cycle = new MarketCycle
        {
            CycleNumber = cycleNumber,
            TradingDayId = tradingDayId,
            TradingCycleNumber = tradingCycleNumber,
            Status = CycleStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();
        return cycle;
    }

    private async Task<Participant> AddTraderAsync(
        decimal currentBalance,
        int cannotBuyCycles = 0,
        decimal reserved = 0m,
        Temperament temperament = Temperament.Balanced,
        RiskProfile riskProfile = RiskProfile.Medium,
        bool pendingFundSwitch = false)
    {
        var trader = new Participant
        {
            Name = "Trader",
            Type = ParticipantType.Individual,
            Temperament = temperament,
            RiskProfile = riskProfile,
            InitialBalance = currentBalance,
            CurrentBalance = currentBalance,
            SettledCashBalance = currentBalance,
            ReservedBalance = reserved,
            IsActive = true,
            CannotBuyCycles = cannotBuyCycles,
            PendingFundSwitch = pendingFundSwitch,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();
        return trader;
    }

    private async Task<(CollectiveFund Fund, Participant Participant)> AddFundAsync(
        CollectiveFundStatus status = CollectiveFundStatus.Active,
        decimal balance = 0m,
        int idleCycles = 0,
        decimal peakNetWorth = 0m,
        int createdInCycleId = 0)
    {
        var fundParticipant = new Participant
        {
            Name = "Collective Fund",
            Type = ParticipantType.CollectiveFund,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 0m,
            CurrentBalance = balance,
            SettledCashBalance = balance,
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
            CreatedInCycleId = createdInCycleId,
            CreatedAt = DateTime.UtcNow,
            IdleCycles = idleCycles,
            PeakNetWorth = peakNetWorth,
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

    private async Task AddDividendReceiptAsync(int participantId, int cycleId, decimal amount)
    {
        context.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = participantId,
            Type = MoneyTransactionType.Dividend,
            Amount = amount,
            CreatedInCycleId = cycleId,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task AddFeeReceiptAsync(int participantId, int cycleId, decimal amount)
    {
        context.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = participantId,
            Type = MoneyTransactionType.CollectiveFundDividendFeeReceived,
            Amount = amount,
            CreatedInCycleId = cycleId,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task AddWorthSnapshotAsync(int participantId, int cycleId, decimal netWorth)
    {
        context.ParticipantWorthSnapshots.Add(new ParticipantWorthSnapshot
        {
            ParticipantId = participantId,
            CreatedInCycleId = cycleId,
            Balance = netWorth,
            HoldingsValue = 0m,
            LoanLiability = 0m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    // Records a rising worth series so the fund reads as growing: six snapshots climbing from `from` to `to`.
    private async Task AddRisingWorthSeriesAsync(int participantId, decimal from, decimal to)
    {
        for (var index = 0; index <= 5; index++)
        {
            var worth = from + ((to - from) * index / 5m);
            await AddWorthSnapshotAsync(participantId, cycleId: index + 1, worth);
        }
    }

    private async Task AddSharesAsync(int ownerId, int companyId, int count, decimal price)
    {
        context.Holdings.Add(new Holding
        {
            ParticipantId = ownerId,
            CompanyId = companyId,
            Quantity = count,
            SettledQuantity = count,
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
