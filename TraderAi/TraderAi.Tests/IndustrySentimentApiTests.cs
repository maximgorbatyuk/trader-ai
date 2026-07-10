using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Tests;

public sealed class IndustrySentimentApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public IndustrySentimentApiTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task IndustriesIncludeCurrentSentimentAndLastRetainedCycleChange()
    {
        await WithDatabaseAsync(async (client, dbContext) =>
        {
            var (alpha, beta, _, firstCycle, secondCycle, _) = await AddIndustryFixtureAsync(dbContext);
            dbContext.SectorSentimentSnapshots.AddRange(
                Snapshot(alpha.Id, 10, firstCycle.Id, 1),
                Snapshot(alpha.Id, 25, secondCycle.Id, 2),
                Snapshot(beta.Id, -40, secondCycle.Id, 3));
            await dbContext.SaveChangesAsync();

            var industries = (await client.GetFromJsonAsync<IndustryDto[]>("/industries"))!;

            var alphaResponse = Assert.Single(industries, industry => industry.Id == alpha.Id);
            Assert.Equal(25, alphaResponse.SentimentValue);
            Assert.Equal(1.25m, alphaResponse.SentimentVolatility);
            Assert.Equal(0.8m, alphaResponse.SectorBeta);
            Assert.Equal(15, alphaResponse.LastCycleSentimentChange);

            var betaResponse = Assert.Single(industries, industry => industry.Id == beta.Id);
            Assert.Equal(0, betaResponse.LastCycleSentimentChange);
        });
    }

    [Fact]
    public async Task IndustryDetailReportsLiveCompanyAggregateAndReturnsNotFoundForMissingIndustry()
    {
        await WithDatabaseAsync(async (client, dbContext) =>
        {
            var (alpha, _, _, _, secondCycle, _) = await AddIndustryFixtureAsync(dbContext);
            var active = new Company { Name = "Active", IndustryId = alpha.Id, IssuedSharesCount = 10 };
            var closed = new Company
            {
                Name = "Closed",
                IndustryId = alpha.Id,
                IssuedSharesCount = 1_000,
                ClosedInCycleId = secondCycle.Id,
            };
            dbContext.Companies.AddRange(active, closed);
            await dbContext.SaveChangesAsync();

            dbContext.PriceSnapshots.AddRange(
                new PriceSnapshot { CompanyId = active.Id, Price = 12m, CreatedInCycleId = secondCycle.Id, CreatedAt = DateTime.UtcNow },
                new PriceSnapshot { CompanyId = closed.Id, Price = 99m, CreatedInCycleId = secondCycle.Id, CreatedAt = DateTime.UtcNow });
            await dbContext.SaveChangesAsync();

            var detail = await client.GetFromJsonAsync<IndustryDetailDto>($"/industries/{alpha.Id}");
            using var missing = await client.GetAsync("/industries/99999");

            Assert.NotNull(detail);
            Assert.Equal(alpha.Id, detail!.Id);
            Assert.Equal("Alpha", detail.Name);
            Assert.Equal(25, detail.SentimentValue);
            Assert.Equal(1.25m, detail.SentimentVolatility);
            Assert.Equal(0.8m, detail.SectorBeta);
            Assert.Equal(120m, detail.TotalNetWorth);
            Assert.Equal(1, detail.CompanyCount);
            Assert.Equal(0, detail.LastCycleSentimentChange);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        });
    }

    [Fact]
    public async Task IndustrySentimentHistoryIsChronologicalAndIgnoresArchivedSnapshots()
    {
        await WithDatabaseAsync(async (client, dbContext) =>
        {
            var (alpha, _, _, firstCycle, secondCycle, thirdCycle) = await AddIndustryFixtureAsync(dbContext);
            dbContext.SectorSentimentSnapshots.AddRange(
                Snapshot(alpha.Id, 20, secondCycle.Id, 2),
                Snapshot(alpha.Id, 5, firstCycle.Id, 1),
                Snapshot(alpha.Id, 30, thirdCycle.Id, 3));
            dbContext.SectorSentimentSnapshotArchives.Add(
                new SectorSentimentSnapshotArchive
                {
                    IndustryId = alpha.Id,
                    SentimentValue = -200,
                    CreatedInCycleId = firstCycle.Id,
                    CreatedAt = DateTime.UtcNow.AddDays(-1),
                });
            await dbContext.SaveChangesAsync();

            var history = (await client.GetFromJsonAsync<SentimentPointDto[]>($"/industries/{alpha.Id}/sentiment-history"))!;
            using var missing = await client.GetAsync("/industries/99999/sentiment-history");

            Assert.Equal([5, 20, 30], history.Select(point => point.SentimentValue));
            Assert.Equal([1, 2, 3], history.Select(point => point.CycleNumber));
            Assert.Equal([firstCycle.Id, secondCycle.Id, thirdCycle.Id], history.Select(point => point.CreatedInCycleId));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        });
    }

    [Fact]
    public async Task AllIndustrySentimentHistoriesAreSeparatedByIndustryAndKeepEmptyLiveSeries()
    {
        await WithDatabaseAsync(async (client, dbContext) =>
        {
            var (alpha, beta, gamma, firstCycle, secondCycle, _) = await AddIndustryFixtureAsync(dbContext);
            dbContext.SectorSentimentSnapshots.AddRange(
                Snapshot(alpha.Id, 10, firstCycle.Id, 1),
                Snapshot(alpha.Id, 11, secondCycle.Id, 2),
                Snapshot(beta.Id, -5, secondCycle.Id, 3));
            await dbContext.SaveChangesAsync();

            var histories = (await client.GetFromJsonAsync<IndustryHistoryDto[]>("/industries/sentiment-history"))!;

            Assert.Equal(["Alpha", "Beta", "Gamma"], histories.Select(history => history.IndustryName));
            Assert.Equal([10, 11], Assert.Single(histories, history => history.IndustryId == alpha.Id).Points.Select(point => point.SentimentValue));
            Assert.Equal([-5], Assert.Single(histories, history => history.IndustryId == beta.Id).Points.Select(point => point.SentimentValue));
            Assert.Empty(Assert.Single(histories, history => history.IndustryId == gamma.Id).Points);
        });
    }

    [Fact]
    public async Task IndustryNewsIncludesOnlyPostsLinkedToThatIndustryNewestFirst()
    {
        await WithDatabaseAsync(async (client, dbContext) =>
        {
            var (alpha, beta, _, _, secondCycle, _) = await AddIndustryFixtureAsync(dbContext);
            var linkedOlder = News("Linked older", secondCycle.Id, DateTime.UtcNow.AddMinutes(-2), NewsImpactScope.Industries);
            var linkedNewer = News("Linked newer", secondCycle.Id, DateTime.UtcNow.AddMinutes(-1), NewsImpactScope.Industries);
            var targetOnly = News("Target only", secondCycle.Id, DateTime.UtcNow, NewsImpactScope.Company);
            var otherIndustry = News("Other industry", secondCycle.Id, DateTime.UtcNow, NewsImpactScope.Industries);
            dbContext.NewsPosts.AddRange(linkedOlder, linkedNewer, targetOnly, otherIndustry);
            await dbContext.SaveChangesAsync();
            dbContext.NewsPostIndustries.AddRange(
                new NewsPostIndustry { NewsPostId = linkedOlder.Id, IndustryId = alpha.Id },
                new NewsPostIndustry { NewsPostId = linkedNewer.Id, IndustryId = alpha.Id },
                new NewsPostIndustry { NewsPostId = otherIndustry.Id, IndustryId = beta.Id });
            await dbContext.SaveChangesAsync();

            var news = (await client.GetFromJsonAsync<NewsDto[]>($"/industries/{alpha.Id}/news"))!;
            using var missing = await client.GetAsync("/industries/99999/news");

            Assert.Equal(["Linked newer", "Linked older"], news.Select(post => post.Title));
            Assert.All(news, post => Assert.Contains("Alpha", post.IndustryNames));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        });
    }

    private async Task WithDatabaseAsync(Func<HttpClient, AppDbContext, Task> test)
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={databasePath}");
            });
            using var client = configuredFactory.CreateClient();
            using var scope = configuredFactory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await test(client, dbContext);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    private static async Task<(Industry Alpha, Industry Beta, Industry Gamma, MarketCycle First, MarketCycle Second, MarketCycle Third)> AddIndustryFixtureAsync(AppDbContext dbContext)
    {
        var first = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Completed };
        var second = new MarketCycle { CycleNumber = 2, Status = CycleStatus.Completed };
        var third = new MarketCycle { CycleNumber = 3, Status = CycleStatus.Completed };
        var alpha = new Industry { Name = "Alpha", SentimentValue = 25, SentimentVolatility = 1.25m, SectorBeta = 0.8m };
        var beta = new Industry { Name = "Beta", SentimentValue = -40, SentimentVolatility = 0.5m, SectorBeta = 1.1m };
        var gamma = new Industry { Name = "Gamma", SentimentValue = 0, SentimentVolatility = 0m, SectorBeta = 1m };
        dbContext.AddRange(first, second, third, alpha, beta, gamma);
        await dbContext.SaveChangesAsync();
        return (alpha, beta, gamma, first, second, third);
    }

    private static SectorSentimentSnapshot Snapshot(int industryId, int sentimentValue, int cycleId, int order) =>
        new()
        {
            IndustryId = industryId,
            SentimentValue = sentimentValue,
            CreatedInCycleId = cycleId,
            CreatedAt = DateTime.UtcNow.AddSeconds(order),
        };

    private static NewsPost News(string title, int cycleId, DateTime publishedAt, NewsImpactScope scope) =>
        new()
        {
            Title = title,
            Content = $"{title} content",
            PublishedInCycleId = cycleId,
            PublishedAt = publishedAt,
            Scope = scope,
        };

    private sealed record IndustryDto(
        int Id,
        string Name,
        int SentimentValue,
        decimal SentimentVolatility,
        decimal SectorBeta,
        int LastCycleSentimentChange);

    private sealed record IndustryDetailDto(
        int Id,
        string Name,
        int SentimentValue,
        decimal SentimentVolatility,
        decimal SectorBeta,
        decimal TotalNetWorth,
        int LastCycleSentimentChange,
        int CompanyCount);

    private sealed record SentimentPointDto(int CreatedInCycleId, int CycleNumber, int SentimentValue, DateTime CreatedAt);

    private sealed record IndustryHistoryDto(int IndustryId, string IndustryName, SentimentPointDto[] Points);

    private sealed record NewsDto(int Id, string Title, string[] IndustryNames);
}
