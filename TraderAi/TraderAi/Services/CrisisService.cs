using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record TriggerCrisisResult(bool Triggered, IReadOnlyList<Crisis> Crises)
{
    public static TriggerCrisisResult None() => new(false, []);
}

// Rolls the chance of a market crisis once per cycle advance and applies it when it fires. Two independent
// shocks ramp up the longer the market goes without one: a local crisis (a few sectors) after a short quiet
// stretch, and a rarer global crisis (a large share of sectors) after a long one. Each resets its own clock
// when it fires. Called from inside the cycle advance, which already holds the lock and owns the save, so
// this only stages changes on the shared context.
public sealed class CrisisService(
    AppDbContext dbContext,
    IOptions<CrisisOptions> options,
    MarketImpactService marketImpact,
    Random random)
{
    // Local: no chance for the first 100 cycles since the last one, then the chance climbs 3 points a cycle.
    private const int LocalQuietCycles = 100;
    private const double LocalStepPerCycle = 0.03;
    private const int LocalMinIndustries = 1;
    private const int LocalMaxIndustries = 3;
    private const int LocalDurationCycles = 10;

    // Global: no chance for the first 250 cycles since the last one, then the chance climbs 1 point a cycle.
    private const int GlobalQuietCycles = 250;
    private const double GlobalStepPerCycle = 0.01;
    private const double GlobalMinIndustryShare = 0.30;
    private const double GlobalMaxIndustryShare = 0.70;
    private const int GlobalDurationCycles = 20;

    // Every affected industry drops by its own draw in this band.
    private const decimal MinImpactPercent = 5m;
    private const decimal MaxImpactPercent = 15m;

    // The crisis whose window covers the current cycle, or null when the market is calm. While one is active,
    // auditors and bankruptcies bite harder and price-lifting events land less often. When two windows overlap
    // (independent scope clocks), the most recently triggered one wins so new events attach to it.
    public Task<Crisis?> GetActiveCrisisAsync(int currentCycleNumber) =>
        dbContext.Crises
            .Where(crisis => currentCycleNumber > crisis.TriggeredInCycleNumber
                && currentCycleNumber <= crisis.TriggeredInCycleNumber + crisis.DurationCycles)
            .OrderByDescending(crisis => crisis.TriggeredInCycleNumber)
            .FirstOrDefaultAsync();

    public async Task<TriggerCrisisResult> MaybeTriggerForCycleAsync(Market market, MarketCycle currentCycle, DateTime now)
    {
        if (!options.Value.Enabled)
        {
            return TriggerCrisisResult.None();
        }

        Crisis? crisis = null;

        // Global is checked first; a global shock already sweeps most sectors, so a local one is skipped the
        // same cycle to avoid stacking two crises in one tick.
        if (ShouldTrigger(currentCycle.CycleNumber, market.LastGlobalCrisisCycleNumber, GlobalQuietCycles, GlobalStepPerCycle))
        {
            crisis = await TriggerAsync(CrisisScope.Global, currentCycle, now);
            if (crisis is not null)
            {
                market.LastGlobalCrisisCycleNumber = currentCycle.CycleNumber;
            }
        }
        else if (ShouldTrigger(currentCycle.CycleNumber, market.LastLocalCrisisCycleNumber, LocalQuietCycles, LocalStepPerCycle))
        {
            crisis = await TriggerAsync(CrisisScope.Local, currentCycle, now);
            if (crisis is not null)
            {
                market.LastLocalCrisisCycleNumber = currentCycle.CycleNumber;
            }
        }

        return crisis is null ? TriggerCrisisResult.None() : new TriggerCrisisResult(true, [crisis]);
    }

    private bool ShouldTrigger(int currentCycleNumber, int lastCrisisCycleNumber, int quietCycles, double stepPerCycle)
    {
        var cyclesSince = currentCycleNumber - lastCrisisCycleNumber;
        var probability = Math.Clamp((cyclesSince - quietCycles) * stepPerCycle, 0d, 1d);
        // A draw is always consumed, even at zero chance, so a scripted Random in tests stays in lockstep.
        return random.NextDouble() < probability;
    }

    private async Task<Crisis?> TriggerAsync(CrisisScope scope, MarketCycle currentCycle, DateTime now)
    {
        var industryIds = await dbContext.Industries.Select(industry => industry.Id).ToListAsync();
        if (industryIds.Count == 0)
        {
            return null;
        }

        var count = scope == CrisisScope.Global
            ? GlobalIndustryCount(industryIds.Count)
            : Math.Min(random.Next(LocalMinIndustries, LocalMaxIndustries + 1), industryIds.Count);

        var chosen = PickDistinct(industryIds, count);
        if (chosen.Count == 0)
        {
            return null;
        }

        var (title, content) = DemoCrisisContent.Generate(scope, random);
        var crisis = new Crisis
        {
            Title = title,
            Content = content,
            Scope = scope,
            TriggeredInCycleId = currentCycle.Id,
            TriggeredInCycleNumber = currentCycle.CycleNumber,
            DurationCycles = scope == CrisisScope.Global ? GlobalDurationCycles : LocalDurationCycles,
            TriggeredAt = now,
        };

        var industryNameById = await dbContext.Industries
            .Where(industry => chosen.Contains(industry.Id))
            .ToDictionaryAsync(industry => industry.Id, industry => industry.Name);

        foreach (var industryId in chosen)
        {
            var percent = RandomImpactPercent();
            crisis.Industries.Add(new CrisisIndustry { IndustryId = industryId, ImpactPercent = percent });

            // The trigger shock opens the crisis timeline; auditor and bankruptcy events append to it later.
            crisis.Events.Add(new CrisisEvent
            {
                Type = CrisisEventType.IndustryShock,
                Description = $"{industryNameById.GetValueOrDefault(industryId) ?? $"Industry #{industryId}"} shocked",
                IndustryId = industryId,
                ImpactPercent = percent,
                CreatedInCycleId = currentCycle.Id,
                CreatedInCycleNumber = currentCycle.CycleNumber,
                CreatedAt = now,
            });

            var companyIds = await dbContext.Companies
                .Where(company => company.IndustryId == industryId)
                .Select(company => company.Id)
                .ToListAsync();

            await marketImpact.ApplyImpactAsync(NewsImpactDirection.Decrease, companyIds, percent, currentCycle.Id, now);
        }

        dbContext.Crises.Add(crisis);
        return crisis;
    }

    private int GlobalIndustryCount(int total)
    {
        var share = GlobalMinIndustryShare + (random.NextDouble() * (GlobalMaxIndustryShare - GlobalMinIndustryShare));
        var count = (int)Math.Round(total * share, MidpointRounding.AwayFromZero);
        return Math.Clamp(count, 1, total);
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

    private decimal RandomImpactPercent() =>
        Math.Round(
            MinImpactPercent + ((decimal)random.NextDouble() * (MaxImpactPercent - MinImpactPercent)),
            2,
            MidpointRounding.AwayFromZero);
}
