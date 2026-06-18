using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Matches open buy and sell orders for a cycle using price-time priority and records the resulting
// share transfers, money movements, and price snapshots. Mutations are tracked on the shared
// DbContext; the caller owns saving and the surrounding transaction.
public sealed class MatchingEngine(AppDbContext dbContext)
{
    public async Task<int> RunAsync(MarketCycle cycle)
    {
        var now = DateTime.UtcNow;
        var participants = await dbContext.Participants.ToDictionaryAsync(participant => participant.Id);

        var openOrders = await dbContext.Orders
            .Where(order => order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
            .Include(order => order.OrderShares)
                .ThenInclude(orderShare => orderShare.Share)
            .ToListAsync();

        var fillCount = 0;

        foreach (var companyOrders in openOrders.GroupBy(order => order.CompanyId))
        {
            var buys = companyOrders
                .Where(order => order.Type == OrderType.Buy && order.RemainingQuantity > 0)
                .OrderByDescending(order => order.LimitPrice)
                .ThenBy(order => order.CreatedAt)
                .ThenBy(order => order.Id)
                .ToList();

            var sells = companyOrders
                .Where(order => order.Type == OrderType.Sell && order.RemainingQuantity > 0)
                .OrderBy(order => order.LimitPrice)
                .ThenBy(order => order.CreatedAt)
                .ThenBy(order => order.Id)
                .ToList();

            var buyIndex = 0;
            var sellIndex = 0;

            while (buyIndex < buys.Count && sellIndex < sells.Count)
            {
                var buy = buys[buyIndex];
                var sell = sells[sellIndex];

                // Best remaining buy cannot meet the cheapest remaining sell, so no further crosses exist.
                if (buy.LimitPrice < sell.LimitPrice)
                {
                    break;
                }

                var matchQuantity = Math.Min(buy.RemainingQuantity, sell.RemainingQuantity);
                var executionPrice = IsOlder(buy, sell) ? buy.LimitPrice : sell.LimitPrice;

                ExecuteFill(buy, sell, matchQuantity, executionPrice, cycle, participants, now);
                fillCount++;

                if (buy.RemainingQuantity == 0)
                {
                    buyIndex++;
                }

                if (sell.RemainingQuantity == 0)
                {
                    sellIndex++;
                }
            }
        }

        return fillCount;
    }

    private static bool IsOlder(Order left, Order right)
    {
        if (left.CreatedAt != right.CreatedAt)
        {
            return left.CreatedAt < right.CreatedAt;
        }

        return left.Id < right.Id;
    }

    private void ExecuteFill(
        Order buy,
        Order sell,
        int quantity,
        decimal executionPrice,
        MarketCycle cycle,
        IReadOnlyDictionary<int, Participant> participants,
        DateTime now)
    {
        var buyer = participants[buy.ParticipantId];
        var seller = participants[sell.ParticipantId];
        var shareLinks = sell.OrderShares.Take(quantity).ToList();

        var shareTransaction = new ShareTransaction
        {
            SellerId = seller.Id,
            BuyerId = buyer.Id,
            CompanyId = buy.CompanyId,
            Quantity = quantity,
            Price = executionPrice,
            TotalCost = executionPrice * quantity,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.ShareTransactions.Add(shareTransaction);

        foreach (var link in shareLinks)
        {
            var share = link.Share!;
            share.OwnerId = buyer.Id;
            share.CurrentPrice = executionPrice;
            share.LastUpdatedAt = now;
            share.LastShareTransaction = shareTransaction;
        }

        // Sold shares leave the offer so the share can be listed again by its new owner.
        dbContext.OrderShares.RemoveRange(shareLinks);
        foreach (var link in shareLinks)
        {
            sell.OrderShares.Remove(link);
        }

        var spent = executionPrice * quantity;
        var reservationForFilled = buy.LimitPrice * quantity;
        var released = reservationForFilled - spent;

        buyer.CurrentBalance -= spent;
        buyer.ReservedBalance -= reservationForFilled;
        buy.ReservedCashAmount -= reservationForFilled;
        seller.CurrentBalance += spent;

        dbContext.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = buyer.Id,
            Type = MoneyTransactionType.Debit,
            Amount = spent,
            RelatedOrderId = buy.Id,
            RelatedShareTransaction = shareTransaction,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });

        // A fill below the buyer's limit frees the unused reservation on the filled shares.
        if (released > 0)
        {
            dbContext.MoneyTransactions.Add(new MoneyTransaction
            {
                ParticipantId = buyer.Id,
                Type = MoneyTransactionType.Release,
                Amount = released,
                RelatedOrderId = buy.Id,
                CreatedInCycleId = cycle.Id,
                CreatedAt = now,
            });
        }

        dbContext.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = seller.Id,
            Type = MoneyTransactionType.Credit,
            Amount = spent,
            RelatedOrderId = sell.Id,
            RelatedShareTransaction = shareTransaction,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });

        dbContext.OrderFills.Add(new OrderFill
        {
            BuyOrderId = buy.Id,
            SellOrderId = sell.Id,
            Quantity = quantity,
            ExecutionPrice = executionPrice,
            CreatedInCycleId = cycle.Id,
            ShareTransaction = shareTransaction,
            CreatedAt = now,
        });

        dbContext.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = buy.CompanyId,
            Price = executionPrice,
            SourceShareTransaction = shareTransaction,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });

        buy.FilledQuantity += quantity;
        buy.Status = buy.RemainingQuantity == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
        buy.UpdatedAt = now;

        sell.FilledQuantity += quantity;
        sell.Status = sell.RemainingQuantity == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
        sell.UpdatedAt = now;
    }
}
