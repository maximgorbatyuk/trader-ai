using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record TriggerScienceResult(bool Triggered, ScienceInvestigation? Investigation)
{
    public static TriggerScienceResult None() => new(false, null);
}

// Science investigations are the upbeat counterpart to a crisis and defer persistence to the cycle advance
// so one transaction keeps their price response and headline inseparable.
// When sentiment is enabled, each selected industry consumes one push roll after price work to keep scripted
// simulations reproducible; disabled mode leaves the legacy draw sequence intact.
public sealed class ScienceInvestigationService(
    AppDbContext dbContext,
    IOptions<ScienceInvestigationOptions> options,
    IOptions<RandomChanceRatesOptions> chanceRates,
    MarketImpactService marketImpact,
    Random random,
    IOptions<IndustrySentimentOptions>? industrySentimentOptions = null)
{
    // No chance for the first 50 cycles since the last one, then the chance climbs by the configured step a cycle.
    private const int QuietCycles = 50;
    private const int MinIndustries = 1;
    private const int MaxIndustries = 5;
    private const int SentimentPush = 15;

    private readonly IndustrySentimentOptions industrySentimentOptionValues =
        industrySentimentOptions?.Value ?? new IndustrySentimentOptions();

    public async Task<TriggerScienceResult> MaybeTriggerForCycleAsync(
        Market market, MarketCycle currentCycle, DateTime now, bool duringCrisis = false)
    {
        if (!options.Value.Enabled)
        {
            return TriggerScienceResult.None();
        }

        if (!ShouldTrigger(currentCycle.CycleNumber, market.LastScienceInvestigationCycleNumber, duringCrisis))
        {
            return TriggerScienceResult.None();
        }

        var investigation = await TriggerAsync(currentCycle, now);
        if (investigation is null)
        {
            return TriggerScienceResult.None();
        }

        market.LastScienceInvestigationCycleNumber = currentCycle.CycleNumber;
        return new TriggerScienceResult(true, investigation);
    }

    private bool ShouldTrigger(int currentCycleNumber, int lastCycleNumber, bool duringCrisis)
    {
        var cyclesSince = currentCycleNumber - lastCycleNumber;
        var probability = Math.Clamp((cyclesSince - QuietCycles) * chanceRates.Value.EventTriggerChances.ScienceStepPerCycle, 0d, 1d);
        if (duringCrisis)
        {
            probability *= chanceRates.Value.ChanceModifiers.CrisisScienceChanceFactor;
        }

        // A draw is always consumed, even at zero chance, so a scripted Random in tests stays in lockstep.
        return random.NextDouble() < probability;
    }

    private async Task<ScienceInvestigation?> TriggerAsync(MarketCycle currentCycle, DateTime now)
    {
        var industryIds = await dbContext.Industries.Select(industry => industry.Id).ToListAsync();
        if (industryIds.Count == 0)
        {
            return null;
        }

        var count = Math.Min(random.Next(MinIndustries, MaxIndustries + 1), industryIds.Count);
        var chosen = PickDistinct(industryIds, count);
        if (chosen.Count == 0)
        {
            return null;
        }

        var (title, content) = DemoScienceContent.Generate(random);
        var investigation = new ScienceInvestigation
        {
            Title = title,
            Content = content,
            TriggeredInCycleId = currentCycle.Id,
            TriggeredAt = now,
        };

        foreach (var industryId in chosen)
        {
            var percent = RandomImpactPercent();
            investigation.Industries.Add(new ScienceInvestigationIndustry { IndustryId = industryId, ImpactPercent = percent });

            var companyIds = await dbContext.Companies
                .Where(company => company.IndustryId == industryId)
                .Select(company => company.Id)
                .ToListAsync();

            await marketImpact.ApplyImpactAsync(
                NewsImpactDirection.Increase,
                companyIds,
                percent,
                currentCycle.Id,
                now,
                cancelStaleOrders: false,
                applySectorSentiment: industrySentimentOptionValues.Enabled);

            if (industrySentimentOptionValues.Enabled
                && random.NextDouble() < chanceRates.Value.EventTriggerChances.ScienceSentimentPush)
            {
                var industry = await dbContext.Industries.SingleAsync(industry => industry.Id == industryId);
                var limit = Math.Max(0, industrySentimentOptionValues.SentimentValueLimit);
                industry.SentimentValue = (int)Math.Clamp(
                    (long)industry.SentimentValue + SentimentPush,
                    -(long)limit,
                    limit);
            }
        }

        dbContext.ScienceInvestigations.Add(investigation);
        return investigation;
    }

    private List<int> PickDistinct(List<int> source, int count)
    {
        var pool = new List<int>(source);
        var picked = new List<int>(count);
        for (var index = 0; index < count && pool.Count > 0; index++)
        {
            var swap = random.Next(pool.Count);
            picked.Add(pool[swap]);
            pool[swap] = pool[^1];
            pool.RemoveAt(pool.Count - 1);
        }

        return picked;
    }

    private decimal RandomImpactPercent()
    {
        var bands = chanceRates.Value.RandomMagnitudeBands;
        return Math.Round(
            bands.ScienceIndustryLiftMinPercent
                + ((decimal)random.NextDouble() * (bands.ScienceIndustryLiftMaxPercent - bands.ScienceIndustryLiftMinPercent)),
            2,
            MidpointRounding.AwayFromZero);
    }
}
