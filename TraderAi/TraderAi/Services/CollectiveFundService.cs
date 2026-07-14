using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Drives the collective-fund lifecycle once per cycle, before matching runs. Eligible cash-strapped traders
// roll to pool into a fund (handing over 90% of their cash and going quiet); a fund trades the pool, returns a
// member's deposit when they leave, and when only a pair is left and one leaves it sells everything and splits
// the cash among the survivors. Called from inside order maintenance, which holds the lock and owns the final
// save, so this stages changes on the shared context (with a couple of saves where it needs generated ids).
public sealed class CollectiveFundService(
    AppDbContext dbContext,
    IOptions<CollectiveFundOptions> options,
    IOptions<RandomChanceRatesOptions> chanceRates,
    IOptions<LoanOptions> loanOptions,
    LoanService loanService,
    Random random)
{
    // Candidates pool into a fund only while their cash sits below CollectiveFundOptions.JoinBalanceCeiling;
    // members must leave once their own cash climbs back above this upper line.
    private const decimal LeaveBalanceThreshold = 100_000_000m;

    // No fund may be created or joined during the market's opening stretch.
    private const int QuietCycles = 50;

    // Once a member sits at or above the leave line its exit chance starts at the configured base and ramps each
    // cycle to the configured cap.
    private const double LeaveStepPerCycle = 0.02;

    private const decimal ContributionFraction = 0.90m;

    // Fund forced sales (raising a leaver's deposit, or dumping everything while closing) undercut the market so
    // the order actually crosses; order ageing pushes any unsold remainder lower over the following cycles.
    private const decimal SaleDiscount = 0.10m;

    // Share of a fund's own dividend receipt that is passed straight through to its members, split by deposit.
    private const decimal DividendPassThroughFraction = 0.50m;

    // The fund withholds this cut from each member's pass-through dividend as a management fee; the withheld
    // amount stays in the fund's cash rather than reaching the member.
    private const decimal DividendFeeFraction = 0.20m;

    // A fund that owns nothing and cannot afford even the cheapest share for this many consecutive cycles unwinds.
    private const int MaxIdleCycles = 20;

    // A closing fund that hands a member a payout at or below this fraction of its deposit inflicted a
    // devastating loss; the member is flagged so the market-exit service can offer it a one-shot chance to quit.
    private const decimal FundLossFlagFraction = 0.20m;

    private const double SwitchTemperamentDelta = 0.05;

    // The founder closes the fund once its net worth collapses to this fraction of its all-time peak, or once it
    // has drawn no dividend income across its last FounderDividendLookbackPayouts post-founding payout cycles.
    private const decimal FounderLossFraction = 0.15m;
    private const int FounderDividendLookbackPayouts = 2;

    // A joiner scores each candidate fund on net worth, dividends over its last this-many payout events, recent
    // net-worth growth, and advertised popularity, each min-max normalised across the candidates and summed with
    // the weights below. The summed score is then damped by the fund's free member capacity and the joiner is
    // placed by a score-proportional wheel, so joiners no longer all pile into the single largest fund.
    private const int JoinDividendLookbackPayouts = 3;
    private const double ScoreWeightWorth = 1.0;
    private const double ScoreWeightDividends = 1.0;
    private const double ScoreWeightGrowth = 1.0;
    private const double ScoreWeightPopularity = 1.0;

    // A fund's popularity decays by one only once its last advertisement is more than this many cycles behind the
    // current cycle (or it has never advertised); advertising within the window holds popularity steady.
    private const int AdvertisementDecayIdleCycles = 20;

    // A fund whose net worth CollectiveFundOptions.FundGrowthWindowCycles snapshots ago is at least
    // FundGrowthThreshold below its latest recorded worth is "growing": it earns a willingness-to-join boost and
    // a celebratory newswire. A fund without that much snapshot history has no signal yet.
    private const decimal FundGrowthThreshold = 0.02m;

    // A growing fund posts a fresh "on a hot streak" headline at most once per this many cycles, so a long
    // winning run does not spam the newswire every cycle.
    private const int GrowthNewsCooldownCycles = 25;

    private Dictionary<int, Participant> participantsById = null!;
    private List<CollectiveFund> funds = null!;
    private Dictionary<int, List<CollectiveFundParticipant>> membershipsByFundId = null!;
    private Dictionary<int, CollectiveFundParticipant> membershipByParticipantId = null!;
    private Dictionary<int, List<OwnedHolding>> ownedByParticipant = null!;
    private Dictionary<int, List<Order>> openOrdersByParticipant = null!;
    private Dictionary<int, decimal> latestPriceByCompany = null!;
    private Dictionary<int, PriceBandState> bandByCompany = null!;
    private Dictionary<(int ParticipantId, int CompanyId), int> available = null!;
    private Dictionary<int, int> tradingDayNumberByCycleId = null!;
    private int currentTradingDayNumber;

    // Distinct dividend-payout cycle ids, most recent first (ids rise monotonically with cycles), and each fund
    // participant's dividend receipts keyed by payout cycle id; both feed fund scoring and the founder close.
    private List<int> payoutCycleIdsDesc = null!;
    private Dictionary<int, Dictionary<int, decimal>> fundDividendByCycleId = null!;

    // Open loans a fund participant carries, so a wind-down can discharge them.
    private Dictionary<int, List<Loan>> openLoansByFundParticipant = null!;
    private Dictionary<int, decimal> marginLiabilityByFundParticipant = null!;
    private HashSet<int> pendingSettlementFundParticipantIds = null!;

    // Recent net-worth growth per fund id (fraction over the growth window; 0 when the fund lacks the history),
    // and the subset whose growth cleared the threshold. The first feeds fund scoring, the second the join
    // boost and the growth newswire.
    private Dictionary<int, decimal> growthPercentByFundId = null!;
    private HashSet<int> growingFundIds = null!;

    // The crisis active this cycle (if any) and its cycle number, stashed per-cycle so a fund that closes deep in
    // the call chain can record itself on the crisis timeline without threading them through every method.
    private Crisis? activeCrisis;
    private int crisisCycleNumber;

    // Draw discipline for a scripted Random in tests: no draws while closing funds, returning deposits, ratcheting
    // peak worth, decaying advertised popularity, running the founder close, enforcing member capacity, detecting
    // fund growth, posting the growth newswire, or dropping a stale membership. In the member pass (fund id, then participant id order) each
    // member draws at most once — the forced-leave roll if it sits at or above the leave line, otherwise the
    // switch roll if it is a non-founder past the minimum trading-day tenure, otherwise nothing. In the join pass (independent
    // traders, id order) a switch-flagged member draws nothing, and every other eligible trader draws once; that
    // single draw both decides join-versus-open and, when it joins, positions the score-proportional fund pick.
    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now, Crisis? activeCrisis = null)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        this.activeCrisis = activeCrisis;
        crisisCycleNumber = currentCycleNumber;

        await LoadStateAsync(currentCycleId);

        foreach (var fund in funds.Where(fund => fund.Status == CollectiveFundStatus.GoingToBeClosed).OrderBy(fund => fund.Id).ToList())
        {
            ProcessClosing(fund, currentCycleId, now);
        }

        foreach (var fund in funds.Where(fund => fund.Status == CollectiveFundStatus.Active).OrderBy(fund => fund.Id).ToList())
        {
            RatchetPeakNetWorth(fund);
            DecayPopularity(fund, currentCycleNumber);

            // A player-managed fund never auto-closes or idle-unwinds; the human owns its fate. The member pass
            // below still runs so anyone who joined can leave and be repaid.
            if (!fund.IsPlayerManaged)
            {
                if (MaybeFounderClose(fund, currentCycleId, now))
                {
                    continue;
                }

                TrackIdleAndMaybeClose(fund, currentCycleId, now);
                if (fund.Status != CollectiveFundStatus.Active)
                {
                    continue;
                }
            }

            // Capacity is enforced on every fund, the player-managed one included: a fund above the cap sheds its
            // newest member before the member pass. The eviction can wind an AI fund down to its last pair.
            await EnforceMemberCapacityAsync(fund, currentCycleId, now);
            if (fund.Status != CollectiveFundStatus.Active)
            {
                continue;
            }

            PrepareForNextTradingDayLeave(fund, currentCycleId, now);

            foreach (var membership in membershipsByFundId[fund.Id].OrderBy(member => member.ParticipantId).ToList())
            {
                if (fund.Status != CollectiveFundStatus.Active)
                {
                    break;
                }

                // A member whose participant row left the market leaves a stale membership behind; drop it
                // rather than dereferencing a key that no longer exists. It got no payout, so the event records zero.
                if (!participantsById.TryGetValue(membership.ParticipantId, out var member))
                {
                    AddMembershipEvent(fund, membership.ParticipantId, CollectiveFundMembershipEventType.Left, 0m, currentCycleId, now);
                    RemoveMembership(fund, membership);
                    continue;
                }

                // A fund releases at most one voluntary leaver per trading day; once the day's leaver has been
                // repaid, everyone else waits for a later day. Administrative removals above already bypass this.
                if (VoluntaryLeaveUsedThisTradingDay(fund))
                {
                    continue;
                }

                if (membership.IsLeaving)
                {
                    await AdvanceLeave(fund, membership, member, currentCycleId, now);
                }
                else
                {
                    await MaybeDecideLeave(fund, membership, member, currentCycleId, now);
                }

                // A departure that actually completed drops the membership row; that spends the fund's single
                // voluntary-leave slot for this trading day. A member merely flagged and still waiting does not.
                if (currentTradingDayNumber > 0 && !membershipsByFundId[fund.Id].Contains(membership))
                {
                    fund.LastVoluntaryLeaveTradingDayNumber = currentTradingDayNumber;
                }
            }
        }

        PostGrowthNewsForFunds(currentCycleId, currentCycleNumber, now);

        // Opening protection: traders only start pooling once the market clears its quiet window.
        if (currentCycleNumber <= QuietCycles)
        {
            return;
        }

        var joinOrOpenCandidates = participantsById.Values
            .Where(participant => participant.Type is ParticipantType.Individual or ParticipantType.AIAgent)
            .OrderBy(participant => participant.Id)
            .ToList();

        foreach (var participant in joinOrOpenCandidates)
        {
            await MaybeJoinOrOpenAsync(participant, currentCycleId, currentCycleNumber, now);
        }
    }

    // Hands a fund's dividend receipt partly through to its members, split by deposit and recorded under its own
    // transaction type so it is not double-counted with market dividends. Called by the dividend pass once the
    // fund has been credited as a share owner.
    public async Task DistributeDividendToMembersAsync(Participant fundOwner, decimal payout, int currentCycleId, DateTime now)
    {
        if (!options.Value.Enabled || payout <= 0m)
        {
            return;
        }

        var fund = await dbContext.CollectiveFunds.FirstOrDefaultAsync(candidate => candidate.ParticipantId == fundOwner.Id);
        if (fund is null)
        {
            return;
        }

        var members = await dbContext.CollectiveFundParticipants
            .Where(member => member.CollectiveFundId == fund.Id)
            .ToListAsync();
        var totalDeposit = members.Sum(member => member.DepositAmount);
        if (members.Count == 0 || totalDeposit <= 0m)
        {
            return;
        }

        var pool = Round(payout * DividendPassThroughFraction);
        if (pool <= 0m)
        {
            return;
        }

        var memberIds = members.Select(member => member.ParticipantId).ToList();
        var memberParticipants = await dbContext.Participants
            .Where(participant => memberIds.Contains(participant.Id))
            .ToDictionaryAsync(participant => participant.Id);

        var distributed = 0m;
        foreach (var member in members)
        {
            if (!memberParticipants.TryGetValue(member.ParticipantId, out var memberParticipant))
            {
                continue;
            }

            var gross = Round(pool * member.DepositAmount / totalDeposit);

            // The fund keeps a fee cut of each member's pass-through dividend; only the net leaves the fund's cash,
            // so the withheld fee stays behind in the fund's balance without a separate transfer.
            var cut = Round(gross * (1m - DividendFeeFraction));
            if (cut <= 0m)
            {
                continue;
            }

            memberParticipant.CurrentBalance += cut;
            memberParticipant.SettledCashBalance += cut;
            distributed += cut;
            dbContext.MoneyTransactions.Add(new MoneyTransaction
            {
                ParticipantId = memberParticipant.Id,
                Type = MoneyTransactionType.CollectiveFundDividend,
                Amount = cut,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
            });
        }

        fundOwner.CurrentBalance -= distributed;
        fundOwner.SettledCashBalance -= distributed;
    }

    private async Task LoadStateAsync(int currentCycleId)
    {
        latestPriceByCompany = await LatestPriceByCompanyAsync();
        bandByCompany = await dbContext.PriceBandStates.ToDictionaryAsync(state => state.CompanyId);

        ownedByParticipant = (await dbContext.Holdings
                .Where(holding => holding.Quantity > 0)
                .Select(holding => new OwnedHolding(holding.ParticipantId, holding.CompanyId, holding.Quantity))
                .ToListAsync())
            .GroupBy(holding => holding.OwnerId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var openOrders = await dbContext.Orders
            .Where(order => order.ParticipantId != null
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .ToListAsync();
        openOrdersByParticipant = openOrders
            .GroupBy(order => order.ParticipantId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        // Uncommitted quantity per (participant, company): owned shares minus what is already listed for sale.
        // Listing a fund sell draws it down, so a share is never offered by two orders at once.
        available = new Dictionary<(int ParticipantId, int CompanyId), int>();
        foreach (var (participantId, holdings) in ownedByParticipant)
        {
            foreach (var holding in holdings)
            {
                available[(participantId, holding.CompanyId)] = holding.Quantity;
            }
        }

        foreach (var order in openOrders.Where(order => order.Type == OrderType.Sell))
        {
            var key = (order.ParticipantId!.Value, order.CompanyId);
            if (available.TryGetValue(key, out var remaining))
            {
                available[key] = remaining - order.RemainingQuantity;
            }
        }

        participantsById = await dbContext.Participants.ToDictionaryAsync(participant => participant.Id);

        var dividendReceipts = await dbContext.MoneyTransactions
            .Where(transaction => transaction.Type == MoneyTransactionType.Dividend)
            .Select(transaction => new { transaction.ParticipantId, transaction.CreatedInCycleId, transaction.Amount })
            .ToListAsync();
        payoutCycleIdsDesc = dividendReceipts
            .Select(receipt => receipt.CreatedInCycleId)
            .Distinct()
            .OrderByDescending(cycleId => cycleId)
            .ToList();
        fundDividendByCycleId = dividendReceipts
            .GroupBy(receipt => receipt.ParticipantId)
            .ToDictionary(
                byParticipant => byParticipant.Key,
                byParticipant => byParticipant
                    .GroupBy(receipt => receipt.CreatedInCycleId)
                    .ToDictionary(byCycle => byCycle.Key, byCycle => byCycle.Sum(receipt => receipt.Amount)));

        funds = await dbContext.CollectiveFunds.ToListAsync();
        var memberships = await dbContext.CollectiveFundParticipants.ToListAsync();
        var membershipCycleIds = memberships
            .Select(member => member.JoinedInCycleId)
            .Append(currentCycleId)
            .Distinct()
            .ToList();
        tradingDayNumberByCycleId = await (
                from cycle in dbContext.MarketCycles
                join day in dbContext.TradingDays on cycle.TradingDayId equals day.Id
                where membershipCycleIds.Contains(cycle.Id)
                select new { cycle.Id, day.DayNumber })
            .ToDictionaryAsync(entry => entry.Id, entry => entry.DayNumber);
        currentTradingDayNumber = tradingDayNumberByCycleId.GetValueOrDefault(currentCycleId);
        membershipByParticipantId = memberships.ToDictionary(member => member.ParticipantId);
        membershipsByFundId = funds.ToDictionary(
            fund => fund.Id,
            fund => memberships.Where(member => member.CollectiveFundId == fund.Id).ToList());

        var fundParticipantIds = funds.Select(fund => fund.ParticipantId).ToList();
        openLoansByFundParticipant = (await dbContext.Loans
                .Where(loan => loan.Status == LoanStatus.Open && fundParticipantIds.Contains(loan.ParticipantId))
                .ToListAsync())
            .GroupBy(loan => loan.ParticipantId)
            .ToDictionary(group => group.Key, group => group.ToList());
        marginLiabilityByFundParticipant = await dbContext.MarginAccounts
            .Where(account => fundParticipantIds.Contains(account.ParticipantId))
            .ToDictionaryAsync(account => account.ParticipantId, account => account.DebitBalance + account.AccruedInterest);
        var pendingSettlements = await dbContext.SettlementInstructions
            .Where(instruction => instruction.Status == SettlementStatus.Pending
                && (fundParticipantIds.Contains(instruction.BuyerId)
                    || (instruction.SellerId.HasValue && fundParticipantIds.Contains(instruction.SellerId.Value))))
            .Select(instruction => new { instruction.BuyerId, instruction.SellerId })
            .ToListAsync();
        pendingSettlementFundParticipantIds = pendingSettlements
            .SelectMany(instruction => new[] { instruction.BuyerId, instruction.SellerId ?? 0 })
            .Where(fundParticipantIds.Contains)
            .ToHashSet();

        await LoadFundGrowthAsync(currentCycleId, fundParticipantIds);
    }

    // Measures each fund's recent net-worth trend from its worth snapshots: the latest recorded worth against
    // the worth FundGrowthWindowCycles snapshots earlier. Only the recent slice is queried (cycle ids are
    // effectively contiguous), so the scan stays small regardless of retention. Draws no randomness.
    private async Task LoadFundGrowthAsync(int currentCycleId, List<int> fundParticipantIds)
    {
        growthPercentByFundId = new Dictionary<int, decimal>();
        growingFundIds = new HashSet<int>();
        if (fundParticipantIds.Count == 0)
        {
            return;
        }

        var windowCycles = options.Value.FundGrowthWindowCycles;

        // A little slack beyond the window guards against any gap in recorded cycle ids.
        var earliestCycleId = currentCycleId - (windowCycles + 5);
        var snapshotsByFund = (await dbContext.ParticipantWorthSnapshots
                .Where(snapshot => fundParticipantIds.Contains(snapshot.ParticipantId)
                    && snapshot.CreatedInCycleId > earliestCycleId)
                .Select(snapshot => new
                {
                    snapshot.ParticipantId,
                    snapshot.CreatedInCycleId,
                    NetWorth = snapshot.Balance + snapshot.HoldingsValue - snapshot.LoanLiability - snapshot.MarginLiability,
                })
                .ToListAsync())
            .GroupBy(snapshot => snapshot.ParticipantId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(snapshot => snapshot.CreatedInCycleId).ToList());

        foreach (var fund in funds)
        {
            // Need one snapshot beyond the window so index [windowCycles] is the worth that many cycles back;
            // a younger fund has no trend to read yet.
            if (!snapshotsByFund.TryGetValue(fund.ParticipantId, out var snapshots)
                || snapshots.Count <= windowCycles)
            {
                continue;
            }

            var recent = snapshots[0].NetWorth;
            var past = snapshots[windowCycles].NetWorth;
            if (past <= 0m)
            {
                continue;
            }

            var growth = (recent - past) / past;
            growthPercentByFundId[fund.Id] = growth;
            if (growth >= FundGrowthThreshold)
            {
                growingFundIds.Add(fund.Id);
            }
        }
    }

    // Posts an impact-free (Scope = None) newswire for each active, growing fund past its headline cooldown, so
    // a fund on a hot streak advertises itself to would-be joiners. Draws no randomness.
    private void PostGrowthNewsForFunds(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        var windowCycles = options.Value.FundGrowthWindowCycles;
        foreach (var fund in funds.Where(fund => fund.Status == CollectiveFundStatus.Active).OrderBy(fund => fund.Id))
        {
            if (!growingFundIds.Contains(fund.Id)
                || (fund.LastGrowthNewsInCycleNumber is int last && currentCycleNumber - last < GrowthNewsCooldownCycles)
                || !participantsById.TryGetValue(fund.ParticipantId, out var fundParticipant))
            {
                continue;
            }

            var gainPercent = Math.Round(growthPercentByFundId.GetValueOrDefault(fund.Id) * 100m, 1, MidpointRounding.AwayFromZero);
            dbContext.NewsPosts.Add(new NewsPost
            {
                Title = $"{fundParticipant.Name} is up {gainPercent}% over the last {windowCycles} cycles",
                Content = $"{fundParticipant.Name} has grown its net worth {gainPercent}% across the last {windowCycles} cycles, drawing fresh interest from traders looking for a fund to join.",
                PublishedInCycleId = currentCycleId,
                PublishedAt = now,
                Scope = NewsImpactScope.None,
                Category = NewsCategory.FundPerformance,
            });
            fund.LastGrowthNewsInCycleNumber = currentCycleNumber;
        }
    }

    private async Task MaybeDecideLeave(CollectiveFund fund, CollectiveFundParticipant membership, Participant member, int currentCycleId, DateTime now)
    {
        if (MembershipTradingDays(membership) < Math.Max(0, options.Value.MinimumMembershipTradingDays))
        {
            membership.LeaveRampCycles = 0;
            return;
        }

        // A member that has grown rich graduates out of the fund on the ramping leave roll and does not come back.
        if (member.CurrentBalance >= LeaveBalanceThreshold)
        {
            membership.LeaveRampCycles++;
            var triggers = chanceRates.Value.EventTriggerChances;
            var rampChance = Math.Min(triggers.FundLeaveBase + (LeaveStepPerCycle * (membership.LeaveRampCycles - 1)), triggers.FundLeaveMax);
            if (random.NextDouble() >= rampChance)
            {
                return;
            }

            membership.IsLeaving = true;
            await AdvanceLeave(fund, membership, member, currentCycleId, now);
            return;
        }

        membership.LeaveRampCycles = 0;

        // Founders stay put; everyone else past the minimum trading-day tenure may roll to chase a better fund.
        if (member.Id == fund.FoundedByParticipantId)
        {
            return;
        }

        if (random.NextDouble() >= SwitchChance(member.Temperament))
        {
            return;
        }

        member.PendingFundSwitch = true;
        membership.IsLeaving = true;
        await AdvanceLeave(fund, membership, member, currentCycleId, now);
    }

    private double SwitchChance(Temperament temperament) =>
        chanceRates.Value.EventTriggerChances.FundSwitchBase + temperament switch
        {
            Temperament.Aggressive => SwitchTemperamentDelta,
            Temperament.Conservative => -SwitchTemperamentDelta,
            _ => 0.0,
        };

    private int MembershipTradingDays(CollectiveFundParticipant membership)
    {
        var joinedTradingDayNumber = tradingDayNumberByCycleId.GetValueOrDefault(membership.JoinedInCycleId);
        if (currentTradingDayNumber <= 0 || joinedTradingDayNumber <= 0)
        {
            return 0;
        }

        return Math.Max(0, currentTradingDayNumber - joinedTradingDayNumber);
    }

    // True once this fund has already repaid a voluntary leaver on the current trading day. Inert when trading
    // days are not tracked (day number zero), so the throttle never blocks in that setup.
    private bool VoluntaryLeaveUsedThisTradingDay(CollectiveFund fund) =>
        currentTradingDayNumber > 0 && fund.LastVoluntaryLeaveTradingDayNumber == currentTradingDayNumber;

    private void PrepareForNextTradingDayLeave(CollectiveFund fund, int currentCycleId, DateTime now)
    {
        if (fund.IsPlayerManaged)
        {
            return;
        }

        var minimumTradingDays = Math.Max(0, options.Value.MinimumMembershipTradingDays);
        var becomesEligibleNextDay = membershipsByFundId[fund.Id]
            .Any(membership => !membership.IsLeaving
                && MembershipTradingDays(membership) + 1 == minimumTradingDays);
        if (!becomesEligibleNextDay)
        {
            return;
        }

        var fundParticipant = participantsById[fund.ParticipantId];
        foreach (var order in OpenOrders(fundParticipant.Id).Where(order => order.Type == OrderType.Buy).ToList())
        {
            CancelBuy(order, fundParticipant, currentCycleId, now);
        }

        var targetCash = Math.Max(0m, FundNetWorth(fundParticipant)) * PreLeaveCashBufferFraction;
        var shortfall = targetCash - TransferableCash(fundParticipant) - PendingSaleValue(fundParticipant.Id);
        if (shortfall > 0m)
        {
            ListFundSellsForCash(fundParticipant, shortfall, currentCycleId, now);
        }
    }

    private decimal PreLeaveCashBufferFraction =>
        Math.Clamp(options.Value.PreLeaveCashBufferFraction, 0m, 1m);

    // Returns a leaving member's full deposit in the same cycle. When the fund is short on free cash it borrows
    // the shortfall (inflated by the payout buffer) and pays from that, so no member is ever left waiting on
    // forced sales; the fund carries the loan, which the loan service repays. If the exit would shrink an AI
    // fund below a pair, the whole fund unwinds instead. Only when loans are disabled does it fall back to the
    // old behavior of raising the shortfall by selling shares and waiting.
    private async Task AdvanceLeave(CollectiveFund fund, CollectiveFundParticipant membership, Participant member, int currentCycleId, DateTime now)
    {
        if (fund.Status != CollectiveFundStatus.Active)
        {
            return;
        }

        var fundParticipant = participantsById[fund.ParticipantId];

        // A shrinking AI fund collapses into a wind-down once only a pair is left; a player-managed fund never
        // does — the player keeps running it even after every AI member has gone.
        if (!fund.IsPlayerManaged && membershipsByFundId[fund.Id].Count <= 2)
        {
            BeginClosing(fund, fundParticipant, currentCycleId, now);
            return;
        }

        var deposit = membership.DepositAmount;

        // Short on free cash: borrow the shortfall plus the buffer so the deposit clears now instead of the
        // member waiting cycles for forced sales to fund it. The debt then rides on the normal loan machinery.
        if (TransferableCash(fundParticipant) < deposit && loanOptions.Value.Enabled)
        {
            var borrowShortfall = deposit - TransferableCash(fundParticipant);
            var principal = borrowShortfall * (1m + loanOptions.Value.LeavePayoutLoanBufferRate);
            await loanService.OriginateLoanAsync(fundParticipant, principal, FundNetWorth(fundParticipant), currentCycleId, now);
        }

        if (TransferableCash(fundParticipant) >= deposit)
        {
            fundParticipant.CurrentBalance -= deposit;
            fundParticipant.SettledCashBalance -= deposit;
            member.CurrentBalance += deposit;
            member.SettledCashBalance += deposit;
            AddFundTransaction(fundParticipant.Id, deposit, currentCycleId, now);
            AddFundTransaction(member.Id, deposit, currentCycleId, now);
            AddMembershipEvent(fund, member.Id, CollectiveFundMembershipEventType.Left, deposit, currentCycleId, now);
            RemoveMembership(fund, membership);
            return;
        }

        // Loans disabled: fall back to raising the shortfall by selling shares and waiting. Existing open sells
        // already bring in cash, so only list the part of the shortfall they do not cover.
        var shortfall = deposit - TransferableCash(fundParticipant);
        var alreadyListed = PendingSaleValue(fundParticipant.Id);
        var stillToRaise = shortfall - alreadyListed;
        if (stillToRaise > 0m)
        {
            ListFundSellsForCash(fundParticipant, stillToRaise, currentCycleId, now);
        }
    }

    private void RatchetPeakNetWorth(CollectiveFund fund)
    {
        var worth = FundNetWorth(participantsById[fund.ParticipantId]);
        if (worth > fund.PeakNetWorth)
        {
            fund.PeakNetWorth = worth;
        }
    }

    // An advertised fund holds its popularity while its last ad is within the idle window; past that (or if it
    // never advertised) popularity ebbs one point per cycle, floored at zero. Deterministic, so scripted tests
    // are unaffected.
    private static void DecayPopularity(CollectiveFund fund, int currentCycleNumber)
    {
        if (fund.PopularityIndex <= 0)
        {
            return;
        }

        var advertisedRecently = fund.LastAdvertisedInCycleNumber is int lastAd
            && currentCycleNumber - lastAd <= AdvertisementDecayIdleCycles;
        if (!advertisedRecently)
        {
            fund.PopularityIndex = Math.Max(0, fund.PopularityIndex - 1);
        }
    }

    // The founder unwinds the fund the moment it has clearly failed: its worth has collapsed to a small fraction
    // of its peak, or it has drawn no dividend income across its last couple of post-founding payout cycles. No
    // random draws, so scripted tests are unaffected.
    private bool MaybeFounderClose(CollectiveFund fund, int currentCycleId, DateTime now)
    {
        var fundParticipant = participantsById[fund.ParticipantId];
        var lostMoney = fund.PeakNetWorth > 0m && FundNetWorth(fundParticipant) <= fund.PeakNetWorth * FounderLossFraction;

        if (lostMoney || DividendStarved(fund))
        {
            BeginClosing(fund, fundParticipant, currentCycleId, now);
            return true;
        }

        return false;
    }

    // A fund that received nothing across each of its last few post-founding payout cycles is dividend-starved.
    // Payout cycles before the fund existed are ignored, and a fund without that much history is given the benefit
    // of the doubt.
    private bool DividendStarved(CollectiveFund fund)
    {
        var relevantPayouts = payoutCycleIdsDesc
            .Where(cycleId => cycleId > fund.CreatedInCycleId)
            .Take(FounderDividendLookbackPayouts)
            .ToList();
        if (relevantPayouts.Count < FounderDividendLookbackPayouts)
        {
            return false;
        }

        var byCycle = fundDividendByCycleId.GetValueOrDefault(fund.ParticipantId);
        return relevantPayouts.All(cycleId => (byCycle?.GetValueOrDefault(cycleId) ?? 0m) <= 0m);
    }

    // The operative member cap: a fund closes to new joiners once it reaches this and sheds its newest member
    // while above it. Never exceeds the absolute MaxMembers ceiling.
    private int MemberCapacity => Math.Min(options.Value.SoftCloseMembers, options.Value.MaxMembers);

    private List<CollectiveFund> JoinableFunds() =>
        funds
            .Where(fund => fund.Status == CollectiveFundStatus.Active && membershipsByFundId[fund.Id].Count < MemberCapacity)
            .ToList();

    // Brings a fund at or above capacity back into line by returning the most recently joined member's deposit
    // through the standard leave path (borrowing the shortfall, or listing shares when loans are off, and
    // pair-closing an AI fund if it drops to its last pair). One member leaves per cycle. Draws no randomness.
    private async Task EnforceMemberCapacityAsync(CollectiveFund fund, int currentCycleId, DateTime now)
    {
        var members = membershipsByFundId[fund.Id];
        if (members.Count <= MemberCapacity)
        {
            return;
        }

        var newest = members
            .OrderByDescending(membership => membership.JoinedInCycleId)
            .ThenByDescending(membership => membership.Id)
            .First();

        // A member whose participant row already left the market leaves a stale membership behind; drop it with a
        // zero-payout leave rather than dereferencing a key that no longer exists.
        if (!participantsById.TryGetValue(newest.ParticipantId, out var member))
        {
            AddMembershipEvent(fund, newest.ParticipantId, CollectiveFundMembershipEventType.Left, 0m, currentCycleId, now);
            RemoveMembership(fund, newest);
            return;
        }

        newest.IsLeaving = true;
        await AdvanceLeave(fund, newest, member, currentCycleId, now);
    }

    // Places a joiner on a candidate fund. Each quality metric — net worth, recent dividends, recent growth, and
    // advertised popularity — is min-max normalised across the candidates and summed, then exponentiated and
    // scaled by the fund's free member capacity so a fund near its cap softly closes to newcomers. The joiner
    // rides a roulette wheel over those weights, so the strongest fund is likeliest yet never captures every
    // joiner; selectionPoint in [0,1) walks the wheel and 0 lands on the top-weighted fund, ties to the lowest id.
    private CollectiveFund? SelectBestFund(List<CollectiveFund> candidates, double selectionPoint)
    {
        if (candidates.Count <= 1)
        {
            return candidates.Count == 1 ? candidates[0] : null;
        }

        var scored = candidates
            .Select(fund => new
            {
                Fund = fund,
                Worth = (double)FundNetWorth(participantsById[fund.ParticipantId]),
                Dividends = (double)RecentDividends(fund.ParticipantId),
                Growth = (double)growthPercentByFundId.GetValueOrDefault(fund.Id),
                Popularity = (double)fund.PopularityIndex,
                RoomFraction = RoomFraction(fund),
            })
            .ToList();

        var worthMin = scored.Min(entry => entry.Worth);
        var worthMax = scored.Max(entry => entry.Worth);
        var dividendMin = scored.Min(entry => entry.Dividends);
        var dividendMax = scored.Max(entry => entry.Dividends);
        var growthMin = scored.Min(entry => entry.Growth);
        var growthMax = scored.Max(entry => entry.Growth);
        var popularityMin = scored.Min(entry => entry.Popularity);
        var popularityMax = scored.Max(entry => entry.Popularity);

        static double Normalise(double value, double min, double max) => max > min ? (value - min) / (max - min) : 0.0;

        var wheel = scored
            .Select(entry => new
            {
                entry.Fund,
                Weight = Math.Exp(
                    (ScoreWeightWorth * Normalise(entry.Worth, worthMin, worthMax))
                    + (ScoreWeightDividends * Normalise(entry.Dividends, dividendMin, dividendMax))
                    + (ScoreWeightGrowth * Normalise(entry.Growth, growthMin, growthMax))
                    + (ScoreWeightPopularity * Normalise(entry.Popularity, popularityMin, popularityMax)))
                    * entry.RoomFraction,
            })
            .OrderByDescending(slot => slot.Weight)
            .ThenBy(slot => slot.Fund.Id)
            .ToList();

        var totalWeight = wheel.Sum(slot => slot.Weight);
        if (totalWeight <= 0.0)
        {
            return wheel[0].Fund;
        }

        var target = Math.Clamp(selectionPoint, 0.0, 1.0) * totalWeight;
        var cumulative = 0.0;
        foreach (var slot in wheel)
        {
            cumulative += slot.Weight;
            if (target < cumulative)
            {
                return slot.Fund;
            }
        }

        return wheel[^1].Fund;
    }

    // The share of a fund's member capacity still free. Candidates are always below the cap, so this stays
    // positive; a fuller fund weighs less on the join wheel, easing it toward a soft close as it approaches capacity.
    private double RoomFraction(CollectiveFund fund)
    {
        var capacity = MemberCapacity;
        if (capacity <= 0)
        {
            return 0.0;
        }

        var free = capacity - membershipsByFundId[fund.Id].Count;
        return Math.Max(0.0, (double)free / capacity);
    }

    private decimal FundNetWorth(Participant fundParticipant)
    {
        var owned = ownedByParticipant.GetValueOrDefault(fundParticipant.Id) ?? [];
        var holdingsValue = owned.Sum(holding => holding.Quantity * latestPriceByCompany.GetValueOrDefault(holding.CompanyId));

        // A fund can now carry debt (from a leave-payout loan), so net worth subtracts the open-loan liability,
        // matching the participant worth definition and keeping the founder-close and scoring reads honest.
        var loanLiability = (openLoansByFundParticipant.GetValueOrDefault(fundParticipant.Id) ?? [])
            .Sum(loan => loan.TotalLiability);
        var marginLiability = marginLiabilityByFundParticipant.GetValueOrDefault(fundParticipant.Id);
        return fundParticipant.CurrentBalance + holdingsValue - loanLiability - marginLiability;
    }

    private decimal RecentDividends(int fundParticipantId)
    {
        if (!fundDividendByCycleId.TryGetValue(fundParticipantId, out var byCycle))
        {
            return 0m;
        }

        return payoutCycleIdsDesc
            .Take(JoinDividendLookbackPayouts)
            .Sum(cycleId => byCycle.GetValueOrDefault(cycleId));
    }

    // A fund that owns no shares and whose spendable cash cannot cover even the cheapest share is idle; after
    // MaxIdleCycles of that it unwinds through the normal closing flow. No random draws, so scripted tests are
    // unaffected. Idleness is judged against the same cash buffer the fund trades within.
    private void TrackIdleAndMaybeClose(CollectiveFund fund, int currentCycleId, DateTime now)
    {
        // Before any prices exist there is nothing to deal on either way, so idleness is not yet meaningful.
        if (latestPriceByCompany.Count == 0)
        {
            return;
        }

        var fundParticipant = participantsById[fund.ParticipantId];
        var ownsShares = (ownedByParticipant.GetValueOrDefault(fundParticipant.Id) ?? []).Count > 0;
        var spendable = fundParticipant.AvailableBalance * (1m - Math.Clamp(options.Value.CashBufferFraction, 0m, 1m));
        var idle = !ownsShares && spendable < latestPriceByCompany.Values.Min();

        if (!idle)
        {
            fund.IdleCycles = 0;
            return;
        }

        fund.IdleCycles++;
        if (fund.IdleCycles >= MaxIdleCycles)
        {
            BeginClosing(fund, fundParticipant, currentCycleId, now);
        }
    }

    private void BeginClosing(CollectiveFund fund, Participant fundParticipant, int currentCycleId, DateTime now)
    {
        fund.Status = CollectiveFundStatus.GoingToBeClosed;
        ProcessClosing(fund, currentCycleId, now);
    }

    // While closing a fund places no buys: it cancels any standing buys, keeps every remaining share listed for
    // sale, and once it holds nothing splits its cash equally among the survivors and shuts down.
    private void ProcessClosing(CollectiveFund fund, int currentCycleId, DateTime now)
    {
        var fundParticipant = participantsById[fund.ParticipantId];

        foreach (var order in OpenOrders(fundParticipant.Id).Where(order => order.Type == OrderType.Buy).ToList())
        {
            CancelBuy(order, fundParticipant, currentCycleId, now);
        }

        var owned = ownedByParticipant.GetValueOrDefault(fundParticipant.Id) ?? [];
        if (owned.Count == 0)
        {
            if (pendingSettlementFundParticipantIds.Contains(fundParticipant.Id)
                || marginLiabilityByFundParticipant.GetValueOrDefault(fundParticipant.Id) > 0m
                || fundParticipant.CurrentBalance != fundParticipant.SettledCashBalance)
            {
                return;
            }

            FinalizeClose(fund, fundParticipant, currentCycleId, now);
            return;
        }

        // A dominant fund's uncommitted shares summed across companies can exceed the 32-bit accumulator;
        // sum in long and clamp before it feeds the int share-count parameter.
        var uncommitted = (int)Math.Clamp(
            owned.Sum(holding => (long)available.GetValueOrDefault((fundParticipant.Id, holding.CompanyId))),
            0L,
            int.MaxValue);
        if (uncommitted > 0)
        {
            ListFundSells(fundParticipant, owned, sharesToList: uncommitted, currentCycleId, now);
        }
    }

    private void FinalizeClose(CollectiveFund fund, Participant fundParticipant, int currentCycleId, DateTime now)
    {
        var members = membershipsByFundId[fund.Id];
        if (members.Count > 0)
        {
            var orderedMembers = members.OrderBy(membership => membership.ParticipantId).ToList();
            var payableMembers = orderedMembers
                .Where(membership => participantsById.ContainsKey(membership.ParticipantId))
                .ToList();
            // With no live member account left there is no recipient, so normal close drops the orphaned balance.
            var transferableCash = TransferableCash(fundParticipant);
            var share = payableMembers.Count == 0
                ? 0m
                : Math.Floor((transferableCash / payableMembers.Count) * 100m) / 100m;
            var residualCents = payableMembers.Count == 0
                ? 0
                : decimal.ToInt32(Math.Floor((transferableCash - (share * payableMembers.Count)) * 100m));
            var payoutIndex = 0;
            foreach (var membership in orderedMembers)
            {
                // A member whose participant row left the market leaves a stale membership behind; just drop it,
                // recording a zero-payout leave so the fund's history has no silent gap.
                if (!participantsById.TryGetValue(membership.ParticipantId, out var member))
                {
                    AddMembershipEvent(fund, membership.ParticipantId, CollectiveFundMembershipEventType.Left, 0m, currentCycleId, now);
                    RemoveMembership(fund, membership);
                    continue;
                }

                // Assigning indivisible cents in stable participant order keeps total payouts within fund cash.
                var payout = share + (payoutIndex < residualCents ? 0.01m : 0m);
                payoutIndex++;

                if (payout > 0m)
                {
                    member.CurrentBalance += payout;
                    member.SettledCashBalance += payout;
                    AddFundTransaction(member.Id, payout, currentCycleId, now);
                }

                AddMembershipEvent(fund, member.Id, CollectiveFundMembershipEventType.Left, payout, currentCycleId, now);

                // A payout that barely dents the deposit (a zero payout dents nothing) is a devastating loss:
                // flag the member so the market-exit service can offer a one-shot quit on its first shareless cycle.
                if (membership.DepositAmount > 0m && payout <= membership.DepositAmount * FundLossFlagFraction)
                {
                    member.PendingFundLossExitRoll = true;
                }

                RemoveMembership(fund, membership);
            }
        }

        // Rounding dust below the per-member cent is dropped so the closed fund settles flat.
        fundParticipant.CurrentBalance = 0m;
        fundParticipant.SettledCashBalance = 0m;
        fundParticipant.ReservedBalance = 0m;
        fundParticipant.IsActive = false;
        fund.Status = CollectiveFundStatus.Closed;
        fund.ClosedAt = now;

        // A winding-down fund's loans are discharged like any departing borrower's.
        if (openLoansByFundParticipant.TryGetValue(fundParticipant.Id, out var fundLoans))
        {
            foreach (var loan in fundLoans)
            {
                LoanService.MarkClosed(loan, LoanCloseReason.ParticipantDeparted, currentCycleId, now);
            }
        }

        // A fund that winds down while a crisis is active joins that crisis's timeline.
        if (activeCrisis is not null)
        {
            dbContext.CrisisEvents.Add(new CrisisEvent
            {
                CrisisId = activeCrisis.Id,
                Type = CrisisEventType.FundClosed,
                Description = $"{fundParticipant.Name} fund closed",
                CreatedInCycleId = currentCycleId,
                CreatedInCycleNumber = crisisCycleNumber,
                CreatedAt = now,
            });
        }
    }

    private async Task MaybeJoinOrOpenAsync(Participant participant, int currentCycleId, int currentCycleNumber, DateTime now)
    {
        // A member that left to chase a better fund waits, flag still set, until its old membership is fully wound
        // up; once free it lands in the best available fund with no cash ceiling and no roll, then the flag clears.
        if (participant.PendingFundSwitch)
        {
            if (membershipByParticipantId.ContainsKey(participant.Id))
            {
                return;
            }

            participant.PendingFundSwitch = false;
            // A switcher already gave up its old fund, so it takes the top-weighted fund outright rather than
            // rolling the wheel; passing 0 keeps this draw-free and lands it on the strongest available fund.
            if (participant.IsActive && !participant.IsBankrupt && SelectBestFund(JoinableFunds(), 0.0) is { } switchTarget)
            {
                JoinFund(switchTarget, participantsById[switchTarget.ParticipantId], participant, currentCycleId, now);
            }

            return;
        }

        if (!participant.IsActive
            || participant.IsBankrupt
            || participant.CurrentBalance >= options.Value.JoinBalanceCeiling
            || TransferableCash(participant) <= 0m
            || membershipByParticipantId.ContainsKey(participant.Id))
        {
            return;
        }

        var joinable = JoinableFunds();
        var growingJoinable = joinable.Any(fund => growingFundIds.Contains(fund.Id));

        var roll = random.NextDouble();
        var joinChance = JoinChance(participant.CannotBuyCycles)
            + (growingJoinable ? chanceRates.Value.ChanceModifiers.FundGrowthJoinBonus : 0.0);
        var openChance = OpenChance(participant.CannotBuyCycles);

        if (joinable.Count > 0 && roll < joinChance)
        {
            // Reuse the join roll to ride the selection wheel: because the join fired, roll sits in [0, joinChance),
            // so roll / joinChance is a uniform point in [0, 1). Taking no extra draw keeps the join-pass draw
            // discipline (one draw per eligible trader) intact.
            var selectionPoint = joinChance > 0.0 ? Math.Min(roll / joinChance, 0.999999) : 0.0;
            var target = SelectBestFund(joinable, selectionPoint)!;
            JoinFund(target, participantsById[target.ParticipantId], participant, currentCycleId, now);
            return;
        }

        var openThreshold = (joinable.Count > 0 ? joinChance : 0.0) + openChance;
        if (roll < openThreshold)
        {
            await OpenFundAsync(participant, currentCycleId, currentCycleNumber, now);
        }
    }

    private async Task OpenFundAsync(Participant founder, int currentCycleId, int currentCycleNumber, DateTime now)
    {
        var fundParticipant = new Participant
        {
            Name = $"{founder.Name}'s Fund #{currentCycleNumber}",
            Type = ParticipantType.CollectiveFund,
            // The founder runs the fund, so it trades with their personality: snapshot the founder's characteristics
            // at creation and keep them even if the founder later reprofiles, leaves, or exits the market.
            Temperament = founder.Temperament,
            RiskProfile = founder.RiskProfile,
            InitialBalance = 0m,
            CurrentBalance = 0m,
            SettledCashBalance = 0m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        dbContext.Participants.Add(fundParticipant);
        await dbContext.SaveChangesAsync();

        var fund = new CollectiveFund
        {
            ParticipantId = fundParticipant.Id,
            FoundedByParticipantId = founder.Id,
            Status = CollectiveFundStatus.Active,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        };
        dbContext.CollectiveFunds.Add(fund);
        await dbContext.SaveChangesAsync();

        participantsById[fundParticipant.Id] = fundParticipant;
        funds.Add(fund);
        membershipsByFundId[fund.Id] = [];

        JoinFund(fund, fundParticipant, founder, currentCycleId, now);
    }

    private void JoinFund(CollectiveFund fund, Participant fundParticipant, Participant member, int currentCycleId, DateTime now)
    {
        // Free any cash tied up in the member's own bids before contributing; the member keeps selling its
        // existing holdings on its own, but stops bidding once it is in the fund.
        foreach (var order in OpenOrders(member.Id).Where(order => order.Type == OrderType.Buy).ToList())
        {
            CancelBuy(order, member, currentCycleId, now);
        }

        var deposit = Round(TransferableCash(member) * ContributionFraction);
        if (deposit <= 0m)
        {
            return;
        }

        member.CurrentBalance -= deposit;
        member.SettledCashBalance -= deposit;
        fundParticipant.CurrentBalance += deposit;
        fundParticipant.SettledCashBalance += deposit;
        AddFundTransaction(member.Id, deposit, currentCycleId, now);
        AddFundTransaction(fundParticipant.Id, deposit, currentCycleId, now);
        AddMembershipEvent(fund, member.Id, CollectiveFundMembershipEventType.Joined, deposit, currentCycleId, now);

        var membership = new CollectiveFundParticipant
        {
            CollectiveFundId = fund.Id,
            ParticipantId = member.Id,
            JoinedAt = now,
            JoinedInCycleId = currentCycleId,
            DepositAmount = deposit,
            LeaveRampCycles = 0,
            IsLeaving = false,
        };
        dbContext.CollectiveFundParticipants.Add(membership);
        membershipByParticipantId[member.Id] = membership;
        membershipsByFundId[fund.Id].Add(membership);
    }

    private void ListFundSellsForCash(Participant fundParticipant, decimal cashNeeded, int currentCycleId, DateTime now)
    {
        var owned = ownedByParticipant.GetValueOrDefault(fundParticipant.Id) ?? [];
        var raised = 0m;

        foreach (var holding in owned.OrderBy(entry => entry.CompanyId))
        {
            if (raised >= cashNeeded)
            {
                break;
            }

            if (!latestPriceByCompany.TryGetValue(holding.CompanyId, out var price) || price <= 0m)
            {
                continue;
            }

            var sellPrice = Round(price * (1m - SaleDiscount));
            if (bandByCompany.GetValueOrDefault(holding.CompanyId) is { } band)
            {
                sellPrice = band.ClampToActiveBand(sellPrice);
            }

            if (sellPrice <= 0m)
            {
                continue;
            }

            // Cash needed divided by a low price can exceed int range; clamp the double before the cast so it
            // cannot silently wrap to a negative share count.
            var sharesNeeded = (int)Math.Clamp(Math.Ceiling((double)((cashNeeded - raised) / sellPrice)), 0d, int.MaxValue);
            var listed = ListCompanySell(fundParticipant, holding.CompanyId, sellPrice, sharesNeeded, currentCycleId, now);
            raised += listed * sellPrice;
        }
    }

    private void ListFundSells(Participant fundParticipant, List<OwnedHolding> owned, int sharesToList, int currentCycleId, DateTime now)
    {
        var remaining = sharesToList;
        foreach (var holding in owned.OrderBy(entry => entry.CompanyId))
        {
            if (remaining <= 0)
            {
                break;
            }

            if (!latestPriceByCompany.TryGetValue(holding.CompanyId, out var price) || price <= 0m)
            {
                continue;
            }

            var sellPrice = Round(price * (1m - SaleDiscount));
            if (bandByCompany.GetValueOrDefault(holding.CompanyId) is { } band)
            {
                sellPrice = band.ClampToActiveBand(sellPrice);
            }

            if (sellPrice <= 0m)
            {
                continue;
            }

            remaining -= ListCompanySell(fundParticipant, holding.CompanyId, sellPrice, remaining, currentCycleId, now);
        }
    }

    private int ListCompanySell(
        Participant fundParticipant,
        int companyId,
        decimal sellPrice,
        int maxShares,
        int currentCycleId,
        DateTime now)
    {
        if (maxShares <= 0)
        {
            return 0;
        }

        var availableQuantity = available.GetValueOrDefault((fundParticipant.Id, companyId));
        var quantity = Math.Min(maxShares, availableQuantity);
        if (quantity <= 0)
        {
            return 0;
        }

        dbContext.Orders.Add(new Order
        {
            ParticipantId = fundParticipant.Id,
            CompanyId = companyId,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = quantity,
            FilledQuantity = 0,
            LimitPrice = sellPrice,
            ReservedCashAmount = 0m,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
            UpdatedAt = now,
        });

        available[(fundParticipant.Id, companyId)] = availableQuantity - quantity;
        return quantity;
    }

    private void CancelBuy(Order order, Participant participant, int currentCycleId, DateTime now)
    {
        var release = order.ReservedCashAmount;
        if (release > 0m)
        {
            participant.ReservedBalance -= release;
            order.ReservedCashAmount = 0m;
            dbContext.MoneyTransactions.Add(new MoneyTransaction
            {
                ParticipantId = participant.Id,
                Type = MoneyTransactionType.Release,
                Amount = release,
                RelatedOrderId = order.Id,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
            });
        }

        order.Status = OrderStatus.Cancelled;
        order.UpdatedAt = now;
    }

    private void RemoveMembership(CollectiveFund fund, CollectiveFundParticipant membership)
    {
        dbContext.CollectiveFundParticipants.Remove(membership);
        membershipByParticipantId.Remove(membership.ParticipantId);
        membershipsByFundId[fund.Id].Remove(membership);
    }

    private void AddFundTransaction(int participantId, decimal amount, int currentCycleId, DateTime now) =>
        dbContext.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = participantId,
            Type = MoneyTransactionType.CollectiveFund,
            Amount = amount,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });

    private void AddMembershipEvent(CollectiveFund fund, int memberParticipantId, CollectiveFundMembershipEventType type, decimal amount, int currentCycleId, DateTime now) =>
        dbContext.CollectiveFundMembershipEvents.Add(new CollectiveFundMembershipEvent
        {
            CollectiveFundId = fund.Id,
            FundParticipantId = fund.ParticipantId,
            ParticipantId = memberParticipantId,
            Type = type,
            Amount = amount,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });

    private IEnumerable<Order> OpenOrders(int participantId) =>
        openOrdersByParticipant.GetValueOrDefault(participantId) ?? [];

    private decimal PendingSaleValue(int fundParticipantId) =>
        OpenOrders(fundParticipantId)
            .Where(order => order.Type == OrderType.Sell
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .Sum(order => order.RemainingQuantity * order.LimitPrice);

    // Each step lifts the bonus once a buying drought passes 10 then 20 cycles, capped at the upper tier.
    private double JoinChance(int cannotBuyCycles) =>
        chanceRates.Value.EventTriggerChances.FundJoin + cannotBuyCycles switch { >= 20 => 0.40, >= 10 => 0.20, _ => 0.0 };

    private double OpenChance(int cannotBuyCycles) =>
        chanceRates.Value.EventTriggerChances.FundOpen + cannotBuyCycles switch { >= 20 => 0.20, >= 10 => 0.10, _ => 0.0 };

    private Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync() =>
        PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal TransferableCash(Participant participant) =>
        Math.Max(0m, Math.Min(participant.AvailableBalance, participant.SettledCashBalance));

    private readonly record struct OwnedHolding(int OwnerId, int CompanyId, int Quantity);
}
