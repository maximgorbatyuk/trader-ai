using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record PublishNewsResult(bool Published, NewsPost? Post, int CompaniesMoved)
{
    public static PublishNewsResult Skipped() => new(false, null, 0);
}

public sealed record ManualNewsRequest(
    NewsImpactScope Scope,
    string ThemeKey,
    NewsImpactDirection Direction,
    decimal ImpactPercent,
    int? TargetCompanyId,
    int[]? IndustryIds);

public sealed record ManualNewsResult(bool Success, NewsPost? Post, string? Error)
{
    public static ManualNewsResult Ok(NewsPost post) => new(true, post, null);

    public static ManualNewsResult Fail(string error) => new(false, null, error);
}

// Composes news immediately but applies its price reaction after the cycle's matching has settled, so the
// headline cannot trade against orders that were already on the book when it arrived.
public sealed class NewsService(
    AppDbContext dbContext,
    MarketCycleLock cycleLock,
    IOptions<NewsOptions> options,
    IOptions<RandomChanceRatesOptions> chanceRates,
    MarketImpactService marketImpact,
    Random random,
    IOptions<IndustrySentimentOptions>? industrySentimentOptions = null)
{
    private const decimal MinImpactPercent = 0.1m;

    // Automated posts stay gentle; a manually created post can specify a much larger swing.
    private const decimal MaxAutomatedImpactPercent = 10m;
    private const decimal MaxManualImpactPercent = 95m;

    // Company news ripples to the rest of the target's industry at a fraction of the headline move — a
    // sympathy move for its peers, in the same direction.
    private const decimal IndustrySpilloverFraction = 0.25m;
    private const int IndustryNewsSentimentNudge = 10;

    private readonly IndustrySentimentOptions industrySentimentOptionValues =
        industrySentimentOptions?.Value ?? new IndustrySentimentOptions();

    // Called from the cycle advance, which already holds the lock and owns the surrounding save; this only
    // adds entities to the shared context when an automated post is due.
    public async Task<PublishNewsResult> MaybeAddAutomatedNewsForCycleAsync(
        MarketCycle currentCycle, DateTime now, bool duringCrisis = false)
    {
        var settings = options.Value;
        if (!settings.Enabled || settings.CyclesBetweenPosts <= 0)
        {
            return PublishNewsResult.Skipped();
        }

        if (currentCycle.CycleNumber % settings.CyclesBetweenPosts != 0)
        {
            return PublishNewsResult.Skipped();
        }

        var genericContent = DemoNewsContent.Generate(random);
        var post = new NewsPost
        {
            Title = genericContent.Title,
            Content = genericContent.Content,
            PublishedInCycleId = currentCycle.Id,
            PublishedAt = now,
            Scope = NewsImpactScope.None,
        };

        if (random.NextDouble() < chanceRates.Value.EventTriggerChances.NewsImpact)
        {
            await ConfigureRandomImpactAsync(post, settings, duringCrisis);
        }

        var content = post.Scope is NewsImpactScope.Company or NewsImpactScope.Industries
            && post.Direction is NewsImpactDirection direction
            ? DemoNewsContent.GenerateForScopedDirection(direction, new Random(ScopedContentSeed(post)))
            : genericContent;
        post.Title = content.Title;
        post.Content = content.Content;

        dbContext.NewsPosts.Add(post);
        return new PublishNewsResult(true, post, 0);
    }

    public async Task<ManualNewsResult> PublishManualNewsAsync(ManualNewsRequest request)
    {
        await cycleLock.Semaphore.WaitAsync();
        try
        {
            return await PublishManualNewsCoreAsync(request);
        }
        finally
        {
            cycleLock.Semaphore.Release();
        }
    }

    private async Task<ManualNewsResult> PublishManualNewsCoreAsync(ManualNewsRequest request)
    {
        if (request.Scope is not (NewsImpactScope.Company or NewsImpactScope.Industries))
        {
            return ManualNewsResult.Fail("Scope must be Company or Industries.");
        }

        if (request.Direction is not (NewsImpactDirection.Increase or NewsImpactDirection.Decrease))
        {
            return ManualNewsResult.Fail("Direction must be Increase or Decrease.");
        }

        var percent = Round(request.ImpactPercent);
        if (percent < MinImpactPercent || percent > MaxManualImpactPercent)
        {
            return ManualNewsResult.Fail($"Impact percent must be between {MinImpactPercent} and {MaxManualImpactPercent}.");
        }

        var content = DemoNewsContent.GenerateForScopedTheme(request.ThemeKey, request.Direction, random);
        if (content is null)
        {
            return ManualNewsResult.Fail("Unknown finance theme.");
        }

        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market?.CurrentCycleId is not int cycleId)
        {
            return ManualNewsResult.Fail("Market is not ready.");
        }

        var now = DateTime.UtcNow;
        var post = new NewsPost
        {
            Title = content.Value.Title,
            Content = content.Value.Content,
            PublishedInCycleId = cycleId,
            PublishedAt = now,
            Scope = request.Scope,
            Direction = request.Direction,
            ImpactPercent = percent,
        };

        if (request.Scope == NewsImpactScope.Company)
        {
            if (request.TargetCompanyId is not int targetId
                || !await dbContext.Companies.AnyAsync(company => company.Id == targetId))
            {
                return ManualNewsResult.Fail("Target company not found.");
            }

            post.TargetCompanyId = targetId;
        }
        else
        {
            var requestedIds = (request.IndustryIds ?? []).Distinct().ToList();
            if (requestedIds.Count == 0)
            {
                return ManualNewsResult.Fail("Select at least one industry.");
            }

            var validIds = await dbContext.Industries
                .Where(industry => requestedIds.Contains(industry.Id))
                .Select(industry => industry.Id)
                .ToListAsync();
            if (validIds.Count == 0)
            {
                return ManualNewsResult.Fail("No valid industries selected.");
            }

            foreach (var industryId in validIds)
            {
                post.Industries.Add(new NewsPostIndustry { IndustryId = industryId });
            }

        }

        dbContext.NewsPosts.Add(post);
        await dbContext.SaveChangesAsync();
        return ManualNewsResult.Ok(post);
    }

    // Preserves the automated composer's random decisions while leaving price formation to the end-of-cycle
    // application pass, where all same-cycle headlines react together after matching.
    private async Task ConfigureRandomImpactAsync(
        NewsPost post, NewsOptions settings, bool duringCrisis)
    {
        var direction = random.Next(2) == 0 ? NewsImpactDirection.Increase : NewsImpactDirection.Decrease;

        // During a crisis, half of would-be price-lifting posts become impact-free; the extra draw is taken
        // only on that branch, so the calm-market path keeps its original draw sequence.
        if (duringCrisis
            && direction == NewsImpactDirection.Increase
            && random.NextDouble() < chanceRates.Value.ChanceModifiers.CrisisNewsIncreaseSuppression)
        {
            ClearImpact(post);
            return;
        }

        var percent = RandomImpactPercent();
        post.Direction = direction;
        post.ImpactPercent = percent;

        if (random.NextDouble() < chanceRates.Value.EventTriggerChances.NewsCompanyScope)
        {
            var companyIds = await dbContext.Companies.Select(company => company.Id).ToListAsync();
            if (companyIds.Count == 0)
            {
                ClearImpact(post);
                return;
            }

            post.Scope = NewsImpactScope.Company;
            var companyId = companyIds[random.Next(companyIds.Count)];
            post.TargetCompanyId = companyId;
            return;
        }

        var industryIds = await dbContext.Industries.Select(industry => industry.Id).ToListAsync();
        if (industryIds.Count == 0)
        {
            ClearImpact(post);
            return;
        }

        post.Scope = NewsImpactScope.Industries;
        var wanted = Math.Min(random.Next(1, settings.MaxIndustriesPerPost + 1), industryIds.Count);
        var chosen = PickDistinct(industryIds, wanted);
        foreach (var industryId in chosen)
        {
            post.Industries.Add(new NewsPostIndustry { IndustryId = industryId });
        }

    }

    // Applies only posts born in this completed cycle; older unmarked rows are historical data rather than
    // delayed work, so filtering them out prevents migrations from replaying past market shocks.
    public async Task<int> ApplyPendingImpactsForCycleAsync(MarketCycle currentCycle, DateTime now)
    {
        var posts = await dbContext.NewsPosts
            .Include(post => post.Industries)
            .Where(post => post.PublishedInCycleId == currentCycle.Id
                && post.ImpactAppliedInCycleId == null
                && (post.Scope == NewsImpactScope.Company || post.Scope == NewsImpactScope.Industries)
                && (post.Direction == NewsImpactDirection.Increase || post.Direction == NewsImpactDirection.Decrease)
                && post.ImpactPercent != null
                && post.ImpactPercent >= MinImpactPercent)
            .ToListAsync();

        var moved = 0;
        foreach (var post in posts)
        {
            var direction = post.Direction!.Value;
            var percent = post.ImpactPercent!.Value;
            if (post.Scope == NewsImpactScope.Company && post.TargetCompanyId is int targetCompanyId)
            {
                moved += await ApplyCompanyImpactAsync(direction, targetCompanyId, percent, currentCycle.Id, now);
            }
            else if (post.Scope == NewsImpactScope.Industries)
            {
                var industryIds = post.Industries.Select(link => link.IndustryId).Distinct().ToList();
                var companyIds = await dbContext.Companies
                    .Where(company => industryIds.Contains(company.IndustryId))
                    .Select(company => company.Id)
                    .ToListAsync();
                moved += await marketImpact.ApplyImpactAsync(
                    direction,
                    companyIds,
                    percent,
                    currentCycle.Id,
                    now,
                    applySectorSentiment: industrySentimentOptionValues.Enabled);
                if (industrySentimentOptionValues.Enabled)
                {
                    await NudgeIndustriesAsync(industryIds, direction);
                }
            }

            post.ImpactAppliedInCycleId = currentCycle.Id;
        }

        return moved;
    }

    private async Task NudgeIndustriesAsync(IReadOnlyCollection<int> industryIds, NewsImpactDirection direction)
    {
        var limit = Math.Max(0, industrySentimentOptionValues.SentimentValueLimit);
        var shift = direction == NewsImpactDirection.Increase ? IndustryNewsSentimentNudge : -IndustryNewsSentimentNudge;
        var industries = await dbContext.Industries.Where(industry => industryIds.Contains(industry.Id)).ToListAsync();
        foreach (var industry in industries)
        {
            industry.SentimentValue = (int)Math.Clamp((long)industry.SentimentValue + shift, -(long)limit, limit);
        }
    }

    // Company-scoped impact moves the target by the full percent and every same-industry peer by a fraction
    // of it (a sympathy move), all in the same direction. Draws no randomness, so it is safe inside the
    // scripted-Random automated path.
    private async Task<int> ApplyCompanyImpactAsync(
        NewsImpactDirection direction, int targetId, decimal percent, int cycleId, DateTime now)
    {
        var industryId = await dbContext.Companies
            .Where(company => company.Id == targetId)
            .Select(company => (int?)company.IndustryId)
            .FirstOrDefaultAsync();
        if (industryId is null)
        {
            return 0;
        }

        var moved = await marketImpact.ApplyImpactAsync(
            direction,
            [targetId],
            percent,
            cycleId,
            now,
            applySectorSentiment: industrySentimentOptionValues.Enabled);

        var peerIds = await dbContext.Companies
            .Where(company => company.IndustryId == industryId.Value && company.Id != targetId)
            .Select(company => company.Id)
            .ToListAsync();

        var spilloverPercent = Round(percent * IndustrySpilloverFraction);
        if (peerIds.Count > 0 && spilloverPercent > 0m)
        {
            moved += await marketImpact.ApplyImpactAsync(
                direction,
                peerIds,
                spilloverPercent,
                cycleId,
                now,
                applySectorSentiment: industrySentimentOptionValues.Enabled);
        }

        return moved;
    }

    private static void ClearImpact(NewsPost post)
    {
        post.Scope = NewsImpactScope.None;
        post.Direction = null;
        post.ImpactPercent = null;
    }

    private static int ScopedContentSeed(NewsPost post)
    {
        var seed = 17;
        seed = unchecked((seed * 31) + post.PublishedInCycleId);
        seed = unchecked((seed * 31) + (int)post.Scope);
        seed = unchecked((seed * 31) + (int)post.Direction!.Value);
        seed = unchecked((seed * 31) + post.TargetCompanyId.GetValueOrDefault());
        foreach (var industryId in post.Industries.Select(link => link.IndustryId).OrderBy(id => id))
        {
            seed = unchecked((seed * 31) + industryId);
        }

        return seed;
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
        Round(MinImpactPercent + ((decimal)random.NextDouble() * (MaxAutomatedImpactPercent - MinImpactPercent)));

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
