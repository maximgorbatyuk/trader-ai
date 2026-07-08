using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Matches open buy and sell orders for a cycle and records the resulting share transfers, money
// movements, and price snapshots. Orders pair by price-time priority, but each cross executes at the
// midpoint of the two limits. Mutations are tracked on the shared DbContext; the caller owns saving and
// the surrounding transaction.
public sealed class MatchingEngine(AppDbContext dbContext)
{
    public async Task<int> RunAsync(MarketCycle cycle)
    {
        var now = DateTime.UtcNow;
        var participants = await dbContext.Participants.ToDictionaryAsync(participant => participant.Id);

        // Positions of everyone who might trade this cycle; buyers acquiring their first shares of a
        // company get a fresh row added on the fly.
        var holdings = await dbContext.Holdings.ToDictionaryAsync(holding => (holding.ParticipantId, holding.CompanyId));

        // Issued-share counts value each fill's price snapshot at total capitalisation; splits do not run
        // during matching, so a single up-front read stays correct for the whole pass.
        var sharesByCompany = await dbContext.Companies.ToDictionaryAsync(company => company.Id, company => company.IssuedSharesCount);

        // A company under a volatility halt is frozen this cycle: its resting orders neither cross nor move.
        var haltedCompanyIds = (await dbContext.Companies
                .Where(company => company.TradingHaltedUntilCycleNumber != null
                    && company.TradingHaltedUntilCycleNumber >= cycle.CycleNumber)
                .Select(company => company.Id)
                .ToListAsync())
            .ToHashSet();

        var openOrders = await dbContext.Orders
            .Where(order => order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
            .ToListAsync();

        var fillCount = 0;

        foreach (var companyOrders in openOrders.GroupBy(order => order.CompanyId))
        {
            if (haltedCompanyIds.Contains(companyOrders.Key))
            {
                continue;
            }

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

                // Crossing guarantees the midpoint sits at or below the buyer's limit and at or above the
                // seller's, so the buyer's unused reservation is still refunded below.
                var executionPrice = Round((buy.LimitPrice + sell.LimitPrice) / 2m);

                ExecuteFill(buy, sell, matchQuantity, executionPrice, cycle, participants, holdings, sharesByCompany, now);
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

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private void ExecuteFill(
        Order buy,
        Order sell,
        int quantity,
        decimal executionPrice,
        MarketCycle cycle,
        IReadOnlyDictionary<int, Participant> participants,
        Dictionary<(int ParticipantId, int CompanyId), Holding> holdings,
        IReadOnlyDictionary<int, int> sharesByCompany,
        DateTime now)
    {
        var buyer = participants[buy.ParticipantId!.Value];
        var seller = sell.ParticipantId is int sellerId ? participants[sellerId] : null;
        var companyId = buy.CompanyId;

        var shareTransaction = new ShareTransaction
        {
            SellerId = seller?.Id,
            BuyerId = buyer.Id,
            CompanyId = companyId,
            Quantity = quantity,
            Price = executionPrice,
            TotalCost = executionPrice * quantity,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.ShareTransactions.Add(shareTransaction);

        // A company-originated offer has no seller position; only a participant seller's holding shrinks.
        if (seller is not null)
        {
            ReduceHolding(holdings, seller.Id, companyId, quantity);
        }

        AddToHolding(holdings, buyer.Id, companyId, quantity, executionPrice);

        var spent = executionPrice * quantity;
        var reservationForFilled = buy.LimitPrice * quantity;
        var released = reservationForFilled - spent;

        buyer.CurrentBalance -= spent;
        buyer.ReservedBalance -= reservationForFilled;
        buy.ReservedCashAmount -= reservationForFilled;

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

        // A company-originated offer has no participant seller, so the proceeds leave the participant
        // economy (the issuing company is not modelled as holding cash) and no credit is recorded.
        if (seller is not null)
        {
            seller.CurrentBalance += spent;

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
        }

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
            CompanyId = companyId,
            Price = executionPrice,
            Capitalization = executionPrice * sharesByCompany.GetValueOrDefault(companyId),
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

    private void AddToHolding(
        Dictionary<(int ParticipantId, int CompanyId), Holding> holdings,
        int participantId,
        int companyId,
        int quantity,
        decimal price)
    {
        if (holdings.TryGetValue((participantId, companyId), out var holding))
        {
            var blended = ((holding.Quantity * holding.AverageCost) + (quantity * price)) / (holding.Quantity + quantity);
            holding.AverageCost = Round(blended);
            holding.Quantity += quantity;
            return;
        }

        var created = new Holding
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Quantity = quantity,
            AverageCost = price,
        };
        dbContext.Holdings.Add(created);
        holdings[(participantId, companyId)] = created;
    }

    private static void ReduceHolding(
        Dictionary<(int ParticipantId, int CompanyId), Holding> holdings,
        int participantId,
        int companyId,
        int quantity)
    {
        // Leave a zero-quantity row rather than deleting it: every holdings read filters on Quantity > 0, and
        // keeping the row avoids a delete-then-insert on the same (participant, company) unique key when a
        // seller sells out and rebuys the same company within one matching run.
        holdings[(participantId, companyId)].Quantity -= quantity;
    }
}
