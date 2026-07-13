using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed class VolatilityHaltService(
    AppDbContext dbContext,
    IOptions<VolatilityHaltOptions> options,
    TradingClockService tradingClock)
{
    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        var referenceCycles = tradingClock.TradingCyclesForSeconds(settings.ReferenceWindowSeconds);
        var limitCycles = tradingClock.TradingCyclesForSeconds(settings.LimitStateDurationSeconds);
        var pauseCycles = tradingClock.TradingCyclesForSeconds(settings.TradingPauseDurationSeconds);
        var companies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null)
            .OrderBy(company => company.Id)
            .ToListAsync();
        var states = dbContext.PriceBandStates.Local.ToDictionary(state => state.CompanyId);
        foreach (var persisted in await dbContext.PriceBandStates.ToListAsync())
        {
            states.TryAdd(persisted.CompanyId, persisted);
        }
        var referenceByCompany = await ReferencePricesAsync(currentCycleNumber, referenceCycles);
        var fallbackByCompany = await PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);
        var openOrders = await dbContext.Orders
            .Where(order => order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
            .ToListAsync();
        var ordersByCompany = openOrders.GroupBy(order => order.CompanyId).ToDictionary(group => group.Key, group => group.ToList());

        foreach (var company in companies)
        {
            if (!states.TryGetValue(company.Id, out var state))
            {
                state = new PriceBandState { CompanyId = company.Id, State = LuldState.Normal };
                states[company.Id] = state;
                dbContext.PriceBandStates.Add(state);
            }

            var reference = referenceByCompany.GetValueOrDefault(company.Id, fallbackByCompany.GetValueOrDefault(company.Id));
            if (reference > 0m && (state.State is LuldState.Normal or LuldState.LimitState || state.ReferencePrice <= 0m))
            {
                state.ReferencePrice = Round(reference);
                state.LowerBandPrice = Round(reference * (1m - settings.LowerBandPercent / 100m));
                state.UpperBandPrice = Round(reference * (1m + settings.UpperBandPercent / 100m));
                state.UpdatedInCycleId = currentCycleId;
            }

            if (state.State == LuldState.TradingPause)
            {
                if (currentCycleNumber >= state.PauseUntilCycleNumber)
                {
                    state.State = LuldState.Reopening;
                    state.UpdatedInCycleId = currentCycleId;
                    company.UpdatedAt = now;
                }
                continue;
            }

            if (state.State == LuldState.Reopening)
            {
                continue;
            }

            if (reference <= 0m)
            {
                continue;
            }

            var direction = LimitPressure(ordersByCompany.GetValueOrDefault(company.Id) ?? [], state);
            if (state.State == LuldState.Normal)
            {
                if (direction is not null)
                {
                    state.State = LuldState.LimitState;
                    state.LimitDirection = direction;
                    state.LimitStateStartedCycleNumber = currentCycleNumber;
                    company.UpdatedAt = now;
                }
                continue;
            }

            if (direction is null || direction != state.LimitDirection)
            {
                ResetToNormal(state, currentCycleId);
                company.UpdatedAt = now;
                continue;
            }

            if (currentCycleNumber - state.LimitStateStartedCycleNumber!.Value + 1 >= limitCycles)
            {
                state.State = LuldState.TradingPause;
                state.PauseUntilCycleNumber = currentCycleNumber + pauseCycles;
                state.UpdatedInCycleId = currentCycleId;
                company.UpdatedAt = now;
                dbContext.NewsPosts.Add(new NewsPost
                {
                    Title = $"Trading in {company.Name} is paused by LULD",
                    Content = $"{company.Name} remained at its {state.LimitDirection!.Value.ToString().ToLowerInvariant()} price band for {settings.LimitStateDurationSeconds} simulated seconds. Orders remain open for a deterministic reopening auction.",
                    PublishedInCycleId = currentCycleId,
                    PublishedAt = now,
                    Scope = NewsImpactScope.None,
                    TargetCompanyId = company.Id,
                });
            }
        }
    }

    private async Task<Dictionary<int, decimal>> ReferencePricesAsync(int currentCycleNumber, int windowCycles)
    {
        var firstCycle = Math.Max(1, currentCycleNumber - windowCycles + 1);
        return await dbContext.ShareTransactions
            .Join(
                dbContext.MarketCycles,
                transaction => transaction.CreatedInCycleId,
                cycle => cycle.Id,
                (transaction, cycle) => new { transaction.CompanyId, transaction.Price, cycle.CycleNumber })
            .Where(row => row.CycleNumber >= firstCycle && row.CycleNumber <= currentCycleNumber)
            .GroupBy(row => row.CompanyId)
            .ToDictionaryAsync(group => group.Key, group => group.Average(row => row.Price));
    }

    private static PriceLimitDirection? LimitPressure(IReadOnlyCollection<Order> orders, PriceBandState state)
    {
        var bestBuy = orders
            .Where(order => order.Type == OrderType.Buy && order.RemainingQuantity > 0)
            .OrderByDescending(order => order.LimitPrice)
            .FirstOrDefault();
        var bestSell = orders
            .Where(order => order.Type == OrderType.Sell && order.RemainingQuantity > 0)
            .OrderBy(order => order.LimitPrice)
            .FirstOrDefault();
        if (bestBuy is null || bestSell is null || bestBuy.LimitPrice < bestSell.LimitPrice)
        {
            return null;
        }

        var effectiveBuy = Math.Min(bestBuy.LimitPrice, state.UpperBandPrice);
        var effectiveSell = Math.Max(bestSell.LimitPrice, state.LowerBandPrice);
        var price = Round((effectiveBuy + effectiveSell) / 2m);
        if (price >= state.UpperBandPrice)
        {
            return PriceLimitDirection.Upper;
        }
        return price <= state.LowerBandPrice ? PriceLimitDirection.Lower : null;
    }

    public static void ResetToNormal(PriceBandState state, int currentCycleId)
    {
        state.State = LuldState.Normal;
        state.LimitDirection = null;
        state.LimitStateStartedCycleNumber = null;
        state.PauseUntilCycleNumber = null;
        state.UpdatedInCycleId = currentCycleId;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
