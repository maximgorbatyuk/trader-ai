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

    private Dictionary<int, Participant> participantsById = null!;
    private List<CollectiveFund> funds = null!;
    private Dictionary<int, List<CollectiveFundParticipant>> membershipsByFundId = null!;
    private Dictionary<int, CollectiveFundParticipant> membershipByParticipantId = null!;
    private Dictionary<int, List<OwnedShare>> ownedByParticipant = null!;
    private Dictionary<int, List<Order>> openOrdersByParticipant = null!;
    private Dictionary<int, decimal> latestPriceByCompany = null!;
    private HashSet<int> committed = null!;

    // Draw discipline for a scripted Random in tests: no draws while closing funds or returning deposits; one
    // NextDouble() per active-fund member at or above the leave line (fund id, then participant id order); one
    // NextDouble() per eligible independent trader, in id order, for the join/open band.
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
            foreach (var membership in membershipsByFundId[fund.Id].OrderBy(member => member.ParticipantId).ToList())
            {
                if (fund.Status != CollectiveFundStatus.Active)
                {
                    break;
                }

                var member = participantsById[membership.ParticipantId];
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

        ownedByParticipant = (await dbContext.Shares
                .Where(share => share.OwnerId != null)
                .Select(share => new OwnedShare(share.OwnerId!.Value, share.CompanyId, share.Id))
                .ToListAsync())
            .GroupBy(share => share.OwnerId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var openOrders = await dbContext.Orders
            .Where(order => order.ParticipantId != null
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .Include(order => order.OrderShares)
            .ToListAsync();
        openOrdersByParticipant = openOrders
            .GroupBy(order => order.ParticipantId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        // A share is only ever offered by its own owner's sell order, so this single set tracks every share
        // currently committed to an open order across all participants.
        committed = openOrders.SelectMany(order => order.OrderShares).Select(link => link.ShareId).ToHashSet();

        participantsById = await dbContext.Participants.ToDictionaryAsync(participant => participant.Id);

        funds = await dbContext.CollectiveFunds.ToListAsync();
        var memberships = await dbContext.CollectiveFundParticipants.ToListAsync();
        membershipByParticipantId = memberships.ToDictionary(member => member.ParticipantId);
        membershipsByFundId = funds.ToDictionary(
            fund => fund.Id,
            fund => memberships.Where(member => member.CollectiveFundId == fund.Id).ToList());
    }

    private void MaybeDecideLeave(CollectiveFund fund, CollectiveFundParticipant membership, Participant member, int currentCycleId, DateTime now)
    {
        if (member.CurrentBalance < LeaveBalanceThreshold)
        {
            membership.LeaveRampCycles = 0;
            return;
        }

        membership.LeaveRampCycles++;
        var chance = Math.Min(LeaveBaseChance + (LeaveStepPerCycle * (membership.LeaveRampCycles - 1)), LeaveMaxChance);
        if (random.NextDouble() >= chance)
        {
            return;
        }

        membership.IsLeaving = true;
        AdvanceLeave(fund, membership, member, currentCycleId, now);
    }

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

        var uncommitted = owned.Where(share => !committed.Contains(share.Id)).ToList();
        if (uncommitted.Count > 0)
        {
            ListFundSells(fundParticipant, uncommitted, sharesToList: uncommitted.Count, currentCycleId, now);
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
                if (share > 0m)
                {
                    var member = participantsById[membership.ParticipantId];
                    member.CurrentBalance += share;
                    AddFundTransaction(member.Id, share, currentCycleId, now);
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
        if (!participant.IsActive
            || participant.IsBankrupt
            || participant.CurrentBalance >= JoinBalanceCeiling
            || membershipByParticipantId.ContainsKey(participant.Id))
        {
            return;
        }

        var joinable = funds
            .Where(fund => fund.Status == CollectiveFundStatus.Active && membershipsByFundId[fund.Id].Count < MaxMembers)
            .ToList();

        var roll = random.NextDouble();
        var joinChance = JoinChance(participant.CannotBuyCycles);
        var openChance = OpenChance(participant.CannotBuyCycles);

        if (joinable.Count > 0 && roll < joinChance)
        {
            // Spread joiners toward the emptiest fund so capacity fills evenly rather than piling onto one.
            var target = joinable
                .OrderBy(fund => membershipsByFundId[fund.Id].Count)
                .ThenBy(fund => fund.Id)
                .First();
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
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
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

        foreach (var byCompany in owned.GroupBy(share => share.CompanyId))
        {
            if (raised >= cashNeeded)
            {
                break;
            }

            if (!latestPriceByCompany.TryGetValue(byCompany.Key, out var price) || price <= 0m)
            {
                continue;
            }

            var sellPrice = Round(price * (1m - SaleDiscount));
            if (sellPrice <= 0m)
            {
                continue;
            }

            var sharesNeeded = (int)Math.Ceiling((double)((cashNeeded - raised) / sellPrice));
            var listed = ListCompanySell(fundParticipant, byCompany, sellPrice, sharesNeeded, currentCycleId, now);
            raised += listed * sellPrice;
        }
    }

    private void ListFundSells(Participant fundParticipant, List<OwnedShare> owned, int sharesToList, int currentCycleId, DateTime now)
    {
        var remaining = sharesToList;
        foreach (var byCompany in owned.GroupBy(share => share.CompanyId))
        {
            if (remaining <= 0)
            {
                break;
            }

            if (!latestPriceByCompany.TryGetValue(byCompany.Key, out var price) || price <= 0m)
            {
                continue;
            }

            var sellPrice = Round(price * (1m - SaleDiscount));
            if (sellPrice <= 0m)
            {
                continue;
            }

            remaining -= ListCompanySell(fundParticipant, byCompany, sellPrice, remaining, currentCycleId, now);
        }
    }

    private int ListCompanySell(
        Participant fundParticipant,
        IGrouping<int, OwnedShare> byCompany,
        decimal sellPrice,
        int maxShares,
        int currentCycleId,
        DateTime now)
    {
        if (maxShares <= 0)
        {
            return 0;
        }

        var freeShareIds = byCompany
            .Where(share => !committed.Contains(share.Id))
            .Select(share => share.Id)
            .Take(maxShares)
            .ToList();
        if (freeShareIds.Count == 0)
        {
            return 0;
        }

        var order = new Order
        {
            ParticipantId = fundParticipant.Id,
            CompanyId = byCompany.Key,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = freeShareIds.Count,
            FilledQuantity = 0,
            LimitPrice = sellPrice,
            ReservedCashAmount = 0m,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var shareId in freeShareIds)
        {
            order.OrderShares.Add(new OrderShare { ShareId = shareId });
            committed.Add(shareId);
        }

        dbContext.Orders.Add(order);
        return freeShareIds.Count;
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

    private readonly record struct OwnedShare(int OwnerId, int CompanyId, int Id);
}
