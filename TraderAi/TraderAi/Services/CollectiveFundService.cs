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
    Random random)
{
    // Only traders below this cash line are candidates to pool into a fund; members must leave once their own
    // cash climbs back above the upper line.
    private const decimal JoinBalanceCeiling = 500_000m;
    private const decimal LeaveBalanceThreshold = 100_000_000m;

    private const int MaxMembers = 20;

    // No fund may be created or joined during the market's opening stretch.
    private const int QuietCycles = 50;

    // Base per-cycle odds an eligible trader joins an existing fund or opens a new one; a long can't-buy
    // drought adds to both (see JoinChance/OpenChance).
    private const double BaseJoinChance = 0.05;
    private const double BaseOpenChance = 0.03;

    // Once a member sits at or above the leave line its exit chance starts here and ramps each cycle to the cap.
    private const double LeaveBaseChance = 0.20;
    private const double LeaveStepPerCycle = 0.02;
    private const double LeaveMaxChance = 0.90;

    private const decimal ContributionFraction = 0.90m;

    // Fund forced sales (raising a leaver's deposit, or dumping everything while closing) undercut the market so
    // the order actually crosses; order ageing pushes any unsold remainder lower over the following cycles.
    private const decimal SaleDiscount = 0.10m;

    // Share of a fund's own dividend receipt that is passed straight through to its members, split by deposit.
    private const decimal DividendPassThroughFraction = 0.50m;

    // A fund that owns nothing and cannot afford even the cheapest share for this many consecutive cycles unwinds.
    private const int MaxIdleCycles = 20;

    // The slice of its cash a fund never spends (mirrors MarketService.CollectiveFundCashBufferFraction), so
    // idleness is judged against what the fund would actually be able to deal with.
    private const decimal CashBufferFraction = 0.10m;

    // A closing fund that hands a member a payout at or below this fraction of its deposit inflicted a
    // devastating loss; the member is flagged so the market-exit service can offer it a one-shot chance to quit.
    private const decimal FundLossFlagFraction = 0.20m;

    // A non-founder member may leave to chase a better fund only after this tenure; each cycle past it, it rolls
    // to switch at the base chance shifted by temperament (aggressive leaves more readily, conservative less).
    private const int MinTenureToSwitchCycles = 20;
    private const double SwitchBaseChance = 0.25;
    private const double SwitchTemperamentDelta = 0.05;

    // The founder closes the fund once its net worth collapses to this fraction of its all-time peak, or once it
    // has drawn no dividend income across its last FounderDividendLookbackPayouts post-founding payout cycles.
    private const decimal FounderLossFraction = 0.15m;
    private const int FounderDividendLookbackPayouts = 2;

    // A joiner scores each candidate fund on size, net worth, and dividends over its last this-many payout
    // events, each min-max normalised across the candidates and summed with the weights below.
    private const int JoinDividendLookbackPayouts = 3;
    private const double ScoreWeightSize = 1.0;
    private const double ScoreWeightWorth = 1.0;
    private const double ScoreWeightDividends = 1.0;

    private Dictionary<int, Participant> participantsById = null!;
    private List<CollectiveFund> funds = null!;
    private Dictionary<int, List<CollectiveFundParticipant>> membershipsByFundId = null!;
    private Dictionary<int, CollectiveFundParticipant> membershipByParticipantId = null!;
    private Dictionary<int, List<OwnedHolding>> ownedByParticipant = null!;
    private Dictionary<int, List<Order>> openOrdersByParticipant = null!;
    private Dictionary<int, decimal> latestPriceByCompany = null!;
    private Dictionary<(int ParticipantId, int CompanyId), int> available = null!;

    // Distinct dividend-payout cycle ids, most recent first (ids rise monotonically with cycles), and each fund
    // participant's dividend receipts keyed by payout cycle id; both feed fund scoring and the founder close.
    private List<int> payoutCycleIdsDesc = null!;
    private Dictionary<int, Dictionary<int, decimal>> fundDividendByCycleId = null!;

    // Draw discipline for a scripted Random in tests: no draws while closing funds, returning deposits, ratcheting
    // peak worth, running the founder close, or dropping a stale membership. In the member pass (fund id, then participant id order) each
    // member draws at most once — the forced-leave roll if it sits at or above the leave line, otherwise the
    // switch roll if it is a non-founder past the minimum tenure, otherwise nothing. In the join pass (independent
    // traders, id order) a switch-flagged member draws nothing, and every other eligible trader draws once.
    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        await LoadStateAsync();

        foreach (var fund in funds.Where(fund => fund.Status == CollectiveFundStatus.GoingToBeClosed).OrderBy(fund => fund.Id).ToList())
        {
            ProcessClosing(fund, currentCycleId, now);
        }

        foreach (var fund in funds.Where(fund => fund.Status == CollectiveFundStatus.Active).OrderBy(fund => fund.Id).ToList())
        {
            RatchetPeakNetWorth(fund);
            if (MaybeFounderClose(fund, currentCycleId, now))
            {
                continue;
            }

            TrackIdleAndMaybeClose(fund, currentCycleId, now);
            if (fund.Status != CollectiveFundStatus.Active)
            {
                continue;
            }

            foreach (var membership in membershipsByFundId[fund.Id].OrderBy(member => member.ParticipantId).ToList())
            {
                if (fund.Status != CollectiveFundStatus.Active)
                {
                    break;
                }

                // A member whose participant row left the market leaves a stale membership behind; drop it
                // rather than dereferencing a key that no longer exists.
                if (!participantsById.TryGetValue(membership.ParticipantId, out var member))
                {
                    RemoveMembership(fund, membership);
                    continue;
                }

                membership.TenureCycles++;

                if (membership.IsLeaving)
                {
                    AdvanceLeave(fund, membership, member, currentCycleId, now);
                }
                else
                {
                    MaybeDecideLeave(fund, membership, member, currentCycleId, now);
                }
            }
        }

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

            var cut = Round(pool * member.DepositAmount / totalDeposit);
            if (cut <= 0m)
            {
                continue;
            }

            memberParticipant.CurrentBalance += cut;
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
    }

    private async Task LoadStateAsync()
    {
        latestPriceByCompany = await LatestPriceByCompanyAsync();

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
        membershipByParticipantId = memberships.ToDictionary(member => member.ParticipantId);
        membershipsByFundId = funds.ToDictionary(
            fund => fund.Id,
            fund => memberships.Where(member => member.CollectiveFundId == fund.Id).ToList());
    }

    private void MaybeDecideLeave(CollectiveFund fund, CollectiveFundParticipant membership, Participant member, int currentCycleId, DateTime now)
    {
        // A member that has grown rich graduates out of the fund on the ramping leave roll and does not come back.
        if (member.CurrentBalance >= LeaveBalanceThreshold)
        {
            membership.LeaveRampCycles++;
            var rampChance = Math.Min(LeaveBaseChance + (LeaveStepPerCycle * (membership.LeaveRampCycles - 1)), LeaveMaxChance);
            if (random.NextDouble() >= rampChance)
            {
                return;
            }

            membership.IsLeaving = true;
            AdvanceLeave(fund, membership, member, currentCycleId, now);
            return;
        }

        membership.LeaveRampCycles = 0;

        // Founders stay put; everyone else, once past the minimum tenure, rolls to leave and chase a better fund.
        if (member.Id == fund.FoundedByParticipantId || membership.TenureCycles < MinTenureToSwitchCycles)
        {
            return;
        }

        if (random.NextDouble() >= SwitchChance(member.Temperament))
        {
            return;
        }

        member.PendingFundSwitch = true;
        membership.IsLeaving = true;
        AdvanceLeave(fund, membership, member, currentCycleId, now);
    }

    private static double SwitchChance(Temperament temperament) =>
        SwitchBaseChance + temperament switch
        {
            Temperament.Aggressive => SwitchTemperamentDelta,
            Temperament.Conservative => -SwitchTemperamentDelta,
            _ => 0.0,
        };

    // Returns a leaving member's deposit once the fund has the cash, otherwise lists shares to raise the
    // shortfall and waits. If the exit would shrink the fund below a pair, the whole fund unwinds instead.
    private void AdvanceLeave(CollectiveFund fund, CollectiveFundParticipant membership, Participant member, int currentCycleId, DateTime now)
    {
        if (fund.Status != CollectiveFundStatus.Active)
        {
            return;
        }

        var fundParticipant = participantsById[fund.ParticipantId];

        if (membershipsByFundId[fund.Id].Count <= 2)
        {
            BeginClosing(fund, fundParticipant, currentCycleId, now);
            return;
        }

        var deposit = membership.DepositAmount;
        if (fundParticipant.AvailableBalance >= deposit)
        {
            fundParticipant.CurrentBalance -= deposit;
            member.CurrentBalance += deposit;
            AddFundTransaction(fundParticipant.Id, deposit, currentCycleId, now);
            AddFundTransaction(member.Id, deposit, currentCycleId, now);
            RemoveMembership(fund, membership);
            return;
        }

        // Existing open sells will already bring in cash, so only list the part of the shortfall they do not cover.
        var shortfall = deposit - fundParticipant.AvailableBalance;
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

    private List<CollectiveFund> JoinableFunds() =>
        funds
            .Where(fund => fund.Status == CollectiveFundStatus.Active && membershipsByFundId[fund.Id].Count < MaxMembers)
            .ToList();

    // Picks the fund a joiner prefers: bigger, worth more, and paying more dividends recently. Each metric is
    // min-max normalised across the candidates so no single scale dominates, then summed with the score weights;
    // ties fall to the lowest fund id.
    private CollectiveFund? SelectBestFund(List<CollectiveFund> candidates)
    {
        if (candidates.Count <= 1)
        {
            return candidates.Count == 1 ? candidates[0] : null;
        }

        var scored = candidates
            .Select(fund => new
            {
                Fund = fund,
                Size = (double)membershipsByFundId[fund.Id].Count,
                Worth = (double)FundNetWorth(participantsById[fund.ParticipantId]),
                Dividends = (double)RecentDividends(fund.ParticipantId),
            })
            .ToList();

        var sizeMin = scored.Min(entry => entry.Size);
        var sizeMax = scored.Max(entry => entry.Size);
        var worthMin = scored.Min(entry => entry.Worth);
        var worthMax = scored.Max(entry => entry.Worth);
        var dividendMin = scored.Min(entry => entry.Dividends);
        var dividendMax = scored.Max(entry => entry.Dividends);

        static double Normalise(double value, double min, double max) => max > min ? (value - min) / (max - min) : 0.0;

        return scored
            .OrderByDescending(entry =>
                (ScoreWeightSize * Normalise(entry.Size, sizeMin, sizeMax))
                + (ScoreWeightWorth * Normalise(entry.Worth, worthMin, worthMax))
                + (ScoreWeightDividends * Normalise(entry.Dividends, dividendMin, dividendMax)))
            .ThenBy(entry => entry.Fund.Id)
            .First()
            .Fund;
    }

    private decimal FundNetWorth(Participant fundParticipant)
    {
        var owned = ownedByParticipant.GetValueOrDefault(fundParticipant.Id) ?? [];
        var holdingsValue = owned.Sum(holding => holding.Quantity * latestPriceByCompany.GetValueOrDefault(holding.CompanyId));
        return fundParticipant.CurrentBalance + holdingsValue;
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
        var spendable = fundParticipant.AvailableBalance * (1m - CashBufferFraction);
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
            FinalizeClose(fund, fundParticipant, currentCycleId, now);
            return;
        }

        var uncommitted = owned.Sum(holding => available.GetValueOrDefault((fundParticipant.Id, holding.CompanyId)));
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
            var share = Round(fundParticipant.AvailableBalance / members.Count);
            foreach (var membership in members.ToList())
            {
                // A member whose participant row left the market leaves a stale membership behind; just drop it.
                if (!participantsById.TryGetValue(membership.ParticipantId, out var member))
                {
                    RemoveMembership(fund, membership);
                    continue;
                }

                if (share > 0m)
                {
                    member.CurrentBalance += share;
                    AddFundTransaction(member.Id, share, currentCycleId, now);
                }

                // A payout that barely dents the deposit (a zero payout dents nothing) is a devastating loss:
                // flag the member so the market-exit service can offer a one-shot quit on its first shareless cycle.
                if (membership.DepositAmount > 0m && share <= membership.DepositAmount * FundLossFlagFraction)
                {
                    member.PendingFundLossExitRoll = true;
                }

                RemoveMembership(fund, membership);
            }
        }

        // Rounding dust below the per-member cent is dropped so the closed fund settles flat.
        fundParticipant.CurrentBalance = 0m;
        fundParticipant.ReservedBalance = 0m;
        fundParticipant.IsActive = false;
        fund.Status = CollectiveFundStatus.Closed;
        fund.ClosedAt = now;
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
            if (participant.IsActive && !participant.IsBankrupt && SelectBestFund(JoinableFunds()) is { } switchTarget)
            {
                JoinFund(switchTarget, participantsById[switchTarget.ParticipantId], participant, currentCycleId, now);
            }

            return;
        }

        if (!participant.IsActive
            || participant.IsBankrupt
            || participant.CurrentBalance >= JoinBalanceCeiling
            || membershipByParticipantId.ContainsKey(participant.Id))
        {
            return;
        }

        var joinable = JoinableFunds();

        var roll = random.NextDouble();
        var joinChance = JoinChance(participant.CannotBuyCycles);
        var openChance = OpenChance(participant.CannotBuyCycles);

        if (joinable.Count > 0 && roll < joinChance)
        {
            var target = SelectBestFund(joinable)!;
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

        var deposit = Round(member.CurrentBalance * ContributionFraction);
        if (deposit <= 0m)
        {
            return;
        }

        member.CurrentBalance -= deposit;
        fundParticipant.CurrentBalance += deposit;
        AddFundTransaction(member.Id, deposit, currentCycleId, now);
        AddFundTransaction(fundParticipant.Id, deposit, currentCycleId, now);

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
            if (sellPrice <= 0m)
            {
                continue;
            }

            var sharesNeeded = (int)Math.Ceiling((double)((cashNeeded - raised) / sellPrice));
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

    private IEnumerable<Order> OpenOrders(int participantId) =>
        openOrdersByParticipant.GetValueOrDefault(participantId) ?? [];

    private decimal PendingSaleValue(int fundParticipantId) =>
        OpenOrders(fundParticipantId)
            .Where(order => order.Type == OrderType.Sell
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .Sum(order => order.RemainingQuantity * order.LimitPrice);

    // Each step lifts the bonus once a buying drought passes 10 then 20 cycles, capped at the upper tier.
    private static double JoinChance(int cannotBuyCycles) =>
        BaseJoinChance + cannotBuyCycles switch { >= 20 => 0.40, >= 10 => 0.20, _ => 0.0 };

    private static double OpenChance(int cannotBuyCycles) =>
        BaseOpenChance + cannotBuyCycles switch { >= 20 => 0.20, >= 10 => 0.10, _ => 0.0 };

    private async Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync()
    {
        var snapshots = await dbContext.PriceSnapshots.ToListAsync();
        return snapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.Id).First().Price);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private readonly record struct OwnedHolding(int OwnerId, int CompanyId, int Quantity);
}
