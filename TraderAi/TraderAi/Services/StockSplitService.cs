using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Keeps per-share prices in a sane band the way real markets do: a price above the high threshold splits the
// stock N-for-1 (shares up, price down) while a price below the low threshold merges it N-to-1 (shares down,
// price up). A split is exact so market cap and holder worth are unchanged; a merge floors each holding to a
// whole share and drops the sub-share remainder, so a holder's worth can tick down by under one merged share.
// Runs once per cycle before the pre-match services so they read the post-change state, and it is the
// deterministic member of the per-cycle family — no Random, nothing drawn. Stages changes; the caller saves.
public sealed class StockSplitService(
    AppDbContext dbContext,
    IOptions<StockSplitOptions> options)
{
    // A company trading at or above this splits; the fixed ratio brings it well back under, so it does not
    // retrigger until it has grown severalfold again.
    private const decimal SplitPriceThreshold = 1000m;
    private const int SplitRatio = 4;

    // A company trading below this merges N-to-1; the ratio lifts a just-under price clear of the threshold, and
    // a still-cheap penny stock simply merges again next cycle until it clears.
    private const decimal MergePriceThreshold = 5m;

    // Safety cap: never split past this issued-share count, so repeated splits on a runaway price cannot
    // overflow the 32-bit share/quantity fields.
    private const long MaxIssuedShares = 1_000_000_000L;

    // Floor mirroring MaxIssuedShares: never merge when the post-merge share count would fall below this, so a
    // stuck penny price cannot collapse the float toward zero.
    private const int MinIssuedShares = 20;

    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var latestPriceByCompany = await LatestPriceByCompanyAsync();

        var companies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null)
            .OrderBy(company => company.Id)
            .ToListAsync();
        foreach (var company in companies)
        {
            if (!latestPriceByCompany.TryGetValue(company.Id, out var price))
            {
                continue;
            }

            if (price >= SplitPriceThreshold)
            {
                // The overflow guard keeps a runaway price from splitting past the 32-bit share fields.
                if ((long)company.IssuedSharesCount * SplitRatio <= MaxIssuedShares)
                {
                    await SplitAsync(company, price, currentCycleId, now);
                }
            }
            else if (price is > 0m and < MergePriceThreshold)
            {
                // The floor guard keeps a stuck penny price from merging the float toward zero.
                if (company.IssuedSharesCount / SplitRatio >= MinIssuedShares)
                {
                    await MergeAsync(company, price, currentCycleId, now);
                }
            }
        }
    }

    private async Task SplitAsync(Company company, decimal price, int currentCycleId, DateTime now)
    {
        company.IssuedSharesCount *= SplitRatio;
        company.UpdatedAt = now;

        // Positions: more shares at a proportionally lower average cost, so total cost basis and worth are unchanged.
        var holdings = await dbContext.Holdings.Where(holding => holding.CompanyId == company.Id).ToListAsync();
        foreach (var holding in holdings)
        {
            holding.Quantity *= SplitRatio;
            holding.AverageCost = Round(holding.AverageCost / SplitRatio);
        }

        var openOrders = await dbContext.Orders
            .Where(order => order.CompanyId == company.Id
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .ToListAsync();

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
            // The issuer float must keep standing — cancelling it would strand the unsold supply — so it is
            // re-denominated in place; every participant order, the player's included, is cancelled so the book
            // re-forms at the new price next cycle.
            if (order.ParticipantId is int ownerId)
            {
                CancelParticipantOrder(order, ownersById[ownerId], currentCycleId, now);
            }
            else
            {
                RedenominateFloat(order, now);
            }
        }

        // The split-adjusted price becomes the company's current quote; capitalisation is unchanged (the price
        // fell by the ratio while the share count rose by it), so the capitalisation chart stays continuous.
        var splitPrice = Round(price / SplitRatio);
        dbContext.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = splitPrice,
            Capitalization = splitPrice * company.IssuedSharesCount,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });

        // An impact-free announcement: it names the company but moves no price and touches no industry.
        dbContext.NewsPosts.Add(new NewsPost
        {
            Title = $"{company.Name} completes a {SplitRatio}-for-1 stock split",
            Content = $"{company.Name} split its stock {SplitRatio}-for-1. Every share becomes {SplitRatio}, and the price is divided to match, so each shareholder's total value is unchanged.",
            PublishedInCycleId = currentCycleId,
            PublishedAt = now,
            Scope = NewsImpactScope.None,
            Category = NewsCategory.StockSplit,
        });
    }

    private async Task MergeAsync(Company company, decimal price, int currentCycleId, DateTime now)
    {
        var holdings = await dbContext.Holdings.Where(holding => holding.CompanyId == company.Id).ToListAsync();
        var ownedBefore = holdings.Sum(holding => holding.Quantity);

        // Positions: fewer shares at a proportionally higher average cost. Integer division floors the sub-share
        // remainder away rather than paying cash for it, so a holder of fewer than the ratio is zeroed out.
        var ownedAfter = 0;
        foreach (var holding in holdings)
        {
            holding.Quantity /= SplitRatio;
            holding.AverageCost = Round(holding.AverageCost * SplitRatio);
            ownedAfter += holding.Quantity;
        }

        // Re-derive the issued count from the floored holdings plus the floored implicit float, so the identity
        // IssuedSharesCount − Σ Holdings stays exact after the remainders are dropped.
        var floatBefore = company.IssuedSharesCount - ownedBefore;
        company.IssuedSharesCount = ownedAfter + floatBefore / SplitRatio;
        company.LastMergedInCycleId = currentCycleId;
        company.UpdatedAt = now;

        var openOrders = await dbContext.Orders
            .Where(order => order.CompanyId == company.Id
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .ToListAsync();

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
            // Same policy as a split: keep the issuer float standing (re-denominated) so the unsold supply is
            // not stranded, and cancel every participant order — the player's included — to reform the book.
            if (order.ParticipantId is int ownerId)
            {
                CancelParticipantOrder(order, ownersById[ownerId], currentCycleId, now);
            }
            else
            {
                RedenominateFloatForMerge(order, now);
            }
        }

        // Multiplying the price while the share count fell by the same ratio leaves capitalisation unchanged,
        // save for the tiny value of the floored-away fractional shares.
        var mergePrice = Round(price * SplitRatio);
        dbContext.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = mergePrice,
            Capitalization = mergePrice * company.IssuedSharesCount,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });

        dbContext.NewsPosts.Add(new NewsPost
        {
            Title = $"{company.Name} completes a {SplitRatio}-to-1 reverse split",
            Content = $"{company.Name} merged its stock {SplitRatio}-to-1. Every {SplitRatio} shares become one and the price is multiplied to match, so each shareholder's total value is essentially unchanged.",
            PublishedInCycleId = currentCycleId,
            PublishedAt = now,
            Scope = NewsImpactScope.None,
            Category = NewsCategory.StockMerge,
        });
    }

    private void RedenominateFloat(Order order, DateTime now)
    {
        // The float is a company-originated sell (it holds no reserved cash), so only quantity and limit change.
        order.Quantity *= SplitRatio;
        order.FilledQuantity *= SplitRatio;
        order.LimitPrice = Round(order.LimitPrice / SplitRatio);
        order.UpdatedAt = now;
    }

    private void RedenominateFloatForMerge(Order order, DateTime now)
    {
        order.Quantity /= SplitRatio;
        order.FilledQuantity /= SplitRatio;
        order.LimitPrice = Round(order.LimitPrice * SplitRatio);
        order.UpdatedAt = now;
    }

    private void CancelParticipantOrder(Order order, Participant owner, int currentCycleId, DateTime now)
    {
        // Releasing a buy's reservation returns the cash to the owner's spendable balance.
        if (order.Type == OrderType.Buy && order.ReservedCashAmount > 0m)
        {
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

    private Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync() =>
        PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
