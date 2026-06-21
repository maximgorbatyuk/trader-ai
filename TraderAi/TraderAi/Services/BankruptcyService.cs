using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Drives trader bankruptcies once per cycle, before the cycle's matching runs. A solvent trader whose net
// worth stays above the wealth line sees its bankruptcy chance ramp each cycle; when it fires the trader's
// cash is wiped and 80% of its holdings are dumped onto the order book at a discount. A trader still working
// through that sell-down has its unsold orders re-listed a step cheaper each cycle until the target is met.
// Called from inside order maintenance, which already holds the lock and owns the save, so this only stages
// changes on the shared context.
public sealed class BankruptcyService(
    AppDbContext dbContext,
    IOptions<BankruptcyOptions> options,
    Random random)
{
    // Only net worth above this line puts a trader at risk; cash plus shares valued at the latest price.
    private const decimal NetWorthThreshold = 1_000_000_000m;

    // No trader can go bankrupt during the market's opening stretch, and the wealth ramp does not start
    // accumulating until it passes, so an early simulation runs clean and the ramp stays gentle afterwards.
    private const int QuietCycles = 500;

    // The chance ramps up each consecutive cycle above the line and holds at the cap, kept gentle so a rich
    // trader lingers at risk for a long stretch rather than collapsing within a few cycles.
    private const double StepPerCycle = 0.002;
    private const double MaxProbability = 0.10;

    // A bankrupt trader must sell down this fraction of the shares it held when bankruptcy struck.
    private const decimal SellDownFraction = 0.80m;

    // The first forced-sale lists at the base discount off the current price; every unsold re-listing
    // deepens it by a step, floored so the asking price never reaches zero.
    private const decimal BaseDiscount = 0.20m;
    private const decimal DiscountStepSize = 0.05m;
    private const decimal MaxDiscount = 0.95m;

    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var latestPriceByCompany = await LatestPriceByCompanyAsync();

        var ownedShares = await dbContext.Shares
            .Where(share => share.OwnerId != null)
            .Select(share => new OwnedShare(share.OwnerId!.Value, share.CompanyId, share.Id))
            .ToListAsync();
        var ownedByParticipant = ownedShares
            .GroupBy(share => share.OwnerId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var openOrders = await dbContext.Orders
            .Where(order => order.ParticipantId != null
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .Include(order => order.OrderShares)
            .ToListAsync();
        var openOrdersByParticipant = openOrders
            .GroupBy(order => order.ParticipantId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        // A share is only ever offered by its own owner's sell order, so this single set tracks every share
        // currently committed to an open order across all participants; listing and cancelling keep it current.
        var committed = openOrders.SelectMany(order => order.OrderShares).Select(link => link.ShareId).ToHashSet();

        // Stable id order keeps the per-trader random draws reproducible for a scripted Random in tests.
        var participants = await dbContext.Participants.OrderBy(participant => participant.Id).ToListAsync();

        foreach (var participant in participants)
        {
            var owned = ownedByParticipant.GetValueOrDefault(participant.Id) ?? [];
            var openForParticipant = openOrdersByParticipant.GetValueOrDefault(participant.Id) ?? [];

            if (participant.IsBankrupt)
            {
                ContinueSellDown(participant, owned, openForParticipant, latestPriceByCompany, committed, currentCycleId, now);
                continue;
            }

            // Opening protection: no new bankruptcy, and no ramp, until the market clears its quiet window.
            if (currentCycleNumber <= QuietCycles)
            {
                continue;
            }

            MaybeTrigger(participant, owned, openForParticipant, latestPriceByCompany, committed, currentCycleId, now);
        }
    }

    private void MaybeTrigger(
        Participant participant,
        List<OwnedShare> owned,
        List<Order> openOrders,
        IReadOnlyDictionary<int, decimal> latestPriceByCompany,
        HashSet<int> committed,
        int currentCycleId,
        DateTime now)
    {
        // Only working traders carry bankruptcy risk; issuer-company participants are left out.
        if (!participant.IsActive
            || (participant.Type != ParticipantType.Individual && participant.Type != ParticipantType.AIAgent))
        {
            return;
        }

        var netWorth = participant.CurrentBalance + ShareWorth(owned, latestPriceByCompany);
        if (netWorth <= NetWorthThreshold)
        {
            participant.WealthyCycles = 0;
            return;
        }

        participant.WealthyCycles++;
        var probability = Math.Min(participant.WealthyCycles * StepPerCycle, MaxProbability);
        if (random.NextDouble() >= probability)
        {
            return;
        }

        var cashLost = participant.CurrentBalance;
        var shareWorth = ShareWorth(owned, latestPriceByCompany);

        // Wipe the trader: cancel every open order (releasing reserved cash and freeing offered shares), then
        // zero the balance and record the loss in the ledger.
        foreach (var order in openOrders)
        {
            if (order.Type == OrderType.Buy)
            {
                CancelBuy(order, participant, currentCycleId, now);
            }
            else
            {
                CancelSell(order, committed, now);
            }
        }

        if (cashLost > 0m)
        {
            dbContext.MoneyTransactions.Add(new MoneyTransaction
            {
                ParticipantId = participant.Id,
                Type = MoneyTransactionType.Bankruptcy,
                Amount = cashLost,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
            });
        }

        participant.CurrentBalance = 0m;
        participant.ReservedBalance = 0m;
        participant.IsActive = false;
        participant.IsBankrupt = true;
        participant.BankruptcyOwnedAtStart = owned.Count;
        participant.BankruptcyDiscountStep = 0;

        var (title, content) = DemoBankruptcyContent.Generate(participant.Name, cashLost, shareWorth, random);
        dbContext.Bankruptcies.Add(new Bankruptcy
        {
            ParticipantId = participant.Id,
            Title = title,
            Content = content,
            CashLost = cashLost,
            ShareWorth = shareWorth,
            TriggeredInCycleId = currentCycleId,
            TriggeredAt = now,
        });

        ListForcedSells(participant, SellDownTarget(participant), owned, latestPriceByCompany, committed, currentCycleId, now);
    }

    private void ContinueSellDown(
        Participant participant,
        List<OwnedShare> owned,
        List<Order> openOrders,
        IReadOnlyDictionary<int, decimal> latestPriceByCompany,
        HashSet<int> committed,
        int currentCycleId,
        DateTime now)
    {
        var sold = participant.BankruptcyOwnedAtStart - owned.Count;
        var remaining = SellDownTarget(participant) - sold;
        if (remaining <= 0)
        {
            participant.IsBankrupt = false;
            return;
        }

        // Any forced-sale order still open was not bought out last cycle; cancel it, deepen the discount, and
        // re-list the shares that remain. With nothing open the previous round cleared, so re-list at the
        // current step without deepening.
        var openSells = openOrders.Where(order => order.Type == OrderType.Sell).ToList();
        if (openSells.Count > 0)
        {
            foreach (var order in openSells)
            {
                CancelSell(order, committed, now);
            }

            participant.BankruptcyDiscountStep++;
        }

        ListForcedSells(participant, remaining, owned, latestPriceByCompany, committed, currentCycleId, now);
    }

    private void ListForcedSells(
        Participant participant,
        int sharesToList,
        List<OwnedShare> owned,
        IReadOnlyDictionary<int, decimal> latestPriceByCompany,
        HashSet<int> committed,
        int currentCycleId,
        DateTime now)
    {
        if (sharesToList <= 0)
        {
            return;
        }

        var discount = Math.Min(BaseDiscount + (DiscountStepSize * participant.BankruptcyDiscountStep), MaxDiscount);
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

            var sellPrice = Round(price * (1m - discount));
            if (sellPrice <= 0m)
            {
                continue;
            }

            var freeShareIds = byCompany
                .Where(share => !committed.Contains(share.Id))
                .Select(share => share.Id)
                .Take(remaining)
                .ToList();
            if (freeShareIds.Count == 0)
            {
                continue;
            }

            var order = new Order
            {
                ParticipantId = participant.Id,
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
            remaining -= freeShareIds.Count;
        }
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

    private void CancelSell(Order order, HashSet<int> committed, DateTime now)
    {
        foreach (var link in order.OrderShares)
        {
            committed.Remove(link.ShareId);
        }

        dbContext.OrderShares.RemoveRange(order.OrderShares);
        order.OrderShares.Clear();
        order.Status = OrderStatus.Cancelled;
        order.UpdatedAt = now;
    }

    private static int SellDownTarget(Participant participant) =>
        (int)Math.Round(participant.BankruptcyOwnedAtStart * SellDownFraction, MidpointRounding.AwayFromZero);

    private static decimal ShareWorth(List<OwnedShare> owned, IReadOnlyDictionary<int, decimal> latestPriceByCompany) =>
        owned.Sum(share => latestPriceByCompany.GetValueOrDefault(share.CompanyId));

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
