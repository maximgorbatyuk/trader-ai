using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// A cycle-based port of index concentration caps (e.g. the Nasdaq-100 special rebalance): a single company
// worth more than MaxSingleCompanyWeightPercent of total market capitalisation is over-concentrated and has
// its price cut PriceCutPercent via MarketImpactService, with a company-scoped news post recording the drop.
// The cut is iterative by design — one cut may not clear the cap in a cycle (and cutting one company lowers
// the total, nudging the others up), so a persistent giant is cut again next cycle until it falls back under.
//
// Runs just before the auditor in the pre-match window, so bankruptcy, funds, and exits all read pre-cut
// prices while the cut takes effect for this cycle's decisions and matching. Deterministic like a stock
// split: no Random, nothing drawn — companies are walked in ascending id order. Stages changes; the caller
// owns the save.
public sealed class ConcentrationCapService(
    AppDbContext dbContext,
    IOptions<ConcentrationCapOptions> options,
    MarketImpactService marketImpact)
{
    public async Task ProcessForCycleAsync(int currentCycleId, DateTime now)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            return;
        }

        var liveCompanies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null)
            .OrderBy(company => company.Id)
            .ToListAsync();
        if (liveCompanies.Count == 0)
        {
            return;
        }

        var latestPriceByCompany = await PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);
        var capByCompany = liveCompanies.ToDictionary(
            company => company.Id,
            company => latestPriceByCompany.GetValueOrDefault(company.Id) * company.IssuedSharesCount);

        var totalMarketCap = capByCompany.Values.Sum();
        if (totalMarketCap <= 0m)
        {
            return;
        }

        // The threshold scales with total capitalisation, so it stays meaningful as the whole market grows.
        var threshold = totalMarketCap * (opts.MaxSingleCompanyWeightPercent / 100m);

        foreach (var company in liveCompanies)
        {
            if (capByCompany.GetValueOrDefault(company.Id) <= threshold)
            {
                continue;
            }

            await CutAsync(company, opts.PriceCutPercent, currentCycleId, now);
        }
    }

    private async Task CutAsync(Company company, decimal cutPercent, int currentCycleId, DateTime now)
    {
        await marketImpact.ApplyImpactAsync(
            NewsImpactDirection.Decrease, [company.Id], cutPercent, currentCycleId, now);
        company.UpdatedAt = now;

        dbContext.NewsPosts.Add(new NewsPost
        {
            Title = $"{company.Name} trimmed for its outsized market weight",
            Content = $"{company.Name} had grown into an outsized share of the whole market, so its price is cut {cutPercent:N0}% to bring its weight back toward the rest of the field.",
            PublishedInCycleId = currentCycleId,
            ImpactAppliedInCycleId = currentCycleId,
            PublishedAt = now,
            Scope = NewsImpactScope.Company,
            Direction = NewsImpactDirection.Decrease,
            ImpactPercent = cutPercent,
            TargetCompanyId = company.Id,
        });
    }
}
