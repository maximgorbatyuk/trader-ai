using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Applies a price shock from news or a crisis: it moves each company's price by inserting a new
// PriceSnapshot (the same mechanic the matching engine uses) and then cancels the resting orders that were
// priced against the old level. A drop cancels the standing buy orders for those companies; a rise cancels
// the standing sell orders. Only participant orders are touched, never the company float.
//
// Like the automated news path, this only stages changes on the shared context; the caller owns the save so
// it can run inside the cycle advance's transaction.
public sealed class MarketImpactService(AppDbContext dbContext)
{
    // A positive-only event (a science investigation) passes cancelStaleOrders: false so it nudges price up
    // without clearing the order book; news and crises keep the default and reprice around the move.
    public async Task<int> ApplyImpactAsync(
        NewsImpactDirection direction,
        IReadOnlyCollection<int> companyIds,
        decimal percent,
        int cycleId,
        DateTime now,
        bool cancelStaleOrders = true)
    {
        if (companyIds.Count == 0)
        {
            return 0;
        }

        var moved = await ApplySnapshotsAsync(direction, percent, companyIds, cycleId, now);
        if (cancelStaleOrders)
        {
            await CancelStaleOrdersAsync(direction, companyIds, cycleId, now);
        }

        return moved;
    }

    private async Task<int> ApplySnapshotsAsync(
        NewsImpactDirection direction,
        decimal percent,
        IReadOnlyCollection<int> companyIds,
        int cycleId,
        DateTime now)
    {
        var latestPriceByCompany = await LatestPriceByCompanyAsync();
        var factor = direction == NewsImpactDirection.Increase
            ? 1m + (percent / 100m)
            : 1m - (percent / 100m);

        var moved = 0;
        foreach (var companyId in companyIds)
        {
            if (!latestPriceByCompany.TryGetValue(companyId, out var price) || price <= 0m)
            {
                continue;
            }

            var newPrice = Round(price * factor);
            if (newPrice <= 0m)
            {
                continue;
            }

            dbContext.PriceSnapshots.Add(new PriceSnapshot
            {
                CompanyId = companyId,
                Price = newPrice,
                CreatedInCycleId = cycleId,
                CreatedAt = now,
            });
            moved++;
        }

        return moved;
    }

    // A drop makes standing bids stale, a rise makes standing asks stale; the matching side is cancelled so
    // the order book reprices around the shock instead of filling at the pre-shock level.
    private async Task CancelStaleOrdersAsync(
        NewsImpactDirection direction,
        IReadOnlyCollection<int> companyIds,
        int cycleId,
        DateTime now)
    {
        var staleType = direction == NewsImpactDirection.Decrease ? OrderType.Buy : OrderType.Sell;

        var orders = await dbContext.Orders
            .Where(order => order.ParticipantId != null
                && order.Type == staleType
                && companyIds.Contains(order.CompanyId)
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .Include(order => order.OrderShares)
            .ToListAsync();

        if (orders.Count == 0)
        {
            return;
        }

        var participantIds = orders.Select(order => order.ParticipantId!.Value).Distinct().ToList();
        var participantsById = await dbContext.Participants
            .Where(participant => participantIds.Contains(participant.Id))
            .ToDictionaryAsync(participant => participant.Id);

        foreach (var order in orders)
        {
            // The player is a human; crisis and news shocks never cancel their orders.
            if (participantsById.TryGetValue(order.ParticipantId!.Value, out var owner)
                && owner.Type == ParticipantType.Player)
            {
                continue;
            }

            if (order.Type == OrderType.Buy)
            {
                var release = order.ReservedCashAmount;
                if (release > 0m && participantsById.TryGetValue(order.ParticipantId!.Value, out var participant))
                {
                    participant.ReservedBalance -= release;
                    order.ReservedCashAmount = 0m;
                    dbContext.MoneyTransactions.Add(new MoneyTransaction
                    {
                        ParticipantId = participant.Id,
                        Type = MoneyTransactionType.Release,
                        Amount = release,
                        RelatedOrderId = order.Id,
                        CreatedInCycleId = cycleId,
                        CreatedAt = now,
                    });
                }
            }
            else
            {
                // Freeing the share links lets the shares be listed again at a price that reflects the rise.
                dbContext.OrderShares.RemoveRange(order.OrderShares);
                order.OrderShares.Clear();
            }

            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = now;
        }
    }

    private async Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync()
    {
        var snapshots = await dbContext.PriceSnapshots.ToListAsync();
        return snapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.Id).First().Price);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
