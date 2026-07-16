using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Gives the company population a life cycle. Once per cycle it may delist one failing company and, separately, may
// list one new one. Nothing closes during the market's first five trading days, and each listing gets the same
// five-day protection from delisting and lifecycle repricing. Closure is deterministic and draws nothing: past
// those grace periods a company qualifies when its price fell in at least 16 of the last 20 recorded per-cycle
// closes, or its three most recent auditor ratings are all High/Extra. At most one company closes per cycle — the
// worst performer when several qualify — and when the market is full at the 300 cap with nothing failing on its
// own, the single worst sub-threshold mature performer is delisted to make room. A company worth at least
// ProtectionCapFraction of total market capitalisation is never closed: a would-be-closed one instead has its price
// cut ProtectedCompanyPriceDropPercent% (via MarketImpactService) and stays listed. A
// closed company's orders are cancelled (buy reservations released), its holdings zeroed with no payout, and it is
// filtered out of the live market while its row survives for history. Appearance is the only random part: the base
// chance to mint a new company each cycle is a population tier — 10% below 50 live companies, 5% below 100, 1% at
// or above — and each delisting since the last listing adds a fixed boost on top so a shrinking population refills
// quickly. Runs in the pre-match window right after share emission so the worth-reading services see the
// post-change state; stages changes and the caller owns the save.
//
// Draw discipline for a scripted Random: closure and the protective price cut both draw nothing. The appearance
// pass draws one NextDouble every cycle the market is below the cap (the base chance is always positive); only a
// clearing roll goes on to draw a share count, a price, an industry index, then one Next for the name.
public sealed class CompanyLifecycleService(
    AppDbContext dbContext,
    IOptions<CompanyLifecycleOptions> options,
    IOptions<RandomChanceRatesOptions> chanceRates,
    Random random,
    MarketImpactService marketImpact)
{
    // The market never lists more than this many live companies.
    private const int MaxCompanies = 300;

    // Base per-cycle appearance chance by live-company count: a sparse market spawns aggressively, a healthy one
    // rarely. Below HighTierCompanyCount uses the high chance, below MidTierCompanyCount the mid chance, otherwise low.
    private const int HighTierCompanyCount = 50;
    private const int MidTierCompanyCount = 100;

    // A company delists when its price fell in at least DeclineThreshold of the last DeclineWindowCycles recorded
    // cycle-over-cycle moves.
    private const int DeclineWindowCycles = 20;
    private const int DeclineThreshold = 16;

    // ...or when its RiskStreakLength most recent ratings are all High or Extra.
    private const int RiskStreakLength = 3;

    // Market-wide and per-listing lifecycle protection use trading days so breaks and cycle-count changes do not
    // shorten either grace period.
    private const int SafePeriodTradingDays = 5;

    // A company worth at least this fraction of total market capitalisation is never closed; a would-be-closed one
    // has its price cut ProtectedCompanyPriceDropPercent% instead.
    private const decimal ProtectionCapFraction = 0.005m;
    private const decimal ProtectedCompanyPriceDropPercent = 60m;

    // A freshly listed company draws a share count and a price in these bands (matching the demo seed), so its
    // starting capitalisation is their product and its price sits well inside the split/merge band.
    private const int MinShares = 100;
    private const int MaxShares = 1000;
    private const int MinPrice = 20;
    private const int MaxPrice = 300;

    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now, Crisis? activeCrisis = null)
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

        var closed = await MaybeCloseOneAsync(liveCompanies, closesByCompany, currentCycleId, currentCycleNumber, now, activeCrisis);
        if (closed)
        {
            market.CompanyClosuresSinceLastAppearance++;
        }

        // The just-closed company frees a slot, so a full market can list a replacement the same cycle.
        var liveCount = liveCompanies.Count - (closed ? 1 : 0);
        await MaybeListNewCompanyAsync(market, liveCount, currentCycleNumber, currentCycleId, now);
    }

    private async Task<bool> MaybeCloseOneAsync(
        List<Company> liveCompanies,
        IReadOnlyDictionary<int, List<decimal>> closesByCompany,
        int currentCycleId,
        int currentCycleNumber,
        DateTime now,
        Crisis? activeCrisis)
    {
        if (liveCompanies.Count == 0)
        {
            return false;
        }

        var currentTradingDayNumber = await TradingDayNumberForCycleAsync(currentCycleId);
        if (currentTradingDayNumber is int marketDayNumber
            && marketDayNumber is >= 1 and <= SafePeriodTradingDays)
        {
            return false;
        }

        var freshCompanyIds = currentTradingDayNumber is int currentDayNumber
            ? await FreshCompanyIdsAsync(liveCompanies, currentDayNumber)
            : [];

        // A company that recently took a big investment is shielded from delisting until its protection day passes.
        var dealProtectedCompanyIds = currentTradingDayNumber is int protectedDayNumber
            ? liveCompanies
                .Where(company => company.CloseProtectedUntilTradingDayNumber is int until && protectedDayNumber < until)
                .Select(company => company.Id)
                .ToHashSet()
            : [];

        var capByCompany = await CapitalizationByCompanyAsync(liveCompanies);
        var riskStreakCompanyIds = await RiskStreakCompanyIdsAsync();
        var pendingCompanyIds = (await dbContext.SettlementInstructions
                .Where(instruction => instruction.Status == SettlementStatus.Pending)
                .Select(instruction => instruction.CompanyId)
                .Distinct()
                .ToListAsync())
            .ToHashSet();

        // A company is protected once it is a large enough slice of the whole market; the threshold scales with
        // total capitalisation rather than a fixed dollar figure. A degenerate zero-cap market protects nothing.
        var totalMarketCap = capByCompany.Values.Sum();
        var protectionThreshold = totalMarketCap * ProtectionCapFraction;
        bool IsProtected(int companyId) =>
            protectionThreshold > 0m && capByCompany.GetValueOrDefault(companyId) >= protectionThreshold;

        var qualifiers = liveCompanies
            .Where(company => !freshCompanyIds.Contains(company.Id)
                && !dealProtectedCompanyIds.Contains(company.Id)
                && !pendingCompanyIds.Contains(company.Id)
                && (HasDeclineStreak(closesByCompany.GetValueOrDefault(company.Id))
                || riskStreakCompanyIds.Contains(company.Id))
            )
            .ToList();

        if (qualifiers.Count > 0)
        {
            var target = WorstPerformer(qualifiers, closesByCompany);

            // A large-cap failure is punished with a price cut rather than a delisting; it is not a closure, so it
            // frees no slot and does not feed the appearance boost.
            if (IsProtected(target.Id))
            {
                await CrashProtectedCompanyAsync(target, currentCycleId, now);
                return false;
            }

            await CloseCompanyAsync(target, currentCycleId, currentCycleNumber, now, activeCrisis);
            return true;
        }

        if (liveCompanies.Count >= MaxCompanies)
        {
            // Pressure valve: the market is full and nothing failed on its own. Only sub-threshold companies are
            // removable, so a large-cap is never delisted just to make room; if all are protected, nothing happens.
            var removable = liveCompanies
                .Where(company => !freshCompanyIds.Contains(company.Id)
                    && !dealProtectedCompanyIds.Contains(company.Id)
                    && !IsProtected(company.Id)
                    && !pendingCompanyIds.Contains(company.Id))
                .ToList();
            if (removable.Count == 0)
            {
                return false;
            }

            await CloseCompanyAsync(
                WorstPerformer(removable, closesByCompany), currentCycleId, currentCycleNumber, now, activeCrisis);
            return true;
        }

        return false;
    }

    private async Task<int?> TradingDayNumberForCycleAsync(int cycleId) =>
        await (from cycle in dbContext.MarketCycles
               join day in dbContext.TradingDays on cycle.TradingDayId equals day.Id
               where cycle.Id == cycleId
               select (int?)day.DayNumber)
            .SingleOrDefaultAsync();

    private async Task<HashSet<int>> FreshCompanyIdsAsync(
        IReadOnlyList<Company> liveCompanies,
        int currentDayNumber)
    {
        var liveCompanyIds = liveCompanies.Select(company => company.Id).ToList();
        var listingDays = await (from company in dbContext.Companies
                                 where liveCompanyIds.Contains(company.Id) && company.CreatedInCycleId.HasValue
                                 join cycle in dbContext.MarketCycles on company.CreatedInCycleId equals (int?)cycle.Id
                                 join day in dbContext.TradingDays on cycle.TradingDayId equals day.Id
                                 select new { company.Id, day.DayNumber })
            .ToListAsync();

        return listingDays
            .Where(listing => listing.DayNumber <= currentDayNumber
                && currentDayNumber - listing.DayNumber < SafePeriodTradingDays)
            .Select(listing => listing.Id)
            .ToHashSet();
    }

    // Market cap per live company = latest price × issued shares; a company with no price snapshot counts as zero.
    private async Task<Dictionary<int, decimal>> CapitalizationByCompanyAsync(IReadOnlyList<Company> liveCompanies)
    {
        var latestPriceByCompany = await PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);
        return liveCompanies.ToDictionary(
            company => company.Id,
            company => latestPriceByCompany.GetValueOrDefault(company.Id) * company.IssuedSharesCount);
    }

    // A protected large-cap that would have been delisted takes a fixed price cut and stays listed, with a
    // company-scoped news post recording the drop. The impact is applied here, so the post carries no second one.
    private async Task CrashProtectedCompanyAsync(Company company, int currentCycleId, DateTime now)
    {
        await marketImpact.ApplyImpactAsync(
            NewsImpactDirection.Decrease, [company.Id], ProtectedCompanyPriceDropPercent, currentCycleId, now);

        company.UpdatedAt = now;

        dbContext.NewsPosts.Add(new NewsPost
        {
            Title = $"{company.Name} takes a sharp writedown",
            Content = $"{company.Name} would have been delisted, but as a large-cap it is spared — its share price is cut {ProtectedCompanyPriceDropPercent:N0}% instead.",
            PublishedInCycleId = currentCycleId,
            ImpactAppliedInCycleId = currentCycleId,
            PublishedAt = now,
            Scope = NewsImpactScope.Company,
            Direction = NewsImpactDirection.Decrease,
            ImpactPercent = ProtectedCompanyPriceDropPercent,
            TargetCompanyId = company.Id,
        });
    }

    // The most-negative recent price change, id order breaking ties, so selection is deterministic.
    private static Company WorstPerformer(
        IReadOnlyList<Company> companies,
        IReadOnlyDictionary<int, List<decimal>> closesByCompany) =>
        companies
            .OrderBy(company => RecentChange(closesByCompany.GetValueOrDefault(company.Id)))
            .ThenBy(company => company.Id)
            .First();

    private async Task CloseCompanyAsync(
        Company company, int currentCycleId, int currentCycleNumber, DateTime now, Crisis? activeCrisis)
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
                    Description = "Reserved cash released on company delisting",
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
            holding.SettledQuantity = 0;
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
            Category = NewsCategory.CompanyClosed,
        });

        // A delisting that lands while a crisis is active joins that crisis's timeline.
        if (activeCrisis is not null)
        {
            dbContext.CrisisEvents.Add(new CrisisEvent
            {
                CrisisId = activeCrisis.Id,
                Type = CrisisEventType.CompanyClosed,
                Description = $"{company.Name} was delisted",
                CompanyId = company.Id,
                CreatedInCycleId = currentCycleId,
                CreatedInCycleNumber = currentCycleNumber,
                CreatedAt = now,
            });
        }
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

        var triggers = chanceRates.Value.EventTriggerChances;
        var baseChance = liveCount < HighTierCompanyCount ? triggers.CompanyAppearanceHigh
            : liveCount < MidTierCompanyCount ? triggers.CompanyAppearanceMid
            : triggers.CompanyAppearanceLow;
        var chance = Math.Min(
            baseChance + market.CompanyClosuresSinceLastAppearance * chanceRates.Value.ChanceModifiers.CompanyClosureAppearanceBoost,
            1.0);

        // The base chance is always positive, so a roll is drawn every cycle below the cap; only a clearing roll
        // goes on to draw listing parameters.
        if (random.NextDouble() >= chance)
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
            CashBalance = 0m,
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
        market.CompanyClosuresSinceLastAppearance = 0;
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
