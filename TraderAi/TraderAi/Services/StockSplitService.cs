using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Keeps per-share prices in a sane band the way real markets do: when a company's latest price climbs past a
// threshold it splits the stock N-for-1 — multiplying share counts and dividing the price so market cap and
// every holder's worth stay exactly the same, only the denomination shrinks. Runs once per cycle before the
// pre-match services so they all read the post-split state. It is the deterministic member of the per-cycle
// service family: it takes no Random and draws nothing, so a scripted Random in tests is never touched. Stages
// changes on the shared context; the caller owns the save.
public sealed class StockSplitService(
    AppDbContext dbContext,
    IOptions<StockSplitOptions> options)
{
    // A company trading at or above this splits; the fixed ratio brings it well back under, so it does not
    // retrigger until it has grown severalfold again.
    private const decimal SplitPriceThreshold = 1000m;
    private const int SplitRatio = 4;

    // Safety cap: never split past this issued-share count, so repeated splits on a runaway price cannot
    // overflow the 32-bit share/quantity fields.
    private const long MaxIssuedShares = 1_000_000_000L;

    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var latestPriceByCompany = await LatestPriceByCompanyAsync();

        var companies = await dbContext.Companies.OrderBy(company => company.Id).ToListAsync();
        foreach (var company in companies)
        {
            if (latestPriceByCompany.GetValueOrDefault(company.Id) < SplitPriceThreshold)
            {
                continue;
            }

            if ((long)company.IssuedSharesCount * SplitRatio > MaxIssuedShares)
            {
                continue;
            }

            await SplitAsync(company, latestPriceByCompany[company.Id], currentCycleId, now);
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

    private async Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync()
    {
        var snapshots = await dbContext.PriceSnapshots.ToListAsync();
        return snapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.Id).First().Price);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
