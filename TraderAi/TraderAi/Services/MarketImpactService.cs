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
    private const decimal SentimentImpactClamp = 0.5m;

    // A positive-only event (a science investigation) passes cancelStaleOrders: false so it nudges price up
    // without clearing the order book; news and crises keep the default and reprice around the move.
    public async Task<int> ApplyImpactAsync(
        NewsImpactDirection direction,
        IReadOnlyCollection<int> companyIds,
        decimal percent,
        int cycleId,
        DateTime now,
        bool cancelStaleOrders = true,
        bool applySectorSentiment = false)
    {
        if (companyIds.Count == 0)
        {
            return 0;
        }

        var moved = await ApplySnapshotsAsync(
            direction,
            percent,
            companyIds,
            cycleId,
            now,
            applySectorSentiment);
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
        DateTime now,
        bool applySectorSentiment)
    {
        var latestPriceByCompany = await LatestPriceByCompanyAsync();
        var priceBandByCompany = await dbContext.PriceBandStates
            .Where(state => companyIds.Contains(state.CompanyId))
            .ToDictionaryAsync(state => state.CompanyId);
        var companyStateById = await dbContext.Companies
            .Where(company => companyIds.Contains(company.Id))
            .ToDictionaryAsync(
                company => company.Id,
                company => new { company.IndustryId, company.IssuedSharesCount });

        var sectorStateByIndustryId = new Dictionary<int, (int SentimentValue, decimal SectorBeta)>();
        if (applySectorSentiment)
        {
            var industryIds = companyStateById.Values
                .Select(company => company.IndustryId)
                .Distinct()
                .ToList();
            var sectorStates = await dbContext.Industries
                .Where(industry => industryIds.Contains(industry.Id))
                .Select(industry => new { industry.Id, industry.SentimentValue, industry.SectorBeta })
                .ToListAsync();
            sectorStateByIndustryId = sectorStates.ToDictionary(
                industry => industry.Id,
                industry => (industry.SentimentValue, industry.SectorBeta));
        }

        var moved = 0;
        foreach (var companyId in companyIds)
        {
            if (!latestPriceByCompany.TryGetValue(companyId, out var price) || price <= 0m)
            {
                continue;
            }

            var issuedShares = companyStateById.TryGetValue(companyId, out var companyState)
                ? companyState.IssuedSharesCount
                : 0;
            var beta = 1m;
            var sentimentFactor = 0m;
            if (applySectorSentiment
                && companyState is not null
                && sectorStateByIndustryId.TryGetValue(companyState.IndustryId, out var sectorState))
            {
                beta = sectorState.SectorBeta;
                sentimentFactor = Math.Clamp(
                    sectorState.SentimentValue / 1000m,
                    -SentimentImpactClamp,
                    SentimentImpactClamp);
            }

            // CAPM-style beta lets defensive sectors cushion shocks and cyclical sectors amplify them, while mood further cushions or amplifies the move.
            var effectivePercent = direction == NewsImpactDirection.Increase
                ? percent * beta * (1m + sentimentFactor)
                : percent * beta * (1m - sentimentFactor);
            var factor = direction == NewsImpactDirection.Increase
                ? 1m + (effectivePercent / 100m)
                : 1m - (effectivePercent / 100m);
            var newPrice = Round(price * factor);
            if (priceBandByCompany.TryGetValue(companyId, out var priceBand)
                && priceBand.ReferencePrice > 0m)
            {
                newPrice = Math.Clamp(newPrice, priceBand.LowerBandPrice, priceBand.UpperBandPrice);
            }
            if (newPrice <= 0m)
            {
                continue;
            }

            dbContext.PriceSnapshots.Add(new PriceSnapshot
            {
                CompanyId = companyId,
                Price = newPrice,
                Capitalization = newPrice * issuedShares,
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
        var affectedCompanyIds = await dbContext.PriceBandStates
            .Where(state => state.State != LuldState.Normal)
            .Select(state => state.CompanyId)
            .ToListAsync();

        var orders = await dbContext.Orders
            .Where(order => order.ParticipantId != null
                && order.Type == staleType
                && companyIds.Contains(order.CompanyId)
                && !affectedCompanyIds.Contains(order.CompanyId)
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
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
            // A sell reserves no cash and holds no links; cancelling just frees its listed quantity so the
            // shares can be re-listed at a price that reflects the rise.
            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = now;
        }
    }

    private Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync() =>
        PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
