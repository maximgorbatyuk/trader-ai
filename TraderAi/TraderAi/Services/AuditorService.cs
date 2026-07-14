using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Weighted selection keeps large companies prominent without starving small ones, while the cooldown prevents
// repeated reviews. Extra outcomes share one roll so eligible positive and negative bands stay exactly symmetric.
// Price changes share MarketImpactService, and the ordinary positive roll remains gated to preserve seeded sequences.
public sealed class AuditorService(
    AppDbContext dbContext,
    IOptions<AuditorOptions> options,
    IOptions<RandomChanceRatesOptions> chanceRates,
    Random random,
    MarketImpactService marketImpact)
{
    // Roughly this fraction of companies is reviewed each cycle; the seeded auditor count is sized from it.
    private const double ReviewFraction = 0.05;

    // A rated company cannot be reviewed again until this many cycles have passed.
    private const int SafePeriodCycles = 15;

    // The price trend is measured across this many cycles; a per-cycle move at or beyond the threshold is "big".
    private const int TrendWindowCycles = 10;
    private const double BigMovePerCycleThreshold = 0.05;

    // Extra verdicts use the same magnitude in opposite directions.
    private const decimal MinExtraImpactPercent = 10m;
    private const decimal MaxExtraImpactPercent = 20m;

    private const decimal MinRaisePercent = 5m;
    private const decimal MaxRaisePercent = 15m;

    // Buyers revise their bids when a company is flagged: a base cancel chance nudged by the owner's risk profile
    // and temperament.
    private const double LowRiskCancelDelta = 0.15;
    private const double HighRiskCancelDelta = -0.15;
    private const double ConservativeCancelDelta = 0.15;

    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now, Crisis? activeCrisis = null)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        await EnsureAuditorsExistAsync(now);

        var auditors = await dbContext.Auditors.OrderBy(auditor => auditor.Id).ToListAsync();
        var companies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null)
            .ToDictionaryAsync(company => company.Id);
        if (auditors.Count == 0 || companies.Count == 0)
        {
            return;
        }

        var cycleNumbersById = await dbContext.MarketCycles
            .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

        var snapshotsByCompany = (await dbContext.PriceSnapshots
                .OrderBy(snapshot => snapshot.Id)
                .Select(snapshot => new { snapshot.CompanyId, snapshot.CreatedInCycleId, snapshot.Price })
                .ToListAsync())
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(snapshot => (
                        CycleNumber: cycleNumbersById.GetValueOrDefault(snapshot.CreatedInCycleId),
                        snapshot.Price))
                    .ToList());

        var ratingRows = await dbContext.CompanyRatings
            .Select(rating => new { rating.Id, rating.CompanyId, rating.Rating, rating.CreatedInCycleId })
            .ToListAsync();
        var latestRatingCycleByCompany = ratingRows
            .GroupBy(rating => rating.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.Max(rating => cycleNumbersById.GetValueOrDefault(rating.CreatedInCycleId)));
        var extraRaiseEligibleCompanyIds = ratingRows
            .GroupBy(rating => rating.CompanyId)
            .Where(group =>
            {
                var recent = group.OrderByDescending(rating => rating.Id).Take(5).Select(rating => rating.Rating).ToList();
                return recent.Contains(CompanyRiskRating.RaisedExpectations)
                    && !recent.Contains(CompanyRiskRating.Extra);
            })
            .Select(group => group.Key)
            .ToHashSet();

        var picked = new HashSet<int>();

        foreach (var auditor in auditors)
        {
            var eligible = companies.Values
                .Where(company => !picked.Contains(company.Id)
                    && IsEligible(company.Id, currentCycleNumber, latestRatingCycleByCompany))
                .OrderBy(company => company.Id)
                .ToList();

            if (eligible.Count == 0)
            {
                continue;
            }

            var company = PickWeightedByCapitalization(eligible, snapshotsByCompany);
            picked.Add(company.Id);

            var stable = IsStable(snapshotsByCompany.GetValueOrDefault(company.Id), currentCycleNumber);
            var triggers = chanceRates.Value.EventTriggerChances;
            var extraOutcomeChance = stable ? triggers.AuditorIssueOnStable : triggers.AuditorIssueOnBigMove;
            if (activeCrisis is not null)
            {
                extraOutcomeChance = Math.Min(
                    1.0,
                    extraOutcomeChance * chanceRates.Value.ChanceModifiers.CrisisAuditorIssueMultiplier);
            }

            var extraOutcomeRoll = random.NextDouble();
            var issueFound = extraOutcomeRoll < extraOutcomeChance;
            var extraRaiseFound = !issueFound
                && extraRaiseEligibleCompanyIds.Contains(company.Id)
                && extraOutcomeRoll < extraOutcomeChance * 2;

            CompanyRiskRating rating;
            decimal? impactPercent = null;

            if (issueFound)
            {
                rating = CompanyRiskRating.Extra;
                impactPercent = Round(MinExtraImpactPercent
                    + ((decimal)random.NextDouble() * (MaxExtraImpactPercent - MinExtraImpactPercent)));

                // cancelStaleOrders: false — the personality-weighted revision below replaces the blanket cancel.
                await marketImpact.ApplyImpactAsync(
                    NewsImpactDirection.Decrease, [company.Id], impactPercent.Value, currentCycleId, now, cancelStaleOrders: false);

                await ReviseBuyOrdersAsync(company.Id, chanceRates.Value.EventTriggerChances.AuditorExtraRatingBuyRevision, currentCycleId, now);

                var (title, content) = DemoAuditContent.Issue(company.Name, random);
                dbContext.NewsPosts.Add(new NewsPost
                {
                    Title = title,
                    Content = content,
                    PublishedInCycleId = currentCycleId,
                    ImpactAppliedInCycleId = currentCycleId,
                    PublishedAt = now,
                    Scope = NewsImpactScope.Company,
                    Direction = NewsImpactDirection.Decrease,
                    ImpactPercent = impactPercent,
                    TargetCompanyId = company.Id,
                });
            }
            else if (extraRaiseFound
                || (triggers.AuditorRaiseExpectationsChance > 0d
                    && random.NextDouble() < triggers.AuditorRaiseExpectationsChance))
            {
                rating = extraRaiseFound
                    ? CompanyRiskRating.ExtraRaisedExpectations
                    : CompanyRiskRating.RaisedExpectations;
                var minImpactPercent = extraRaiseFound ? MinExtraImpactPercent : MinRaisePercent;
                var maxImpactPercent = extraRaiseFound ? MaxExtraImpactPercent : MaxRaisePercent;
                impactPercent = Round(minImpactPercent
                    + ((decimal)random.NextDouble() * (maxImpactPercent - minImpactPercent)));

                await marketImpact.ApplyImpactAsync(
                    NewsImpactDirection.Increase,
                    [company.Id],
                    impactPercent.Value,
                    currentCycleId,
                    now,
                    cancelStaleOrders: false);

                await ReviseSellOrdersAsync(company.Id, now);

                var (title, content) = extraRaiseFound
                    ? DemoAuditContent.ExtraRaisedExpectations(company.Name, random)
                    : DemoAuditContent.RaisedExpectations(company.Name, random);
                dbContext.NewsPosts.Add(new NewsPost
                {
                    Title = title,
                    Content = content,
                    PublishedInCycleId = currentCycleId,
                    ImpactAppliedInCycleId = currentCycleId,
                    PublishedAt = now,
                    Scope = NewsImpactScope.Company,
                    Direction = NewsImpactDirection.Increase,
                    ImpactPercent = impactPercent,
                    TargetCompanyId = company.Id,
                });
            }
            else if (!stable)
            {
                rating = CompanyRiskRating.High;

                await ReviseBuyOrdersAsync(company.Id, chanceRates.Value.EventTriggerChances.AuditorHighRatingBuyRevision, currentCycleId, now);

                var (title, content) = DemoAuditContent.HighRisk(company.Name, random);
                dbContext.NewsPosts.Add(new NewsPost
                {
                    Title = title,
                    Content = content,
                    PublishedInCycleId = currentCycleId,
                    PublishedAt = now,
                    Scope = NewsImpactScope.None,
                });
            }
            else
            {
                rating = CompanyRiskRating.Low;
            }

            dbContext.CompanyRatings.Add(new CompanyRating
            {
                CompanyId = company.Id,
                AuditorId = auditor.Id,
                Rating = rating,
                ImpactPercent = impactPercent,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
            });

            // A risky verdict uncovered while a crisis is active joins that crisis's timeline.
            if (activeCrisis is not null && rating is CompanyRiskRating.High or CompanyRiskRating.Extra)
            {
                dbContext.CrisisEvents.Add(new CrisisEvent
                {
                    CrisisId = activeCrisis.Id,
                    Type = CrisisEventType.AuditorRating,
                    Description = $"{company.Name} rated {rating} risk",
                    CompanyId = company.Id,
                    ImpactPercent = impactPercent,
                    CreatedInCycleId = currentCycleId,
                    CreatedInCycleNumber = currentCycleNumber,
                    CreatedAt = now,
                });
            }
        }
    }

    private async Task EnsureAuditorsExistAsync(DateTime now)
    {
        if (await dbContext.Auditors.AnyAsync())
        {
            return;
        }

        var companyCount = await dbContext.Companies.CountAsync(company => company.ClosedInCycleId == null);
        if (companyCount == 0)
        {
            return;
        }

        foreach (var (name, description) in DemoAuditorProfiles.Take(AuditorCountFor(companyCount)))
        {
            dbContext.Auditors.Add(new Auditor { Name = name, Description = description, CreatedAt = now });
        }

        // Saved here so the freshly minted auditors have ids the rating rows can reference this same cycle.
        await dbContext.SaveChangesAsync();
    }

    // The seeded/backfilled auditor count: enough for roughly ReviewFraction of companies to be reviewed each
    // cycle, since each auditor reviews one company per cycle.
    public static int AuditorCountFor(int companyCount) =>
        Math.Max(1, (int)Math.Ceiling(companyCount * ReviewFraction));

    private static bool IsEligible(
        int companyId,
        int currentCycleNumber,
        IReadOnlyDictionary<int, int> latestRatingCycleByCompany) =>
        !latestRatingCycleByCompany.TryGetValue(companyId, out var ratedAtCycle)
        || currentCycleNumber - ratedAtCycle >= SafePeriodCycles;

    private Company PickWeightedByCapitalization(
        IReadOnlyList<Company> eligible,
        IReadOnlyDictionary<int, List<(int CycleNumber, decimal Price)>> snapshotsByCompany)
    {
        var floor = 1.0 / eligible.Count;
        var caps = new double[eligible.Count];
        var totalCap = 0.0;
        for (var index = 0; index < eligible.Count; index++)
        {
            var latest = LatestPrice(snapshotsByCompany.GetValueOrDefault(eligible[index].Id));
            caps[index] = (double)latest * eligible[index].IssuedSharesCount;
            totalCap += caps[index];
        }

        var weights = new double[eligible.Count];
        var totalWeight = 0.0;
        for (var index = 0; index < eligible.Count; index++)
        {
            var proportion = totalCap > 0 ? caps[index] / totalCap : 0.0;
            weights[index] = Math.Max(proportion, floor);
            totalWeight += weights[index];
        }

        var roll = random.NextDouble() * totalWeight;
        var cumulative = 0.0;
        for (var index = 0; index < eligible.Count; index++)
        {
            cumulative += weights[index];
            if (roll < cumulative)
            {
                return eligible[index];
            }
        }

        return eligible[^1];
    }

    // Stable unless the per-cycle compound price move over the trend window meets the big-move threshold in
    // either direction. Too little history, or a non-positive price to compare, counts as stable.
    private static bool IsStable(List<(int CycleNumber, decimal Price)>? snapshots, int currentCycleNumber)
    {
        if (snapshots is not { Count: > 0 })
        {
            return true;
        }

        var latest = snapshots[^1].Price;
        if (latest <= 0m)
        {
            return true;
        }

        var targetCycle = currentCycleNumber - TrendWindowCycles;
        var oldPrice = snapshots[0].Price;
        var oldCycle = snapshots[0].CycleNumber;
        foreach (var (cycleNumber, price) in snapshots)
        {
            if (cycleNumber > targetCycle)
            {
                break;
            }

            oldPrice = price;
            oldCycle = cycleNumber;
        }

        if (oldPrice <= 0m)
        {
            return true;
        }

        var span = Math.Max(1, currentCycleNumber - oldCycle);
        var perCycleRate = Math.Pow((double)(latest / oldPrice), 1.0 / span) - 1.0;
        return Math.Abs(perCycleRate) < BigMovePerCycleThreshold;
    }

    private async Task ReviseBuyOrdersAsync(int companyId, double baseChance, int currentCycleId, DateTime now)
    {
        var orders = await dbContext.Orders
            .Where(order => order.ParticipantId != null
                && order.CompanyId == companyId
                && order.Type == OrderType.Buy
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .OrderBy(order => order.Id)
            .ToListAsync();

        if (orders.Count == 0)
        {
            return;
        }

        var ownerIds = orders.Select(order => order.ParticipantId!.Value).Distinct().ToList();
        var ownersById = await dbContext.Participants
            .Where(participant => ownerIds.Contains(participant.Id))
            .ToDictionaryAsync(participant => participant.Id);

        foreach (var order in orders)
        {
            var owner = ownersById[order.ParticipantId!.Value];

            // The market never auto-cancels the human player, and a bankrupt trader's orders belong to the
            // bankruptcy service; neither draws a roll.
            if (owner.Type == ParticipantType.Player || owner.IsBankrupt)
            {
                continue;
            }

            var chance = Math.Clamp(baseChance + CancelDelta(owner), 0.0, 1.0);
            if (random.NextDouble() >= chance)
            {
                continue;
            }

            CancelBuy(order, owner, currentCycleId, now);
        }
    }

    private async Task ReviseSellOrdersAsync(int companyId, DateTime now)
    {
        var orders = await dbContext.Orders
            .Where(order => order.ParticipantId != null
                && order.CompanyId == companyId
                && order.Type == OrderType.Sell
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .OrderBy(order => order.Id)
            .ToListAsync();
        if (orders.Count == 0)
        {
            return;
        }

        var ownerIds = orders.Select(order => order.ParticipantId!.Value).Distinct().ToList();
        var ownersById = await dbContext.Participants
            .Where(participant => ownerIds.Contains(participant.Id))
            .ToDictionaryAsync(participant => participant.Id);

        foreach (var order in orders)
        {
            var owner = ownersById[order.ParticipantId!.Value];
            if (owner.Type == ParticipantType.Player || owner.IsBankrupt)
            {
                continue;
            }

            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = now;
        }
    }

    private static double CancelDelta(Participant owner) =>
        (owner.RiskProfile == RiskProfile.Low ? LowRiskCancelDelta : 0.0)
        + (owner.RiskProfile == RiskProfile.High ? HighRiskCancelDelta : 0.0)
        + (owner.Temperament == Temperament.Conservative ? ConservativeCancelDelta : 0.0);

    private void CancelBuy(Order order, Participant owner, int currentCycleId, DateTime now)
    {
        if (order.ReservedCashAmount > 0m)
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

    private static decimal LatestPrice(List<(int CycleNumber, decimal Price)>? snapshots) =>
        snapshots is { Count: > 0 } ? snapshots[^1].Price : 0m;

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
