using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record TriggerCrisisResult(bool Triggered, IReadOnlyList<Crisis> Crises)
{
    public static TriggerCrisisResult None() => new(false, []);
}

// Rolls the chance of a market crisis once per trading day and applies it when it fires. Two independent
// shocks ramp up the longer the market goes without one: a local crisis (a few sectors) after a short quiet
// stretch, and a rarer global crisis (a large share of sectors) after a long one. Each resets its own clock
// when it fires. Called from inside the cycle advance on the first cycle of each trading day, which already
// holds the lock and owns the save, so this only stages changes on the shared context.
public sealed class CrisisService(
    AppDbContext dbContext,
    IOptions<CrisisOptions> options,
    IOptions<RandomChanceRatesOptions> chanceRates,
    MarketImpactService marketImpact,
    Random random,
    IOptions<TradingClockOptions> tradingClockOptions,
    IOptions<IndustrySentimentOptions>? industrySentimentOptions = null)
{
    private readonly IndustrySentimentOptions industrySentimentOptionValues =
        industrySentimentOptions?.Value ?? new IndustrySentimentOptions();

    // Elapsed cycles are converted to trading days against this length so the quiet windows and per-day step
    // are measured in trading days rather than raw cycles.
    private readonly int cyclesPerTradingDay = Math.Max(1, tradingClockOptions.Value.TradingCyclesPerDay);

    // Local: no chance for the first 10 trading days since the last one, then the chance climbs by the configured step.
    private const int LocalQuietDays = 10;
    private const int LocalMinIndustries = 1;
    private const int LocalMaxIndustries = 3;
    private const int LocalMinDurationCycles = 5;
    private const int LocalMaxDurationCycles = 15;

    // Global: no chance for the first 40 trading days since the last one, then the chance climbs by the configured step.
    private const int GlobalQuietDays = 40;
    private const int GlobalMinDurationCycles = 15;
    private const int GlobalMaxDurationCycles = 25;

    // The crisis whose window covers the current cycle, or null when the market is calm. While one is active,
    // bankruptcies bite harder and price-lifting events land less often. When two windows overlap
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
        if (ShouldTrigger(currentCycle.CycleNumber, market.LastGlobalCrisisCycleNumber, GlobalQuietDays, chanceRates.Value.EventTriggerChances.GlobalCrisisStepPerTradingDay))
        {
            crisis = await TriggerAsync(CrisisScope.Global, currentCycle, now);
            if (crisis is not null)
            {
                market.LastGlobalCrisisCycleNumber = currentCycle.CycleNumber;
            }
        }
        else if (ShouldTrigger(currentCycle.CycleNumber, market.LastLocalCrisisCycleNumber, LocalQuietDays, chanceRates.Value.EventTriggerChances.LocalCrisisStepPerTradingDay))
        {
            crisis = await TriggerAsync(CrisisScope.Local, currentCycle, now);
            if (crisis is not null)
            {
                market.LastLocalCrisisCycleNumber = currentCycle.CycleNumber;
            }
        }

        return crisis is null ? TriggerCrisisResult.None() : new TriggerCrisisResult(true, [crisis]);
    }

    private bool ShouldTrigger(int currentCycleNumber, int lastCrisisCycleNumber, int quietDays, double stepPerTradingDay)
    {
        var daysSince = (currentCycleNumber - lastCrisisCycleNumber) / cyclesPerTradingDay;
        var probability = Math.Clamp((daysSince - quietDays) * stepPerTradingDay, 0d, 1d);
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

        // One draw for the active-window length, right after the content draws: Local 5–15 cycles, Global 15–25.
        var durationCycles = scope == CrisisScope.Global
            ? random.Next(GlobalMinDurationCycles, GlobalMaxDurationCycles + 1)
            : random.Next(LocalMinDurationCycles, LocalMaxDurationCycles + 1);

        var crisis = new Crisis
        {
            Title = title,
            Content = content,
            Scope = scope,
            TriggeredInCycleId = currentCycle.Id,
            TriggeredInCycleNumber = currentCycle.CycleNumber,
            DurationCycles = durationCycles,
            TriggeredAt = now,
        };

        var industryNameById = await dbContext.Industries
            .Where(industry => chosen.Contains(industry.Id))
            .ToDictionaryAsync(industry => industry.Id, industry => industry.Name);

        foreach (var industryId in chosen)
        {
            var percent = RandomImpactPercent();
            crisis.Industries.Add(new CrisisIndustry { IndustryId = industryId, ImpactPercent = percent });

            // The trigger shock opens the crisis timeline; bankruptcies and closures append to it later.
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

            await marketImpact.ApplyImpactAsync(
                NewsImpactDirection.Decrease,
                companyIds,
                percent,
                currentCycle.Id,
                now,
                applySectorSentiment: industrySentimentOptionValues.Enabled);
        }

        dbContext.Crises.Add(crisis);
        return crisis;
    }

    private int GlobalIndustryCount(int total)
    {
        var bands = chanceRates.Value.RandomMagnitudeBands;
        var share = bands.GlobalCrisisIndustryShareMin
            + (random.NextDouble() * (bands.GlobalCrisisIndustryShareMax - bands.GlobalCrisisIndustryShareMin));
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

    private decimal RandomImpactPercent()
    {
        var bands = chanceRates.Value.RandomMagnitudeBands;
        return Math.Round(
            bands.CrisisIndustryDropMinPercent
                + ((decimal)random.NextDouble() * (bands.CrisisIndustryDropMaxPercent - bands.CrisisIndustryDropMinPercent)),
            2,
            MidpointRounding.AwayFromZero);
    }
}
