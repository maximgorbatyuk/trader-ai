using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record PublishNewsResult(bool Published, NewsPost? Post, int CompaniesMoved)
{
    public static PublishNewsResult Skipped() => new(false, null, 0);
}

// Publishes a random themed news post and, when the roll calls for it, moves the share price of a single
// company or every company in one or more industries. Price is moved the same way the matching engine moves
// it: by inserting a new PriceSnapshot, which every read of "current price" derives from. Shares the
// market's cycle lock so a publish never races a cycle tick's SQLite writes.
public sealed class NewsService(
    AppDbContext dbContext,
    MarketCycleLock cycleLock,
    IOptions<NewsLoopOptions> options,
    Random random)
{
    private const decimal MinImpactPercent = 0.1m;
    private const decimal MaxImpactPercent = 10m;

    public async Task<PublishNewsResult> PublishRandomNewsAsync()
    {
        await cycleLock.Semaphore.WaitAsync();
        try
        {
            return await PublishRandomNewsCoreAsync();
        }
        finally
        {
            cycleLock.Semaphore.Release();
        }
    }

    private async Task<PublishNewsResult> PublishRandomNewsCoreAsync()
    {
        var settings = options.Value;

        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market is null || market.Status != MarketStatus.Running || market.CurrentCycleId is not int cycleId)
        {
            return PublishNewsResult.Skipped();
        }

        if (random.NextDouble() >= settings.PublishProbability)
        {
            return PublishNewsResult.Skipped();
        }

        var (title, content) = DemoNewsContent.Generate(random);
        var now = DateTime.UtcNow;

        var post = new NewsPost
        {
            Title = title,
            Content = content,
            PublishedInCycleId = cycleId,
            PublishedAt = now,
            Scope = NewsImpactScope.None,
        };

        var companiesMoved = 0;
        if (random.NextDouble() < settings.ImpactProbability)
        {
            companiesMoved = await ApplyImpactAsync(post, settings, cycleId, now);
        }

        dbContext.NewsPosts.Add(post);
        await dbContext.SaveChangesAsync();

        return new PublishNewsResult(true, post, companiesMoved);
    }

    private async Task<int> ApplyImpactAsync(NewsPost post, NewsLoopOptions settings, int cycleId, DateTime now)
    {
        post.Direction = random.Next(2) == 0 ? NewsImpactDirection.Increase : NewsImpactDirection.Decrease;
        post.ImpactPercent = RandomImpactPercent();

        var affectedCompanyIds = random.NextDouble() < settings.CompanyScopeProbability
            ? await PickCompanyTargetAsync(post)
            : await PickIndustryTargetsAsync(post, settings);

        if (affectedCompanyIds.Count == 0)
        {
            return 0;
        }

        var latestPriceByCompany = await LatestPriceByCompanyAsync();
        var factor = post.Direction == NewsImpactDirection.Increase
            ? 1m + (post.ImpactPercent!.Value / 100m)
            : 1m - (post.ImpactPercent!.Value / 100m);

        var moved = 0;
        foreach (var companyId in affectedCompanyIds)
        {
            if (!latestPriceByCompany.TryGetValue(companyId, out var price) || price <= 0m)
            {
                continue;
            }

            var newPrice = Round(price * factor);
            if (newPrice <= 0m)
            {
                continue;
            }

            dbContext.PriceSnapshots.Add(new PriceSnapshot
            {
                CompanyId = companyId,
                Price = newPrice,
                CreatedInCycleId = cycleId,
                CreatedAt = now,
            });
            moved++;
        }

        return moved;
    }

    private async Task<List<int>> PickCompanyTargetAsync(NewsPost post)
    {
        var companyIds = await dbContext.Companies.Select(company => company.Id).ToListAsync();
        if (companyIds.Count == 0)
        {
            post.Scope = NewsImpactScope.None;
            post.Direction = null;
            post.ImpactPercent = null;
            return [];
        }

        post.Scope = NewsImpactScope.Company;
        var companyId = companyIds[random.Next(companyIds.Count)];
        post.TargetCompanyId = companyId;
        return [companyId];
    }

    private async Task<List<int>> PickIndustryTargetsAsync(NewsPost post, NewsLoopOptions settings)
    {
        var industryIds = await dbContext.Industries.Select(industry => industry.Id).ToListAsync();
        if (industryIds.Count == 0)
        {
            post.Scope = NewsImpactScope.None;
            post.Direction = null;
            post.ImpactPercent = null;
            return [];
        }

        post.Scope = NewsImpactScope.Industries;
        var wanted = Math.Min(random.Next(1, settings.MaxIndustriesPerPost + 1), industryIds.Count);
        var chosen = PickDistinct(industryIds, wanted);

        foreach (var industryId in chosen)
        {
            post.Industries.Add(new NewsPostIndustry { IndustryId = industryId });
        }

        return await dbContext.Companies
            .Where(company => chosen.Contains(company.IndustryId))
            .Select(company => company.Id)
            .ToListAsync();
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

    private async Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync()
    {
        var snapshots = await dbContext.PriceSnapshots.ToListAsync();
        return snapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.Id).First().Price);
    }

    private decimal RandomImpactPercent() =>
        Round(MinImpactPercent + ((decimal)random.NextDouble() * (MaxImpactPercent - MinImpactPercent)));

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
