using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// A cycle-based port of the Limit Up-Limit Down circuit breaker: a company whose price moved past the up or
// down band over the last ObservationWindowCycles cycles is frozen for HaltDurationCycles, its whole order
// book cancelled so it re-forms fresh on re-open. While frozen the matching engine skips it and no new order
// may be placed for it. The freeze is symmetric, so a fast crash halts just like a fast surge.
//
// Runs first in the pre-match window so it reads only prior-cycle closes — before this cycle's splits,
// emissions, lifecycle cut, or auditor downgrade add any snapshot — which keeps a same-cycle deliberate price
// cut from tripping the down-halt. Like a stock split it is the deterministic kind of per-cycle service: no
// Random, nothing drawn. Stages changes; the caller owns the save.
public sealed class VolatilityHaltService(
    AppDbContext dbContext,
    IOptions<VolatilityHaltOptions> options)
{
    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        var opts = options.Value;
        if (!opts.Enabled || opts.ObservationWindowCycles <= 0)
        {
            return;
        }

        var window = opts.ObservationWindowCycles;
        var companies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null)
            .OrderBy(company => company.Id)
            .ToListAsync();

        var closesByCompany = await PerCycleClosesByCompanyAsync();

        foreach (var company in companies)
        {
            // A company already frozen is left alone until its halt lapses — it must not re-detect or
            // re-cancel a book that is already empty.
            if (company.TradingHaltedUntilCycleNumber is int until && until >= currentCycleNumber)
            {
                continue;
            }

            if (!closesByCompany.TryGetValue(company.Id, out var closes) || closes.Count == 0)
            {
                continue;
            }

            var recent = closes[^1];

            // Only an actively-trading company can breach: skip one whose latest close predates the window,
            // so a stock that simply has not traded in a while is never halted on stale history.
            if (currentCycleNumber - recent.CycleNumber > window)
            {
                continue;
            }

            var baselineCycle = recent.CycleNumber - window;
            var basePrice = NewestCloseAtOrBefore(closes, baselineCycle);
            if (basePrice is not decimal baseline || baseline <= 0m)
            {
                continue;
            }

            var movePct = (recent.Price - baseline) / baseline * 100m;
            if (movePct >= opts.UpBandPercent || movePct <= -opts.DownBandPercent)
            {
                await HaltAsync(company, currentCycleId, currentCycleNumber, opts.HaltDurationCycles, movePct >= 0m, now);
            }
        }
    }

    private async Task HaltAsync(
        Company company, int currentCycleId, int currentCycleNumber, int durationCycles, bool wasRise, DateTime now)
    {
        company.TradingHaltedUntilCycleNumber = currentCycleNumber + durationCycles;
        company.UpdatedAt = now;

        await CancelAllOrdersAsync(company.Id, currentCycleId, now);

        var move = wasRise ? "surge" : "slide";
        dbContext.NewsPosts.Add(new NewsPost
        {
            Title = $"Trading in {company.Name} is halted after a sharp {move}",
            Content = $"{company.Name} moved too far, too fast, so trading is frozen for {durationCycles} cycles to let the price settle. Every open order is cancelled and the book re-forms once trading resumes.",
            PublishedInCycleId = currentCycleId,
            PublishedAt = now,
            Scope = NewsImpactScope.None,
        });
    }

    // A halt freezes everyone equally, so the whole book is cancelled — the player's orders and the issuer
    // float included — with buy reservations released, so no cash stays locked through the freeze.
    private async Task CancelAllOrdersAsync(int companyId, int currentCycleId, DateTime now)
    {
        var openOrders = await dbContext.Orders
            .Where(order => order.CompanyId == companyId
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .ToListAsync();
        if (openOrders.Count == 0)
        {
            return;
        }

        var ownerIds = openOrders
            .Where(order => order.ParticipantId != null)
            .Select(order => order.ParticipantId!.Value)
            .Distinct()
            .ToList();
        var ownersById = await dbContext.Participants
            .Where(participant => ownerIds.Contains(participant.Id))
            .ToDictionaryAsync(participant => participant.Id);

        foreach (var order in openOrders)
        {
            if (order.Type == OrderType.Buy && order.ParticipantId is int buyerId && order.ReservedCashAmount > 0m)
            {
                var owner = ownersById[buyerId];
                owner.ReservedBalance -= order.ReservedCashAmount;
                dbContext.MoneyTransactions.Add(new MoneyTransaction
                {
                    ParticipantId = owner.Id,
                    Type = MoneyTransactionType.Release,
                    Amount = order.ReservedCashAmount,
                    RelatedOrderId = order.Id,
                    CreatedInCycleId = currentCycleId,
                    CreatedAt = now,
                });
                order.ReservedCashAmount = 0m;
            }

            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = now;
        }
    }

    private static decimal? NewestCloseAtOrBefore(List<(int CycleNumber, decimal Price)> closes, int baselineCycle)
    {
        for (var index = closes.Count - 1; index >= 0; index--)
        {
            if (closes[index].CycleNumber <= baselineCycle)
            {
                return closes[index].Price;
            }
        }

        return null;
    }

    // One close per cycle keyed on company id, ascending by cycle number. The last snapshot within a cycle is
    // that cycle's close (snapshots are read in ascending id order).
    private async Task<Dictionary<int, List<(int CycleNumber, decimal Price)>>> PerCycleClosesByCompanyAsync()
    {
        var cycleNumbersById = await dbContext.MarketCycles
            .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

        var snapshots = await dbContext.PriceSnapshots
            .OrderBy(snapshot => snapshot.Id)
            .Select(snapshot => new { snapshot.CompanyId, snapshot.CreatedInCycleId, snapshot.Price })
            .ToListAsync();

        return snapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(snapshot => cycleNumbersById.GetValueOrDefault(snapshot.CreatedInCycleId))
                    .OrderBy(cycleGroup => cycleGroup.Key)
                    .Select(cycleGroup => (CycleNumber: cycleGroup.Key, Price: cycleGroup.Last().Price))
                    .ToList());
    }
}
