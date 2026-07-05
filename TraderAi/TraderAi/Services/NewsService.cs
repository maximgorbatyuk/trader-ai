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

// Creates news posts and applies their market impact. Price is moved the same way the matching engine
// moves it: by inserting a new PriceSnapshot, which every read of "current price" derives from. Automated
// posts are generated once every N cycles from inside the cycle advance (already under the cycle lock, so
// they must not re-lock or save); manual posts come from the API and take the lock themselves.
public sealed class NewsService(
    AppDbContext dbContext,
    MarketCycleLock cycleLock,
    IOptions<NewsOptions> options,
    MarketImpactService marketImpact,
    Random random)
{
    private const decimal MinImpactPercent = 0.1m;

    // Automated posts stay gentle; a manually created post can specify a much larger swing.
    private const decimal MaxAutomatedImpactPercent = 10m;
    private const decimal MaxManualImpactPercent = 95m;

    // Company news ripples to the rest of the target's industry at a fraction of the headline move — a
    // sympathy move for its peers, in the same direction.
    private const decimal IndustrySpilloverFraction = 0.25m;

    // Called from the cycle advance, which already holds the lock and owns the surrounding save; this only
    // adds entities to the shared context when an automated post is due.
    public async Task<PublishNewsResult> MaybeAddAutomatedNewsForCycleAsync(MarketCycle currentCycle, DateTime now)
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

        var (title, content) = DemoNewsContent.Generate(random);
        var post = new NewsPost
        {
            Title = title,
            Content = content,
            PublishedInCycleId = currentCycle.Id,
            PublishedAt = now,
            Scope = NewsImpactScope.None,
        };

        var companiesMoved = 0;
        if (random.NextDouble() < settings.ImpactProbability)
        {
            companiesMoved = await ApplyRandomImpactAsync(post, settings, currentCycle.Id, now);
        }

        dbContext.NewsPosts.Add(post);
        return new PublishNewsResult(true, post, companiesMoved);
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

        var percent = Round(request.ImpactPercent);
        if (percent < MinImpactPercent || percent > MaxManualImpactPercent)
        {
            return ManualNewsResult.Fail($"Impact percent must be between {MinImpactPercent} and {MaxManualImpactPercent}.");
        }

        var content = DemoNewsContent.GenerateForTheme(request.ThemeKey, random);
        if (content is null)
        {
            return ManualNewsResult.Fail("Unknown theme.");
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
            await ApplyCompanyImpactAsync(request.Direction, targetId, percent, cycleId, now);
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

            var affectedCompanyIds = await dbContext.Companies
                .Where(company => validIds.Contains(company.IndustryId))
                .Select(company => company.Id)
                .ToListAsync();
            await marketImpact.ApplyImpactAsync(request.Direction, affectedCompanyIds, percent, cycleId, now);
        }

        dbContext.NewsPosts.Add(post);
        await dbContext.SaveChangesAsync();
        return ManualNewsResult.Ok(post);
    }

    private async Task<int> ApplyRandomImpactAsync(NewsPost post, NewsOptions settings, int cycleId, DateTime now)
    {
        var direction = random.Next(2) == 0 ? NewsImpactDirection.Increase : NewsImpactDirection.Decrease;
        var percent = RandomImpactPercent();
        post.Direction = direction;
        post.ImpactPercent = percent;

        if (random.NextDouble() < settings.CompanyScopeProbability)
        {
            var companyIds = await dbContext.Companies.Select(company => company.Id).ToListAsync();
            if (companyIds.Count == 0)
            {
                ClearImpact(post);
                return 0;
            }

            post.Scope = NewsImpactScope.Company;
            var companyId = companyIds[random.Next(companyIds.Count)];
            post.TargetCompanyId = companyId;
            return await ApplyCompanyImpactAsync(direction, companyId, percent, cycleId, now);
        }

        var industryIds = await dbContext.Industries.Select(industry => industry.Id).ToListAsync();
        if (industryIds.Count == 0)
        {
            ClearImpact(post);
            return 0;
        }

        post.Scope = NewsImpactScope.Industries;
        var wanted = Math.Min(random.Next(1, settings.MaxIndustriesPerPost + 1), industryIds.Count);
        var chosen = PickDistinct(industryIds, wanted);
        foreach (var industryId in chosen)
        {
            post.Industries.Add(new NewsPostIndustry { IndustryId = industryId });
        }

        var affectedCompanyIds = await dbContext.Companies
            .Where(company => chosen.Contains(company.IndustryId))
            .Select(company => company.Id)
            .ToListAsync();

        return await marketImpact.ApplyImpactAsync(direction, affectedCompanyIds, percent, cycleId, now);
    }

    // Company-scoped impact moves the target by the full percent and every same-industry peer by a fraction
    // of it (a sympathy move), all in the same direction. Draws no randomness, so it is safe inside the
    // scripted-Random automated path.
    private async Task<int> ApplyCompanyImpactAsync(
        NewsImpactDirection direction, int targetId, decimal percent, int cycleId, DateTime now)
    {
        var moved = await marketImpact.ApplyImpactAsync(direction, [targetId], percent, cycleId, now);

        var industryId = await dbContext.Companies
            .Where(company => company.Id == targetId)
            .Select(company => company.IndustryId)
            .FirstAsync();
        var peerIds = await dbContext.Companies
            .Where(company => company.IndustryId == industryId && company.Id != targetId)
            .Select(company => company.Id)
            .ToListAsync();

        var spilloverPercent = Round(percent * IndustrySpilloverFraction);
        if (peerIds.Count > 0 && spilloverPercent > 0m)
        {
            moved += await marketImpact.ApplyImpactAsync(direction, peerIds, spilloverPercent, cycleId, now);
        }

        return moved;
    }

    private static void ClearImpact(NewsPost post)
    {
        post.Scope = NewsImpactScope.None;
        post.Direction = null;
        post.ImpactPercent = null;
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
