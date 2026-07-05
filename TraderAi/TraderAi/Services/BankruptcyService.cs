using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Drives trader bankruptcies once per cycle, before the cycle's matching runs. A trader whose share holdings
// stay valued above the wealth line sees its bankruptcy chance ramp each cycle; when it fires the trader's
// cash is wiped and most of its holdings are dumped onto the order book at a discount. A trader still working
// through that sell-down has its unsold orders re-listed a step cheaper each cycle until the target is met.
// Called from inside order maintenance, which already holds the lock and owns the save, so this only stages
// changes on the shared context.
public sealed class BankruptcyService(
    AppDbContext dbContext,
    IOptions<BankruptcyOptions> options,
    Random random)
{
    // Only the trader's share holdings, valued at the latest price, are weighed against this line; cash is
    // ignored. At or above it the trader is at risk.
    private const decimal ShareWorthThreshold = 2_000_000_000m;

    // No trader can go bankrupt during the market's opening stretch, and the wealth ramp does not start
    // accumulating until it passes, so an early simulation runs clean and the ramp stays gentle afterwards.
    private const int QuietCycles = 500;

    // The chance ramps up each consecutive cycle above the line and holds at the cap, kept gentle so a rich
    // trader lingers at risk for a long stretch rather than collapsing within a few cycles.
    private const double StepPerCycle = 0.002;
    private const double MaxProbability = 0.10;

    // Debt carries its own bankruptcy risk, stacked on top of the wealth ramp: each percentage point of debt
    // against total worth adds this much chance, clamped at the 20% borrow ceiling so it tops out near 5%.
    private const double DebtBankruptcyChancePerPercent = 0.0025;
    private const decimal MaxDebtPercent = 20m;

    // While a crisis is active every trader's bankruptcy chance is doubled (clamped to 1).
    private const double CrisisBankruptcyMultiplier = 2.0;

    // A bankrupt trader must sell down this fraction of the shares it held when bankruptcy struck.
    private const decimal SellDownFraction = 0.65m;

    // The first forced-sale lists at the base discount off the current price; every unsold re-listing
    // deepens it by a step, floored so the asking price never reaches zero.
    private const decimal BaseDiscount = 0.20m;
    private const decimal DiscountStepSize = 0.05m;
    private const decimal MaxDiscount = 0.95m;

    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now, Crisis? activeCrisis = null)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var latestPriceByCompany = await LatestPriceByCompanyAsync();

        var ownedByParticipant = (await dbContext.Holdings
                .Where(holding => holding.Quantity > 0)
                .Select(holding => new { holding.ParticipantId, holding.CompanyId, holding.Quantity })
                .ToListAsync())
            .GroupBy(holding => holding.ParticipantId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(holding => holding.CompanyId, holding => holding.Quantity));

        var openOrders = await dbContext.Orders
            .Where(order => order.ParticipantId != null
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .ToListAsync();
        var openOrdersByParticipant = openOrders
            .GroupBy(order => order.ParticipantId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        // Uncommitted quantity per (participant, company): owned shares minus what is already listed for sale.
        // Listing a forced sale draws it down and cancelling one gives it back — the quantity analogue of the
        // old committed-share set, so a share is never offered by two orders at once.
        var available = new Dictionary<(int ParticipantId, int CompanyId), int>();
        foreach (var (participantId, byCompany) in ownedByParticipant)
        {
            foreach (var (companyId, quantity) in byCompany)
            {
                available[(participantId, companyId)] = quantity;
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

        // Stable id order keeps the per-trader random draws reproducible for a scripted Random in tests.
        var participants = await dbContext.Participants.OrderBy(participant => participant.Id).ToListAsync();

        foreach (var participant in participants)
        {
            var owned = ownedByParticipant.GetValueOrDefault(participant.Id) ?? new Dictionary<int, int>();
            var openForParticipant = openOrdersByParticipant.GetValueOrDefault(participant.Id) ?? [];

            if (participant.IsBankrupt)
            {
                ContinueSellDown(participant, owned, openForParticipant, latestPriceByCompany, available, currentCycleId, now);
                continue;
            }

            // Opening protection: no new bankruptcy, and no ramp, until the market clears its quiet window.
            if (currentCycleNumber <= QuietCycles)
            {
                continue;
            }

            MaybeTrigger(participant, owned, openForParticipant, latestPriceByCompany, available, currentCycleId, currentCycleNumber, now, activeCrisis);
        }
    }

    private void MaybeTrigger(
        Participant participant,
        IReadOnlyDictionary<int, int> owned,
        List<Order> openOrders,
        IReadOnlyDictionary<int, decimal> latestPriceByCompany,
        Dictionary<(int ParticipantId, int CompanyId), int> available,
        int currentCycleId,
        int currentCycleNumber,
        DateTime now,
        Crisis? activeCrisis)
    {
        // Only working traders carry bankruptcy risk; issuer-company participants are left out.
        if (!participant.IsActive
            || (participant.Type != ParticipantType.Individual && participant.Type != ParticipantType.AIAgent))
        {
            return;
        }

        var shareWorth = ShareWorth(owned, latestPriceByCompany);

        double wealthyProbability;
        if (shareWorth >= ShareWorthThreshold)
        {
            participant.WealthyCycles++;
            wealthyProbability = Math.Min(participant.WealthyCycles * StepPerCycle, MaxProbability);
        }
        else
        {
            participant.WealthyCycles = 0;
            wealthyProbability = 0.0;
        }

        var probability = wealthyProbability + DebtBankruptcyProbability(participant, shareWorth);
        if (activeCrisis is not null)
        {
            probability = Math.Min(1.0, probability * CrisisBankruptcyMultiplier);
        }

        if (probability <= 0.0 || random.NextDouble() >= probability)
        {
            return;
        }

        // A debtor holds negative cash, so nothing is lost on the wipe; clamping keeps the ledger and story
        // non-negative while zeroing the balance below discharges the debt.
        var cashLost = Math.Max(0m, participant.CurrentBalance);

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
                CancelSell(order, available, now);
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
        participant.BankruptcyOwnedAtStart = TotalOwned(owned);
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

        // A collapse that struck while a crisis is active joins that crisis's timeline.
        if (activeCrisis is not null)
        {
            dbContext.CrisisEvents.Add(new CrisisEvent
            {
                CrisisId = activeCrisis.Id,
                Type = CrisisEventType.Bankruptcy,
                Description = $"{participant.Name} went bankrupt",
                CreatedInCycleId = currentCycleId,
                CreatedInCycleNumber = currentCycleNumber,
                CreatedAt = now,
            });
        }

        ListForcedSells(participant, SellDownTarget(participant), owned, latestPriceByCompany, available, currentCycleId, now);
    }

    private void ContinueSellDown(
        Participant participant,
        IReadOnlyDictionary<int, int> owned,
        List<Order> openOrders,
        IReadOnlyDictionary<int, decimal> latestPriceByCompany,
        Dictionary<(int ParticipantId, int CompanyId), int> available,
        int currentCycleId,
        DateTime now)
    {
        var sold = participant.BankruptcyOwnedAtStart - TotalOwned(owned);
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
                CancelSell(order, available, now);
            }

            participant.BankruptcyDiscountStep++;
        }

        ListForcedSells(participant, remaining, owned, latestPriceByCompany, available, currentCycleId, now);
    }

    private void ListForcedSells(
        Participant participant,
        int sharesToList,
        IReadOnlyDictionary<int, int> owned,
        IReadOnlyDictionary<int, decimal> latestPriceByCompany,
        Dictionary<(int ParticipantId, int CompanyId), int> available,
        int currentCycleId,
        DateTime now)
    {
        if (sharesToList <= 0)
        {
            return;
        }

        var discount = Math.Min(BaseDiscount + (DiscountStepSize * participant.BankruptcyDiscountStep), MaxDiscount);
        var remaining = sharesToList;

        foreach (var companyId in owned.Keys.OrderBy(id => id))
        {
            if (remaining <= 0)
            {
                break;
            }

            if (!latestPriceByCompany.TryGetValue(companyId, out var price) || price <= 0m)
            {
                continue;
            }

            var availableQuantity = available.GetValueOrDefault((participant.Id, companyId));
            if (availableQuantity <= 0)
            {
                continue;
            }

            var sellPrice = Round(price * (1m - discount));
            if (sellPrice <= 0m)
            {
                continue;
            }

            var quantity = Math.Min(remaining, availableQuantity);

            dbContext.Orders.Add(new Order
            {
                ParticipantId = participant.Id,
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

            available[(participant.Id, companyId)] = availableQuantity - quantity;
            remaining -= quantity;
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

    private static void CancelSell(
        Order order,
        Dictionary<(int ParticipantId, int CompanyId), int> available,
        DateTime now)
    {
        // The unsold quantity returns to the seller's available pool so it can be re-listed.
        var key = (order.ParticipantId!.Value, order.CompanyId);
        available[key] = available.GetValueOrDefault(key) + order.RemainingQuantity;
        order.Status = OrderStatus.Cancelled;
        order.UpdatedAt = now;
    }

    private static int SellDownTarget(Participant participant) =>
        (int)Math.Round(participant.BankruptcyOwnedAtStart * SellDownFraction, MidpointRounding.AwayFromZero);

    // Aggregate holdings across all companies can exceed the 32-bit field for a dominant holder; sum in
    // long and clamp so the checked int accumulator cannot overflow.
    private static int TotalOwned(IReadOnlyDictionary<int, int> owned) =>
        (int)Math.Clamp(owned.Values.Sum(quantity => (long)quantity), 0L, int.MaxValue);

    private static decimal ShareWorth(IReadOnlyDictionary<int, int> owned, IReadOnlyDictionary<int, decimal> latestPriceByCompany) =>
        owned.Sum(holding => holding.Value * latestPriceByCompany.GetValueOrDefault(holding.Key));

    // Bankruptcy chance from a negative balance: debt as a percent of total worth (cash plus holdings), clamped
    // at the borrow ceiling, times the per-percent step. A trader whose debt outruns its holdings caps out.
    private static double DebtBankruptcyProbability(Participant participant, decimal shareWorth)
    {
        if (participant.CurrentBalance >= 0m)
        {
            return 0.0;
        }

        var debt = -participant.CurrentBalance;
        var worth = participant.CurrentBalance + shareWorth;
        var debtPercent = worth > 0m
            ? Math.Min(debt / worth * 100m, MaxDebtPercent)
            : MaxDebtPercent;

        return (double)debtPercent * DebtBankruptcyChancePerPercent;
    }

    private Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync() =>
        PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
