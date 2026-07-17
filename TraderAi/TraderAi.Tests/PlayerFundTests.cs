using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Covers the fund the human player opens and trades by hand: the open/deposit/withdraw cash flows on
// MarketService, the decision-pass exclusion, and the CollectiveFundService carve-outs (never auto-closes, still
// joinable). Uses the same in-memory SQLite fixture as the other service tests.
public sealed class PlayerFundTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public PlayerFundTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private MarketService Service(Random random, IDecisionEngine? decisionEngine = null) =>
        new(context, new MatchingEngine(context), decisionEngine ?? new NoOpDecisionEngine(), new MarketCycleLock(), random);

    private CollectiveFundService FundService(Random random)
    {
        var loanOptions = Options.Create(new LoanOptions { Enabled = true });
        return new CollectiveFundService(
            context,
            Options.Create(new CollectiveFundOptions { Enabled = true }),
            Options.Create(new RandomChanceRatesOptions()),
            loanOptions,
            new LoanService(context, loanOptions),
            random);
    }

    // Opening moves the seed from the player to a new player-managed fund participant, records both cash legs, and
    // does not enrol the player as a depositing member.
    [Fact]
    public async Task OpenFundDebitsPlayerCashAndCreatesManagedFund()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!; // FixedRoll pins the balance to 100,000.

        var result = await market.OpenPlayerFundAsync(4_000m, null);

        Assert.True(result.Success);
        await context.Entry(player).ReloadAsync();
        Assert.Equal(96_000m, player.CurrentBalance);

        var fund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.True(fund.IsPlayerManaged);
        Assert.Equal(player.Id, fund.FoundedByParticipantId);
        Assert.Equal(CollectiveFundStatus.Active, fund.Status);

        var fundParticipant = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fund.ParticipantId);
        Assert.Equal(ParticipantType.CollectiveFund, fundParticipant.Type);
        Assert.Equal("Ada's Fund", fundParticipant.Name);
        Assert.Equal(4_000m, fundParticipant.CurrentBalance);

        Assert.Equal(2, await context.MoneyTransactions.CountAsync(transaction => transaction.Type == MoneyTransactionType.CollectiveFund));
        // The player runs the fund but is not a depositing member.
        Assert.Equal(0, await context.CollectiveFundParticipants.CountAsync());
    }

    // Only one managed fund per player, mirroring the single-player invariant.
    [Fact]
    public async Task SecondOpenFundFails()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        await market.CreatePlayerAsync("Ada");
        Assert.True((await market.OpenPlayerFundAsync(1_000m, null)).Success);

        var second = await market.OpenPlayerFundAsync(1_000m, null);

        Assert.False(second.Success);
        Assert.Equal("The player already manages a fund.", second.Error);
        Assert.Equal(1, await context.CollectiveFunds.CountAsync());
    }

    // A seed larger than the player's available cash is refused and opens nothing.
    [Fact]
    public async Task OpenFundSeedAboveAvailableFails()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        await market.CreatePlayerAsync("Ada");

        var result = await market.OpenPlayerFundAsync(1_000_000m, null);

        Assert.False(result.Success);
        Assert.Equal("Seed amount exceeds the player's available balance.", result.Error);
        Assert.Equal(0, await context.CollectiveFunds.CountAsync());
    }

    // A non-positive seed is rejected.
    [Fact]
    public async Task OpenFundWithNonPositiveSeedFails()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        await market.CreatePlayerAsync("Ada");

        var result = await market.OpenPlayerFundAsync(0m, null);

        Assert.False(result.Success);
        Assert.Equal("Seed amount must be positive.", result.Error);
        Assert.Equal(0, await context.CollectiveFunds.CountAsync());
    }

    // Depositing moves more cash from the player into the fund.
    [Fact]
    public async Task DepositMovesCashFromPlayerToFund()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        await market.OpenPlayerFundAsync(4_000m, null);

        var result = await market.DepositToPlayerFundAsync(1_000m);

        Assert.True(result.Success);
        await context.Entry(player).ReloadAsync();
        Assert.Equal(95_000m, player.CurrentBalance);
        var fund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        var fundParticipant = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fund.ParticipantId);
        Assert.Equal(5_000m, fundParticipant.CurrentBalance);
    }

    [Fact]
    public async Task DepositCannotSpendUnsettledPlayerCash()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        await market.OpenPlayerFundAsync(4_000m, null);
        player.CurrentBalance += 2_000m;
        await context.SaveChangesAsync();

        var result = await market.DepositToPlayerFundAsync(97_000m);

        Assert.False(result.Success);
        await context.Entry(player).ReloadAsync();
        Assert.Equal(98_000m, player.CurrentBalance);
        Assert.Equal(96_000m, player.SettledCashBalance);
    }

    // Withdrawing moves cash back to the player.
    [Fact]
    public async Task WithdrawMovesCashFromFundToPlayer()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        await market.OpenPlayerFundAsync(4_000m, null);

        var result = await market.WithdrawFromPlayerFundAsync(1_500m);

        Assert.True(result.Success);
        await context.Entry(player).ReloadAsync();
        Assert.Equal(97_500m, player.CurrentBalance);
        var fund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        var fundParticipant = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fund.ParticipantId);
        Assert.Equal(2_500m, fundParticipant.CurrentBalance);
    }

    // The withdrawal records a directional pair so cash movements read correctly: the fund's row is an outflow
    // and the owner's row is income, instead of both showing as a deposit.
    [Fact]
    public async Task WithdrawRecordsFundOutflowAndOwnerIncome()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        await market.OpenPlayerFundAsync(4_000m, null);
        var fund = await context.CollectiveFunds.AsNoTracking().SingleAsync();

        var result = await market.WithdrawFromPlayerFundAsync(1_500m);

        Assert.True(result.Success);
        var fundRow = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.ParticipantId == fund.ParticipantId
                && transaction.Type == MoneyTransactionType.CollectiveFundWithdrawal);
        Assert.Equal(1_500m, fundRow.Amount);
        var ownerRow = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.ParticipantId == player.Id
                && transaction.Type == MoneyTransactionType.CollectiveFundWithdrawalReceived);
        Assert.Equal(1_500m, ownerRow.Amount);
    }

    [Fact]
    public async Task WithdrawCannotSpendUnsettledFundCash()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        await market.CreatePlayerAsync("Ada");
        await market.OpenPlayerFundAsync(4_000m, null);
        var fund = await context.CollectiveFunds.SingleAsync();
        var fundParticipant = await context.Participants.SingleAsync(participant => participant.Id == fund.ParticipantId);
        fundParticipant.CurrentBalance += 2_000m;
        await context.SaveChangesAsync();

        var result = await market.WithdrawFromPlayerFundAsync(5_000m);

        Assert.False(result.Success);
        await context.Entry(fundParticipant).ReloadAsync();
        Assert.Equal(6_000m, fundParticipant.CurrentBalance);
        Assert.Equal(4_000m, fundParticipant.SettledCashBalance);
    }

    // A withdrawal can never dip into the cash owed back to members as returnable deposits.
    [Fact]
    public async Task WithdrawCannotStrandMemberDeposits()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        await market.CreatePlayerAsync("Ada");
        await market.OpenPlayerFundAsync(4_000m, null);

        // An AI member has joined with a 3,000 deposit that the fund now also holds.
        var fund = await context.CollectiveFunds.SingleAsync();
        var fundParticipant = await context.Participants.FirstAsync(participant => participant.Id == fund.ParticipantId);
        var member = await AddTraderAsync(10_000m);
        context.CollectiveFundParticipants.Add(new CollectiveFundParticipant
        {
            CollectiveFundId = fund.Id,
            ParticipantId = member.Id,
            JoinedAt = DateTime.UtcNow,
            JoinedInCycleId = 0,
            DepositAmount = 3_000m,
        });
        fundParticipant.CurrentBalance += 3_000m; // fund now holds 7,000, but 3,000 is owed back to the member.
        fundParticipant.SettledCashBalance += 3_000m;
        await context.SaveChangesAsync();

        // Only 4,000 (7,000 − 3,000 owed) is withdrawable, so pulling the full balance is refused.
        var tooMuch = await market.WithdrawFromPlayerFundAsync(7_000m);
        Assert.False(tooMuch.Success);
        Assert.Equal("Withdrawal amount exceeds the fund's withdrawable cash.", tooMuch.Error);

        // Withdrawing exactly the free cash succeeds and leaves the member's deposit intact.
        var atCap = await market.WithdrawFromPlayerFundAsync(4_000m);
        Assert.True(atCap.Success);
        await context.Entry(fundParticipant).ReloadAsync();
        Assert.Equal(3_000m, fundParticipant.CurrentBalance);
    }

    // The automated decision pass trades every eligible fund except a player-managed one, which only moves when
    // the human places an order through it.
    [Fact]
    public async Task GenerateDecisionsSkipsPlayerManagedFund()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new Random(1), new DeterministicDecisionEngine());
        await market.CreatePlayerAsync("Ada");
        await market.OpenPlayerFundAsync(4_000m, null);
        var managedFund = await context.CollectiveFunds.AsNoTracking().SingleAsync(fund => fund.IsPlayerManaged);

        // A control fund with the same cash and no shares is left un-managed so it still trades.
        var controlFundParticipantId = await AddPlainFundAsync(balance: 4_000m);

        var result = await market.GenerateDecisionsAsync();

        Assert.True(result.Success);
        Assert.True(result.OrdersPlaced > 0);
        Assert.Equal(0, await context.Orders.CountAsync(order => order.ParticipantId == managedFund.ParticipantId));
        Assert.True(await context.Orders.AnyAsync(order => order.ParticipantId == controlFundParticipantId));
    }

    // A player-managed fund never auto-closes, even when its worth has collapsed far below its peak.
    [Fact]
    public async Task PlayerManagedFundIsNotFounderClosed()
    {
        var cycle = await SeedFundScenarioAsync();
        var (fund, _) = await AddManagedFundAsync(balance: 100_000m, peakNetWorth: 1_000_000m);
        var member = await AddTraderAsync(600_000m);
        await AddMembershipAsync(fund, member, deposit: 10_000m, cycle.Id);

        await FundService(new ScriptedRandom([], [])).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.Active, refreshed.Status);
    }

    // Any eligible trader may still join a player-managed fund and be enrolled as a member.
    [Fact]
    public async Task EligibleTraderCanJoinPlayerManagedFund()
    {
        var cycle = await SeedFundScenarioAsync();
        var (fund, _) = await AddManagedFundAsync(balance: 50_000m);
        var existing = await AddTraderAsync(10_000m);
        await AddMembershipAsync(fund, existing, deposit: 50_000m, cycle.Id);
        var joiner = await AddTraderAsync(100_000m);

        // The seated member is below the tenure gate; the joiner's 0.0 lands in the join band.
        await FundService(new ScriptedRandom([0.0d], [])).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(2, await context.CollectiveFundParticipants.CountAsync(member => member.CollectiveFundId == fund.Id));
        Assert.True(await context.CollectiveFundParticipants.AnyAsync(member => member.ParticipantId == joiner.Id));
    }

    // Closing an empty fund hands its cash and positions to the player and tombstones it, freeing the player to
    // open another.
    [Fact]
    public async Task ClosingFundHandsCashAndHoldingsToPlayer()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!; // FixedRoll pins the balance to 100,000.
        await market.OpenPlayerFundAsync(4_000m, null); // player 96,000, fund 4,000
        var fund = await context.CollectiveFunds.SingleAsync();
        var company = await context.Companies.FirstAsync();
        context.Holdings.Add(new Holding { ParticipantId = fund.ParticipantId, CompanyId = company.Id, Quantity = 5, AverageCost = 80m });
        await context.SaveChangesAsync();

        var result = await market.ClosePlayerFundAsync();

        Assert.True(result.Success);
        var refreshedFund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.Closed, refreshedFund.Status);
        var fundParticipant = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fund.ParticipantId);
        Assert.False(fundParticipant.IsActive);
        Assert.Equal(0m, fundParticipant.CurrentBalance);

        await context.Entry(player).ReloadAsync();
        Assert.Equal(100_000m, player.CurrentBalance); // seed cash returned in full

        var playerHolding = await context.Holdings.AsNoTracking()
            .FirstAsync(holding => holding.ParticipantId == player.Id && holding.CompanyId == company.Id);
        Assert.Equal(5, playerHolding.Quantity);
        Assert.Equal(80m, playerHolding.AverageCost);
        Assert.Equal(0, await context.Holdings.Where(holding => holding.ParticipantId == fund.ParticipantId).SumAsync(holding => holding.Quantity));

        // The player may open a fresh fund once the old one is closed.
        Assert.True((await market.OpenPlayerFundAsync(1_000m, null)).Success);
    }

    // Closing returns each member's deposit and hands the leftover cash to the player.
    [Fact]
    public async Task ClosingFundReturnsMemberDepositsThenResidualToPlayer()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        await market.OpenPlayerFundAsync(4_000m, null); // player 96,000, fund 4,000
        var fund = await context.CollectiveFunds.SingleAsync();
        var fundParticipant = await context.Participants.FirstAsync(participant => participant.Id == fund.ParticipantId);
        var member = await AddTraderAsync(500m);
        await AddMembershipAsync(fund, member, deposit: 3_000m, joinedCycleId: 0);
        fundParticipant.CurrentBalance += 3_000m; // the member's deposit is now pooled: fund holds 7,000
        fundParticipant.SettledCashBalance += 3_000m;
        await context.SaveChangesAsync();

        var result = await market.ClosePlayerFundAsync();

        Assert.True(result.Success);
        await context.Entry(member).ReloadAsync();
        Assert.Equal(3_500m, member.CurrentBalance); // 500 + 3,000 deposit back
        await context.Entry(player).ReloadAsync();
        Assert.Equal(100_000m, player.CurrentBalance); // 96,000 + (7,000 − 3,000) residual
        Assert.Equal(0, await context.CollectiveFundParticipants.CountAsync());
        Assert.Equal(CollectiveFundStatus.Closed, (await context.CollectiveFunds.AsNoTracking().SingleAsync()).Status);

        // The member's exit is logged with the deposit it was handed back.
        var leaveEvent = await context.CollectiveFundMembershipEvents.AsNoTracking()
            .SingleAsync(membershipEvent => membershipEvent.ParticipantId == member.Id);
        Assert.Equal(CollectiveFundMembershipEventType.Left, leaveEvent.Type);
        Assert.Equal(fundParticipant.Id, leaveEvent.FundParticipantId);
        Assert.Equal(3_000m, leaveEvent.Amount);
    }

    // A close cannot strand a member's principal: it is refused while the fund's cash cannot cover the deposits.
    [Fact]
    public async Task ClosingFundBlockedWhenCashCannotCoverMemberDeposits()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        await market.CreatePlayerAsync("Ada");
        await market.OpenPlayerFundAsync(4_000m, null); // fund holds only 4,000
        var fund = await context.CollectiveFunds.SingleAsync();
        var member = await AddTraderAsync(500m);
        await AddMembershipAsync(fund, member, deposit: 5_000m, joinedCycleId: 0); // owed more than the fund's cash

        var result = await market.ClosePlayerFundAsync();

        Assert.False(result.Success);
        Assert.Contains("cannot cover", result.Error);
        Assert.Equal(CollectiveFundStatus.Active, (await context.CollectiveFunds.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ClosingFundBlockedWhileTradeSettlementIsPending()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        await market.OpenPlayerFundAsync(4_000m, null);
        var fund = await context.CollectiveFunds.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync(cycle => cycle.Id == context.Markets.Single().CurrentCycleId);
        var company = await context.Companies.FirstAsync();
        var trade = new ShareTransaction
        {
            SellerId = fund.ParticipantId,
            BuyerId = player.Id,
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
            SellerId = fund.ParticipantId,
            BuyerId = player.Id,
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

        var result = await market.ClosePlayerFundAsync();

        Assert.False(result.Success);
        Assert.Contains("settlement", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CollectiveFundStatus.Active, (await context.CollectiveFunds.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ClosingFundBlockedWhileMarginLiabilityRemains()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        await market.CreatePlayerAsync("Ada");
        await market.OpenPlayerFundAsync(4_000m, null);
        var fund = await context.CollectiveFunds.SingleAsync();
        context.MarginAccounts.Add(new MarginAccount
        {
            ParticipantId = fund.ParticipantId,
            DebitBalance = 500m,
            AccruedInterest = 25m,
            InitialMarginRate = 0.50m,
            MaintenanceMarginRate = 0.25m,
            Status = MarginAccountStatus.Active,
        });
        await context.SaveChangesAsync();

        var result = await market.ClosePlayerFundAsync();

        Assert.False(result.Success);
        Assert.Contains("margin", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CollectiveFundStatus.Active, (await context.CollectiveFunds.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ClosingFundBlockedWhileTermLoanRemainsOpen()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        await market.CreatePlayerAsync("Ada");
        await market.OpenPlayerFundAsync(4_000m, null);
        var fund = await context.CollectiveFunds.SingleAsync();
        var cycleId = (await context.Markets.SingleAsync()).CurrentCycleId!.Value;
        var bank = new Bank { Name = "Test bank", InterestRatePerCycle = 0.001m };
        context.Banks.Add(bank);
        await context.SaveChangesAsync();
        context.Loans.Add(new Loan
        {
            BankId = bank.Id,
            ParticipantId = fund.ParticipantId,
            Principal = 500m,
            RemainingPrincipal = 500m,
            InterestRatePerCycle = 0.001m,
            TermCycles = 10,
            ScheduledInstallment = 50m,
            Status = LoanStatus.Open,
            OpenedInCycleId = cycleId,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var result = await market.ClosePlayerFundAsync();

        Assert.False(result.Success);
        Assert.Contains("loan", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CollectiveFundStatus.Active, (await context.CollectiveFunds.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(LoanStatus.Open, (await context.Loans.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ClosingFundBlockedUntilEconomicCashIsSettled()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        await market.CreatePlayerAsync("Ada");
        await market.OpenPlayerFundAsync(4_000m, null);
        var fund = await context.CollectiveFunds.SingleAsync();
        var fundParticipant = await context.Participants.SingleAsync(participant => participant.Id == fund.ParticipantId);
        fundParticipant.CurrentBalance += 100m;
        await context.SaveChangesAsync();

        var result = await market.ClosePlayerFundAsync();

        Assert.False(result.Success);
        Assert.Contains("settled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CollectiveFundStatus.Active, (await context.CollectiveFunds.AsNoTracking().SingleAsync()).Status);
    }

    // The player hand-trades its managed fund, so it can also cancel that fund's open orders; the reservation
    // is released back to the fund, not the player.
    [Fact]
    public async Task PlayerCanCancelManagedFundBuyOrder()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        await market.CreatePlayerAsync("Ada");
        await market.OpenPlayerFundAsync(4_000m, null);
        var fund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        var company = await context.Companies.FirstAsync();

        var placed = await market.PlaceOrderAsync(fund.ParticipantId, company.Id, OrderType.Buy, 5, 100m);
        Assert.True(placed.Success);

        var fundParticipant = await context.Participants.FirstAsync(participant => participant.Id == fund.ParticipantId);
        await context.Entry(fundParticipant).ReloadAsync();
        Assert.Equal(500m, fundParticipant.ReservedBalance);

        var cancelled = await market.CancelPlayerOrderAsync(placed.Order!.Id);

        Assert.True(cancelled.Success);
        var refreshed = await context.Orders.AsNoTracking().FirstAsync(order => order.Id == placed.Order!.Id);
        Assert.Equal(OrderStatus.Cancelled, refreshed.Status);
        await context.Entry(fundParticipant).ReloadAsync();
        Assert.Equal(0m, fundParticipant.ReservedBalance);
    }

    // The player-scoped cancel still rejects an order that is neither the player's nor its managed fund's.
    [Fact]
    public async Task CancelRejectsAnotherParticipantsOrder()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        await market.CreatePlayerAsync("Ada");
        var bob = await context.Participants.FirstAsync(participant => participant.Name == "Bob");
        var company = await context.Companies.FirstAsync();

        var placed = await market.PlaceOrderAsync(bob.Id, company.Id, OrderType.Buy, 5, 100m);
        Assert.True(placed.Success);

        var result = await market.CancelPlayerOrderAsync(placed.Order!.Id);

        Assert.False(result.Success);
        Assert.Equal("Order does not belong to the player.", result.Error);
        var refreshed = await context.Orders.AsNoTracking().FirstAsync(order => order.Id == placed.Order!.Id);
        Assert.Equal(OrderStatus.Open, refreshed.Status);
    }

    private async Task<MarketCycle> SeedFundScenarioAsync()
    {
        var now = DateTime.UtcNow;
        var cycle = new MarketCycle { CycleNumber = 600, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);
        var marketRow = new Market { Name = "Demo Market", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(marketRow);
        var industry = new Industry { Name = "Tech" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();

        var company = new Company { Name = "Acme", IndustryId = industry.Id, IssuedSharesCount = 1000, CreatedAt = now, UpdatedAt = now };
        context.Companies.Add(company);
        await context.SaveChangesAsync();

        context.PriceSnapshots.Add(new PriceSnapshot { CompanyId = company.Id, Price = 100m, CreatedInCycleId = cycle.Id, CreatedAt = now });
        marketRow.CurrentCycleId = cycle.Id;
        await context.SaveChangesAsync();
        return cycle;
    }

    private async Task<Participant> AddTraderAsync(decimal currentBalance)
    {
        var trader = new Participant
        {
            Name = "Trader",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = currentBalance,
            CurrentBalance = currentBalance,
            SettledCashBalance = currentBalance,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();
        return trader;
    }

    private async Task<int> AddPlainFundAsync(decimal balance)
    {
        var fundParticipant = new Participant
        {
            Name = "Control Fund",
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

        context.CollectiveFunds.Add(new CollectiveFund
        {
            ParticipantId = fundParticipant.Id,
            FoundedByParticipantId = fundParticipant.Id,
            IsPlayerManaged = false,
            Status = CollectiveFundStatus.Active,
            CreatedInCycleId = 0,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        return fundParticipant.Id;
    }

    private async Task<(CollectiveFund Fund, Participant Participant)> AddManagedFundAsync(decimal balance, decimal peakNetWorth = 0m)
    {
        var fundParticipant = new Participant
        {
            Name = "Player's Fund",
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
            IsPlayerManaged = true,
            Status = CollectiveFundStatus.Active,
            CreatedInCycleId = 0,
            CreatedAt = DateTime.UtcNow,
            PeakNetWorth = peakNetWorth,
        };
        context.CollectiveFunds.Add(fund);
        await context.SaveChangesAsync();
        return (fund, fundParticipant);
    }

    private async Task AddMembershipAsync(CollectiveFund fund, Participant member, decimal deposit, int joinedCycleId)
    {
        context.CollectiveFundParticipants.Add(new CollectiveFundParticipant
        {
            CollectiveFundId = fund.Id,
            ParticipantId = member.Id,
            JoinedAt = DateTime.UtcNow,
            JoinedInCycleId = joinedCycleId,
            DepositAmount = deposit,
        });
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    // Pins every ranged draw to its low end so the player's starting balance is deterministic.
    private sealed class FixedRoll(double value) : Random
    {
        public override double NextDouble() => value;

        public override int Next(int minValue, int maxValue) => minValue;
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
