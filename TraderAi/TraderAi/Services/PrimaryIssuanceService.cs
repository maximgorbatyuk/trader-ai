using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// A persisted issuance event keeps the daily cooldown valid even when issuer orders are cancelled independently.
// Shadow matching ties new supply to demand that the existing book cannot satisfy at price-time priority.
public sealed class PrimaryIssuanceService(
    AppDbContext dbContext,
    IOptions<PrimaryIssuanceOptions> options,
    IOptions<VolatilityHaltOptions> volatilityHaltOptions)
{
    private sealed class ShadowOrder(
        int id,
        int? participantId,
        decimal limitPrice,
        long remainingQuantity,
        DateTime createdAt)
    {
        public int Id { get; } = id;
        public int? ParticipantId { get; } = participantId;
        public decimal LimitPrice { get; } = limitPrice;
        public long RemainingQuantity { get; set; } = remainingQuantity;
        public DateTime CreatedAt { get; } = createdAt;
    }

    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        var tradingDayId = await dbContext.MarketCycles
            .Where(cycle => cycle.Id == currentCycleId)
            .Select(cycle => cycle.TradingDayId)
            .FirstOrDefaultAsync();
        if (tradingDayId <= 0)
        {
            return;
        }

        var tradingDayCycleIds = await dbContext.MarketCycles
            .Where(cycle => cycle.TradingDayId == tradingDayId)
            .Select(cycle => cycle.Id)
            .ToListAsync();
        var issuedToday = (await dbContext.PrimaryIssuanceEvents
                .AsNoTracking()
                .Where(issuance => tradingDayCycleIds.Contains(issuance.CreatedInCycleId))
                .Select(issuance => issuance.CompanyId)
                .ToListAsync())
            .Concat(dbContext.ChangeTracker
                .Entries<PrimaryIssuanceEvent>()
                .Where(entry => entry.State == EntityState.Added
                    && tradingDayCycleIds.Contains(entry.Entity.CreatedInCycleId))
                .Select(entry => entry.Entity.CompanyId))
            .ToHashSet();

        var legacyIssuerOrders = await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.ParticipantId == null
                && order.Type == OrderType.Sell
                && order.IsFloatReplenishment
                && order.LimitPrice > 0m)
            .Join(
                dbContext.MarketCycles,
                order => order.CreatedInCycleId,
                cycle => cycle.Id,
                (order, cycle) => new { order.CompanyId, cycle.TradingDayId })
            .Where(row => row.TradingDayId == tradingDayId)
            .Select(row => row.CompanyId)
            .ToListAsync();
        issuedToday.UnionWith(legacyIssuerOrders);

        var heldByCompany = (await dbContext.Holdings
                .Select(holding => new { holding.CompanyId, holding.Quantity })
                .ToListAsync())
            .GroupBy(holding => holding.CompanyId)
            .ToDictionary(group => group.Key, group => group.Sum(holding => (long)holding.Quantity));
        var latestPriceByCompany = await PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);
        var priceBandByCompany = await dbContext.PriceBandStates.ToDictionaryAsync(state => state.CompanyId);
        var companies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null)
            .OrderBy(company => company.Id)
            .ToListAsync();
        var cancelledReplenishmentCompanyIds = (await dbContext.Orders.AsNoTracking()
                .Where(order => order.ParticipantId == null
                    && order.Type == OrderType.Sell
                    && order.IsFloatReplenishment
                    && order.Status == OrderStatus.Cancelled)
                .Select(order => order.CompanyId)
                .Distinct()
                .ToListAsync())
            .ToHashSet();
        var eligibleBuys = await dbContext.Orders.AsNoTracking()
            .Where(order => order.ParticipantId != null
                && order.Type == OrderType.Buy
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
                && order.Quantity > order.FilledQuantity)
            .Join(
                dbContext.Participants.AsNoTracking()
                    .Where(participant => participant.IsActive
                        && !participant.IsBankrupt
                        && (participant.Type == ParticipantType.Individual
                            || participant.Type == ParticipantType.AIAgent)),
                order => order.ParticipantId,
                participant => participant.Id,
                (order, participant) => new
                {
                    order.Id,
                    order.ParticipantId,
                    order.CompanyId,
                    order.LimitPrice,
                    RemainingQuantity = order.Quantity - order.FilledQuantity,
                    order.CreatedAt,
                })
            .ToListAsync();
        var existingSells = await dbContext.Orders
            .Where(order => order.Type == OrderType.Sell
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
                && order.Quantity > order.FilledQuantity)
            .ToListAsync();

        foreach (var order in existingSells.Where(order => order.ParticipantId == null && order.IsFloatReplenishment))
        {
            if (!latestPriceByCompany.TryGetValue(order.CompanyId, out var latestPrice)
                || latestPrice <= 0m)
            {
                continue;
            }

            var priceBand = priceBandByCompany.GetValueOrDefault(order.CompanyId);
            if (priceBand is not null && priceBand.State != LuldState.Normal)
            {
                continue;
            }

            var bounds = ResolveBounds(priceBand, latestPrice, volatilityHaltOptions.Value);
            if (bounds is null
                || !bounds.IsWithinActiveBand(latestPrice)
                || bounds.IsWithinActiveBand(order.LimitPrice))
            {
                continue;
            }

            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = now;
            cancelledReplenishmentCompanyIds.Add(order.CompanyId);
        }

        foreach (var company in companies)
        {
            if (company.IssuedSharesCount <= 0
                || issuedToday.Contains(company.Id)
                || !latestPriceByCompany.TryGetValue(company.Id, out var price)
                || price <= 0m)
            {
                continue;
            }

            var priceBand = priceBandByCompany.GetValueOrDefault(company.Id);
            if (priceBand is not null && priceBand.State != LuldState.Normal)
            {
                continue;
            }

            var bounds = ResolveBounds(
                priceBand,
                price,
                volatilityHaltOptions.Value);
            if (bounds is null || !bounds.IsWithinActiveBand(price))
            {
                continue;
            }

            var issuerFloat = (long)company.IssuedSharesCount - heldByCompany.GetValueOrDefault(company.Id);
            var scarcityThreshold = company.IssuedSharesCount * settings.FloatScarcityThresholdPercent / 100m;

            var buys = eligibleBuys
                .Where(order => order.CompanyId == company.Id
                    && order.LimitPrice >= price
                    && bounds.IsWithinActiveBand(order.LimitPrice))
                .OrderByDescending(order => order.LimitPrice)
                .ThenBy(order => order.CreatedAt)
                .ThenBy(order => order.Id)
                .Select(order => new ShadowOrder(
                    order.Id,
                    order.ParticipantId,
                    order.LimitPrice,
                    order.RemainingQuantity,
                    order.CreatedAt))
                .ToList();
            if (buys.Count == 0)
            {
                continue;
            }

            var sells = existingSells
                .Where(order => order.CompanyId == company.Id
                    && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
                    && order.Quantity > order.FilledQuantity
                    && bounds.IsWithinActiveBand(order.LimitPrice))
                .OrderBy(order => order.LimitPrice)
                .ThenBy(order => order.CreatedAt)
                .ThenBy(order => order.Id)
                .Select(order => new ShadowOrder(
                    order.Id,
                    order.ParticipantId,
                    order.LimitPrice,
                    order.Quantity - order.FilledQuantity,
                    order.CreatedAt))
                .ToList();
            var unmetDemand = CalculateUnmetDemand(buys, sells);
            if (unmetDemand <= 0)
            {
                continue;
            }

            var listedIssuerFloat = existingSells
                .Where(order => order.CompanyId == company.Id
                    && order.ParticipantId == null
                    && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
                    && order.Quantity > order.FilledQuantity)
                .Aggregate(0L, (listed, order) => AddWithoutOverflow(
                    listed,
                    order.Quantity - order.FilledQuantity));
            // A cancelled replenishment proves that unlisted issuer float came from a superseded offer; without
            // that history, a missing issuer order must not silently turn unrelated free shares into a new listing.
            var relistableFloat = cancelledReplenishmentCompanyIds.Contains(company.Id)
                ? Math.Max(0L, issuerFloat - listedIssuerFloat)
                : 0L;
            var relistedQuantity = Math.Min(
                unmetDemand,
                Math.Min(relistableFloat, int.MaxValue));
            var remainingDemand = unmetDemand - relistedQuantity;
            var remainingOrderCapacity = int.MaxValue - relistedQuantity;
            var newlyIssuedQuantity = 0L;
            if (remainingDemand > 0
                && remainingOrderCapacity > 0
                && issuerFloat < scarcityThreshold
                && TryCalculateDailyCap(
                    company.IssuedSharesCount,
                    settings.MaximumDailyIssuancePercent,
                    out var dailyCap))
            {
                var availableIssuedShareCapacity = int.MaxValue - company.IssuedSharesCount;
                newlyIssuedQuantity = Math.Min(
                    Math.Min(remainingDemand, dailyCap),
                    Math.Min(availableIssuedShareCapacity, remainingOrderCapacity));
            }

            var quantity = (int)(relistedQuantity + newlyIssuedQuantity);
            if (quantity <= 0)
            {
                continue;
            }

            if (newlyIssuedQuantity > 0)
            {
                var issuedSharesBefore = company.IssuedSharesCount;
                company.IssuedSharesCount += (int)newlyIssuedQuantity;
                company.UpdatedAt = now;
                dbContext.PrimaryIssuanceEvents.Add(new PrimaryIssuanceEvent
                {
                    CompanyId = company.Id,
                    CreatedInCycleId = currentCycleId,
                    IssuedSharesBefore = issuedSharesBefore,
                    NewlyIssuedShares = (int)newlyIssuedQuantity,
                    IssuedSharesAfter = company.IssuedSharesCount,
                    CreatedAt = now,
                });
            }
            dbContext.Orders.Add(new Order
            {
                ParticipantId = null,
                CompanyId = company.Id,
                Type = OrderType.Sell,
                Status = OrderStatus.Open,
                Quantity = quantity,
                LimitPrice = price,
                IsFloatReplenishment = true,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    private static OrderPriceBounds? ResolveBounds(
        PriceBandState? band,
        decimal latestPrice,
        VolatilityHaltOptions settings)
    {
        var bounds = OrderPriceBounds.Resolve(
            band,
            latestPrice,
            settings.LowerBandPercent,
            settings.UpperBandPercent,
            settings.AllowedOrderLowerPercent,
            settings.AllowedOrderUpperPercent);
        return bounds is { ReferencePrice: > 0m, ActiveLowerPrice: > 0m }
            && bounds.ActiveUpperPrice >= bounds.ActiveLowerPrice
                ? bounds
                : null;
    }

    private static long CalculateUnmetDemand(
        IReadOnlyList<ShadowOrder> buys,
        IReadOnlyList<ShadowOrder> sells)
    {
        var buyIndex = 0;
        var sellIndex = 0;
        while (buyIndex < buys.Count && sellIndex < sells.Count)
        {
            var buy = buys[buyIndex];
            var sell = sells[sellIndex];
            if (buy.LimitPrice < sell.LimitPrice)
            {
                break;
            }

            if (buy.ParticipantId is int buyerId && buyerId == sell.ParticipantId)
            {
                var buyIsNewer = buy.CreatedAt > sell.CreatedAt
                    || (buy.CreatedAt == sell.CreatedAt && buy.Id > sell.Id);
                if (buyIsNewer)
                {
                    buy.RemainingQuantity = 0;
                    buyIndex++;
                }
                else
                {
                    sell.RemainingQuantity = 0;
                    sellIndex++;
                }

                continue;
            }

            var matched = Math.Min(buy.RemainingQuantity, sell.RemainingQuantity);
            buy.RemainingQuantity -= matched;
            sell.RemainingQuantity -= matched;
            if (buy.RemainingQuantity == 0)
            {
                buyIndex++;
            }
            if (sell.RemainingQuantity == 0)
            {
                sellIndex++;
            }
        }

        return buys.Aggregate(
            0L,
            (unmetDemand, buy) => AddWithoutOverflow(unmetDemand, buy.RemainingQuantity));
    }

    private static bool TryCalculateDailyCap(int issuedShares, decimal percentage, out long cap)
    {
        cap = 0;
        if (issuedShares <= 0 || percentage <= 0m)
        {
            return false;
        }

        try
        {
            var rounded = decimal.Ceiling(issuedShares * percentage / 100m);
            if (rounded <= 0m)
            {
                return false;
            }

            cap = rounded >= long.MaxValue ? long.MaxValue : decimal.ToInt64(rounded);
            return cap > 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static long AddWithoutOverflow(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
