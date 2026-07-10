using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Industries are processed by id and consume exactly one roll each, keeping scripted market simulations reproducible.
public sealed class IndustrySentimentService(
    AppDbContext dbContext,
    IOptions<IndustrySentimentOptions> options,
    IOptions<RandomChanceRatesOptions> chanceRates,
    Random random)
{
    private const int BaseStep = 5;
    private const int CrisisHitPush = 15;

    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, Crisis? activeCrisis = null)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var industries = await dbContext.Industries.OrderBy(industry => industry.Id).ToListAsync();
        var crisisIndustryIds = activeCrisis is null
            ? []
            : await dbContext.CrisisIndustries
                .Where(link => link.CrisisId == activeCrisis.Id)
                .Select(link => link.IndustryId)
                .ToHashSetAsync();
        var previousCycleId = await dbContext.MarketCycles
            .Where(cycle => cycle.CycleNumber == currentCycleNumber - 1)
            .Select(cycle => (int?)cycle.Id)
            .SingleOrDefaultAsync();
        var newsEvidence = previousCycleId is null
            ? []
            : await dbContext.NewsPosts
                .Where(post => post.Scope == NewsImpactScope.Company
                    && post.ImpactAppliedInCycleId == previousCycleId
                    && post.TargetCompanyId != null
                    && post.Direction != null)
                .Join(
                    dbContext.Companies,
                    post => post.TargetCompanyId,
                    company => (int?)company.Id,
                    (post, company) => new { company.IndustryId, Direction = post.Direction!.Value })
                .ToListAsync();
        var positiveNewsIndustryIds = newsEvidence
            .Where(item => item.Direction == NewsImpactDirection.Increase)
            .Select(item => item.IndustryId)
            .ToHashSet();
        var negativeNewsIndustryIds = newsEvidence
            .Where(item => item.Direction == NewsImpactDirection.Decrease)
            .Select(item => item.IndustryId)
            .ToHashSet();
        var revisionBase = chanceRates.Value.EventTriggerChances.IndustrySentimentRevisionBase;
        var volatilityFactor = chanceRates.Value.ChanceModifiers.IndustrySentimentVolatilityFactor;
        var crisisForcedDown = chanceRates.Value.ChanceModifiers.CrisisSentimentForcedDown;
        var companyNewsBonus = chanceRates.Value.ChanceModifiers.CompanyNewsSentimentBonus;
        var limit = Math.Max(0, options.Value.SentimentValueLimit);
        var decay = Math.Max(0, options.Value.SentimentDecayPerCycle);

        foreach (var industry in industries)
        {
            var roll = random.NextDouble();
            // More volatile sectors experience wider cyclical mood swings even when the market is calm.
            var revisionChance = revisionBase * (1d + ((double)industry.SentimentVolatility * volatilityFactor));
            var upDamping = limit > 0
                ? Math.Clamp((limit - (double)industry.SentimentValue) / limit, 0d, 1d)
                : 0d;
            var downDamping = limit > 0
                ? Math.Clamp((limit + (double)industry.SentimentValue) / limit, 0d, 1d)
                : 0d;
            // An active crisis keeps confidence risk-off across the whole market, including sectors outside the initial shock.
            var effectiveUp = activeCrisis is null ? revisionChance * upDamping : 0d;
            var effectiveDown = activeCrisis is null
                ? revisionChance * downDamping
                : crisisForcedDown * downDamping;
            var hasPositiveNews = positiveNewsIndustryIds.Contains(industry.Id);
            var hasNegativeNews = negativeNewsIndustryIds.Contains(industry.Id);
            if (activeCrisis is null && hasPositiveNews && !hasNegativeNews)
            {
                effectiveUp += companyNewsBonus;
            }
            else if (hasNegativeNews && !hasPositiveNews)
            {
                effectiveDown += companyNewsBonus;
            }

            var upThreshold = Math.Clamp(effectiveUp, 0d, 1d);
            var downThreshold = Math.Clamp(
                upThreshold + Math.Max(0d, effectiveDown),
                upThreshold,
                1d);
            var value = (long)industry.SentimentValue;

            if (roll < upThreshold)
            {
                value += BaseStep;
            }
            else if (roll < downThreshold)
            {
                value -= BaseStep;
            }

            if (crisisIndustryIds.Contains(industry.Id))
            {
                value -= CrisisHitPush;
            }

            // Confidence fades without fresh evidence, keeping sector mood from becoming permanently anchored.
            value -= Math.Sign(value) * Math.Min((long)decay, Math.Abs(value));
            industry.SentimentValue = (int)Math.Clamp(value, -(long)limit, limit);
        }

        await dbContext.SaveChangesAsync();
    }
}
