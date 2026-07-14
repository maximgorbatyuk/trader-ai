using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// A priced issuer order keeps new supply inside ordinary matching and settlement, while the day's existing
// priced issuer offer provides a cooldown without separate persistence. Ascending evaluation and gated draws
// keep seeded simulations reproducible.
public sealed class PrimaryIssuanceService(
    AppDbContext dbContext,
    IOptions<PrimaryIssuanceOptions> options,
    IOptions<RandomChanceRatesOptions> chanceRates,
    Random random)
{
    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        if (!options.Value.Enabled)
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

        var issuedToday = (await dbContext.Orders
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
                .ToListAsync())
            .ToHashSet();

        var heldByCompany = await dbContext.Holdings
            .GroupBy(holding => holding.CompanyId)
            .ToDictionaryAsync(group => group.Key, group => group.Sum(holding => holding.Quantity));
        var latestPriceByCompany = await PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);
        var companies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null)
            .OrderBy(company => company.Id)
            .ToListAsync();

        foreach (var company in companies)
        {
            if (company.IssuedSharesCount <= 0
                || issuedToday.Contains(company.Id)
                || !latestPriceByCompany.TryGetValue(company.Id, out var price)
                || price <= 0m)
            {
                continue;
            }

            var issuerFloat = company.IssuedSharesCount - heldByCompany.GetValueOrDefault(company.Id);
            var scarcityThreshold = company.IssuedSharesCount * options.Value.FloatScarcityThresholdPercent / 100m;
            if (issuerFloat >= scarcityThreshold)
            {
                continue;
            }

            var magnitudes = chanceRates.Value.RandomMagnitudeBands;
            var rate = magnitudes.PrimaryIssuanceRateMin
                + random.NextDouble() * (magnitudes.PrimaryIssuanceRateMax - magnitudes.PrimaryIssuanceRateMin);
            var quantity = Math.Max(1, (int)Math.Round(company.IssuedSharesCount * rate, MidpointRounding.AwayFromZero));

            company.IssuedSharesCount += quantity;
            company.UpdatedAt = now;
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
}
