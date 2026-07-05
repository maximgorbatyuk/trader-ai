using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Gives the company population a life cycle. Once per cycle it may delist one failing company and, separately, may
// list one new one. Closure is deterministic and draws nothing: a company qualifies when its price fell in at
// least 15 of the last 20 recorded per-cycle closes, or its three most recent auditor ratings are all High/Extra.
// At most one company closes per cycle — the worst performer when several qualify — and when the market is full at
// the 300 cap with nothing failing on its own, the single worst performer is delisted to make room. A closed
// company's orders are cancelled (buy reservations released), its holdings zeroed with no payout, and it is
// filtered out of the live market while its row survives for history. Appearance is the only random part: after a
// 100-cycle safe period the chance to mint a new company climbs 0.1% per cycle to a certainty. Runs in the
// pre-match window right after share emission so the worth-reading services see the post-change state; stages
// changes and the caller owns the save.
//
// Draw discipline for a scripted Random: closure draws nothing. Appearance draws one NextDouble only once past the
// safe period (inside it the chance is 0 and no roll is taken); if it fires it draws a share count, a price, an
// industry index, then one Next for the name.
public sealed class CompanyLifecycleService(
    AppDbContext dbContext,
    IOptions<CompanyLifecycleOptions> options,
    Random random)
{
    // The market never lists more than this many live companies.
    private const int MaxCompanies = 300;

    // No company appears for this many cycles after the last listing; past it the chance climbs by a step per cycle.
    private const int SafePeriodCycles = 100;
    private const double ChanceStepPerCycle = 0.001;

    // A company delists when its price fell in at least DeclineThreshold of the last DeclineWindowCycles recorded
    // cycle-over-cycle moves.
    private const int DeclineWindowCycles = 20;
    private const int DeclineThreshold = 15;

    // ...or when its RiskStreakLength most recent ratings are all High or Extra.
    private const int RiskStreakLength = 3;

    // A freshly listed company draws a share count and a price in these bands (matching the demo seed), so its
    // starting capitalisation is their product and its price sits well inside the split/merge band.
    private const int MinShares = 100;
    private const int MaxShares = 1000;
    private const int MinPrice = 20;
    private const int MaxPrice = 300;

    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market is null)
        {
            return;
        }

        var liveCompanies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null)
            .OrderBy(company => company.Id)
            .ToListAsync();

        var closesByCompany = await PerCycleClosesByCompanyAsync();

        var closed = await MaybeCloseOneAsync(liveCompanies, closesByCompany, currentCycleId, now);

        // The just-closed company frees a slot, so a full market can list a replacement the same cycle.
        var liveCount = liveCompanies.Count - (closed ? 1 : 0);
        await MaybeListNewCompanyAsync(market, liveCount, currentCycleNumber, currentCycleId, now);
    }

    private async Task<bool> MaybeCloseOneAsync(
        List<Company> liveCompanies,
        IReadOnlyDictionary<int, List<decimal>> closesByCompany,
        int currentCycleId,
        DateTime now)
    {
        if (liveCompanies.Count == 0)
        {
            return false;
        }

        var riskStreakCompanyIds = await RiskStreakCompanyIdsAsync();

        var qualifiers = liveCompanies
            .Where(company => HasDeclineStreak(closesByCompany.GetValueOrDefault(company.Id))
                || riskStreakCompanyIds.Contains(company.Id))
            .ToList();

        Company? target;
        if (qualifiers.Count > 0)
        {
            target = WorstPerformer(qualifiers, closesByCompany);
        }
        else if (liveCompanies.Count >= MaxCompanies)
        {
            // Pressure valve: the market is full and nothing failed on its own, so the single worst performer is
            // delisted to make room for fresh listings.
            target = WorstPerformer(liveCompanies, closesByCompany);
        }
        else
        {
            return false;
        }

        await CloseCompanyAsync(target, currentCycleId, now);
        return true;
    }

    // The most-negative recent price change, id order breaking ties, so selection is deterministic.
    private static Company WorstPerformer(
        IReadOnlyList<Company> companies,
        IReadOnlyDictionary<int, List<decimal>> closesByCompany) =>
        companies
            .OrderBy(company => RecentChange(closesByCompany.GetValueOrDefault(company.Id)))
            .ThenBy(company => company.Id)
            .First();

    private async Task CloseCompanyAsync(Company company, int currentCycleId, DateTime now)
    {
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
            // A buy releases its reserved cash back to the owner; participant sells and the issuer float just cancel.
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

        // Holders lose their shares with no payout; the row is kept at zero quantity per the holdings convention.
        var holdings = await dbContext.Holdings
            .Where(holding => holding.CompanyId == company.Id && holding.Quantity > 0)
            .ToListAsync();
        foreach (var holding in holdings)
        {
            holding.Quantity = 0;
        }

        company.ClosedInCycleId = currentCycleId;
        company.ClosedAt = now;
        company.UpdatedAt = now;

        dbContext.NewsPosts.Add(new NewsPost
        {
            Title = $"{company.Name} is delisted from the market",
            Content = $"{company.Name} has been delisted after a sustained decline. Its open orders are cancelled and its shares are wiped out — shareholders recover nothing.",
            PublishedInCycleId = currentCycleId,
            PublishedAt = now,
            Scope = NewsImpactScope.None,
        });
    }

    private async Task MaybeListNewCompanyAsync(
        Market market,
        int liveCount,
        int currentCycleNumber,
        int currentCycleId,
        DateTime now)
    {
        if (liveCount >= MaxCompanies)
        {
            return;
        }

        var cyclesSinceLast = currentCycleNumber - market.LastCompanyAppearanceCycleNumber;
        var chance = Math.Min(Math.Max(0, cyclesSinceLast - SafePeriodCycles) * ChanceStepPerCycle, 1.0);

        // Inside the safe period the chance is zero and no roll is drawn, keeping a scripted Random predictable.
        if (chance <= 0.0 || random.NextDouble() >= chance)
        {
            return;
        }

        var industryIds = await dbContext.Industries
            .OrderBy(industry => industry.Id)
            .Select(industry => industry.Id)
            .ToListAsync();
        if (industryIds.Count == 0)
        {
            return;
        }

        var shares = random.Next(MinShares, MaxShares + 1);
        var price = (decimal)random.Next(MinPrice, MaxPrice + 1);
        var industryId = industryIds[random.Next(industryIds.Count)];

        var takenNames = (await dbContext.Companies
                .Select(company => company.Name)
                .ToListAsync())
            .ToHashSet();
        var name = DemoMarketNames.PickOneCompany(random, takenNames);

        var company = new Company
        {
            Name = name,
            IndustryId = industryId,
            IssuedSharesCount = shares,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.Companies.Add(company);

        // Saved here so the float order and the first price snapshot below can reference the new company's id.
        await dbContext.SaveChangesAsync();

        // The whole issued supply starts as the company float: one company-originated sell at the listing price.
        dbContext.Orders.Add(new Order
        {
            ParticipantId = null,
            CompanyId = company.Id,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = shares,
            FilledQuantity = 0,
            LimitPrice = price,
            ReservedCashAmount = 0m,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
            UpdatedAt = now,
        });

        dbContext.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = price,
            Capitalization = price * shares,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });

        dbContext.NewsPosts.Add(new NewsPost
        {
            Title = $"{name} lists on the market",
            Content = $"{name} has listed {shares:N0} shares at ${price:N2} apiece, opening a fresh position for traders to take.",
            PublishedInCycleId = currentCycleId,
            PublishedAt = now,
            Scope = NewsImpactScope.None,
        });

        market.LastCompanyAppearanceCycleNumber = currentCycleNumber;
        market.UpdatedAt = now;
    }

    // One close per cycle, oldest to newest, keyed on company id. The last snapshot within a cycle is that cycle's
    // close (snapshots are read in ascending id order), so a cycle with several trades still contributes one point.
    private async Task<Dictionary<int, List<decimal>>> PerCycleClosesByCompanyAsync()
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
                    .Select(cycleGroup => cycleGroup.Last().Price)
                    .ToList());
    }

    private async Task<HashSet<int>> RiskStreakCompanyIdsAsync()
    {
        var ratings = await dbContext.CompanyRatings
            .OrderByDescending(rating => rating.Id)
            .Select(rating => new { rating.CompanyId, rating.Rating })
            .ToListAsync();

        var result = new HashSet<int>();
        foreach (var group in ratings.GroupBy(rating => rating.CompanyId))
        {
            var recent = group.Take(RiskStreakLength).ToList();
            if (recent.Count == RiskStreakLength
                && recent.All(rating => rating.Rating is CompanyRiskRating.High or CompanyRiskRating.Extra))
            {
                result.Add(group.Key);
            }
        }

        return result;
    }

    private static bool HasDeclineStreak(List<decimal>? closes)
    {
        // Need one more close than the window so the window has a prior-cycle price to compare its first move against.
        if (closes is not { Count: > DeclineWindowCycles })
        {
            return false;
        }

        var window = closes.Skip(closes.Count - (DeclineWindowCycles + 1)).ToList();
        var decreases = 0;
        for (var index = 1; index < window.Count; index++)
        {
            if (window[index] < window[index - 1])
            {
                decreases++;
            }
        }

        return decreases >= DeclineThreshold;
    }

    // Fractional price change across the decline window (or the full history if shorter); too little history counts
    // as flat so a young company is not spuriously ranked the worst performer.
    private static decimal RecentChange(List<decimal>? closes)
    {
        if (closes is not { Count: > 1 })
        {
            return 0m;
        }

        var window = closes.Count > DeclineWindowCycles + 1
            ? closes.Skip(closes.Count - (DeclineWindowCycles + 1)).ToList()
            : closes;
        var first = window[0];
        var last = window[^1];
        return first > 0m ? (last - first) / first : 0m;
    }
}
