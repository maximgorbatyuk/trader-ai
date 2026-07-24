using System.Reflection;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class IndustrySentimentPersistenceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public IndustrySentimentPersistenceTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void ModelDefinesIndustrySentimentStateAndHistory()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var context = new AppDbContext(options);

        var industry = new Industry { Name = "Software" };
        Assert.Equal(0, industry.SentimentValue);
        Assert.Equal(0m, industry.SentimentVolatility);
        Assert.Equal(1m, industry.SectorBeta);

        var industryType = context.Model.FindEntityType(typeof(Industry))!;
        Assert.Equal(0, industryType.FindProperty(nameof(Industry.SentimentValue))!.GetDefaultValue());
        Assert.Equal(0m, industryType.FindProperty(nameof(Industry.SentimentVolatility))!.GetDefaultValue());
        Assert.Equal(1m, industryType.FindProperty(nameof(Industry.SectorBeta))!.GetDefaultValue());

        var snapshotType = context.Model.FindEntityType(typeof(SectorSentimentSnapshot))!;
        Assert.Contains(snapshotType.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(SectorSentimentSnapshot.CreatedInCycleId)]));
        Assert.Contains(snapshotType.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Single().Name == nameof(SectorSentimentSnapshot.IndustryId) &&
            foreignKey.PrincipalEntityType.ClrType == typeof(Industry));
        Assert.Contains(snapshotType.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Single().Name == nameof(SectorSentimentSnapshot.CreatedInCycleId) &&
            foreignKey.PrincipalEntityType.ClrType == typeof(MarketCycle));

        var archiveType = context.Model.FindEntityType(typeof(SectorSentimentSnapshotArchive))!;
        Assert.Empty(archiveType.GetForeignKeys());
        Assert.Contains(archiveType.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(SectorSentimentSnapshotArchive.CreatedInCycleId)]));

        Assert.NotNull(typeof(AppDbContext).GetProperty(nameof(AppDbContext.SectorSentimentSnapshots)));
        Assert.NotNull(typeof(AppDbContext).GetProperty(nameof(AppDbContext.SectorSentimentSnapshotArchives)));
        Assert.Null(new NewsPost
        {
            Title = "Headline",
            Content = "Body",
            PublishedInCycleId = 1,
            PublishedAt = DateTime.UtcNow,
        }.ImpactAppliedInCycleId);
    }

    [Fact]
    public void IndustrySentimentOptionsAndChanceRatesExposeExpectedDefaults()
    {
        var options = new IndustrySentimentOptions();
        Assert.False(options.Enabled);
        Assert.Equal(-300, options.SentimentValueMin);
        Assert.Equal(300, options.SentimentValueMax);
        Assert.Equal(0m, options.SentimentVolatilityMin);
        Assert.Equal(3m, options.SentimentVolatilityMax);
        Assert.Equal(0.6m, options.SectorBetaMin);
        Assert.Equal(1.5m, options.SectorBetaMax);
        Assert.Equal(1000, options.SentimentValueLimit);
        Assert.Equal(1, options.SentimentDecayPerCycle);

        var chanceRates = new RandomChanceRatesOptions();
        Assert.Equal(0.25, chanceRates.EventTriggerChances.IndustrySentimentRevisionBase);
        Assert.Equal(0.50, chanceRates.EventTriggerChances.ScienceSentimentPush);
        Assert.Equal(0.10, chanceRates.ChanceModifiers.IndustrySentimentVolatilityFactor);
        Assert.Equal(0.50, chanceRates.ChanceModifiers.CrisisSentimentForcedDown);
        Assert.Equal(0.10, chanceRates.ChanceModifiers.CompanyNewsSentimentBonus);
    }

    [Fact]
    public void ConfigurationBindsIndustrySentimentSection()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var scope = configuredFactory.Services.CreateScope();

            var options = scope.ServiceProvider.GetRequiredService<IOptions<IndustrySentimentOptions>>().Value;

            Assert.True(options.Enabled);
            Assert.Equal(-300, options.SentimentValueMin);
            Assert.Equal(1.5m, options.SectorBetaMax);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SeedAppendsIndustryDrawsWithoutChangingCompanySequence()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            using var response = await client.PostAsync("/market/seed", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var scope = configuredFactory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var actualCompanies = await context.Companies
                .OrderBy(company => company.Id)
                .Join(
                    context.PriceSnapshots,
                    company => company.Id,
                    snapshot => snapshot.CompanyId,
                    (company, snapshot) => new SeededCompany(snapshot.Price, company.IssuedSharesCount))
                .ToArrayAsync();
            var actualIndustries = await context.Industries.OrderBy(industry => industry.Id).ToArrayAsync();

            var (expectedCompanies, expectedIndustries) = ExpectedSeedValues(actualIndustries.Length);
            Assert.Equal(expectedCompanies, actualCompanies);
            Assert.Collection(
                actualIndustries,
                expectedIndustries.Select<ExpectedIndustry, Action<Industry>>(expected => actual =>
                {
                    Assert.Equal(expected.SentimentValue, actual.SentimentValue);
                    Assert.Equal(expected.SentimentVolatility, actual.SentimentVolatility);
                    Assert.Equal(expected.SectorBeta, actual.SectorBeta);
                }).ToArray());
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DisabledSentimentSeedKeepsIndustriesNeutralAndPreservesTheConfiguredCompanySequence()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath, sentimentEnabled: false);
            using var client = configuredFactory.CreateClient();

            using var response = await client.PostAsync("/market/seed", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var scope = configuredFactory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var actualCompanies = await context.Companies
                .OrderBy(company => company.Id)
                .Join(
                    context.PriceSnapshots,
                    company => company.Id,
                    snapshot => snapshot.CompanyId,
                    (company, snapshot) => new SeededCompany(snapshot.Price, company.IssuedSharesCount))
                .ToArrayAsync();
            var industries = await context.Industries.OrderBy(industry => industry.Id).ToArrayAsync();

            var (expectedCompanies, _) = ExpectedSeedValues(industries.Length);
            Assert.Equal(expectedCompanies, actualCompanies);
            Assert.All(industries, industry =>
            {
                Assert.Equal(0, industry.SentimentValue);
                Assert.Equal(0m, industry.SentimentVolatility);
                Assert.Equal(1m, industry.SectorBeta);
            });
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ResetClearsSentimentHistoryAndReseedsIndustries()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            ExpectedIndustry[] firstSeed;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var industry = await context.Industries.OrderBy(item => item.Id).FirstAsync();
                var cycle = await context.MarketCycles.OrderBy(item => item.Id).FirstAsync();
                firstSeed = await context.Industries
                    .OrderBy(item => item.Id)
                    .Select(item => new ExpectedIndustry(item.SentimentValue, item.SentimentVolatility, item.SectorBeta))
                    .ToArrayAsync();

                context.SectorSentimentSnapshots.Add(new SectorSentimentSnapshot
                {
                    IndustryId = industry.Id,
                    SentimentValue = industry.SentimentValue,
                    CreatedInCycleId = cycle.Id,
                    CreatedAt = DateTime.UtcNow,
                });
                context.SectorSentimentSnapshotArchives.Add(new SectorSentimentSnapshotArchive
                {
                    IndustryId = industry.Id,
                    SentimentValue = industry.SentimentValue,
                    CreatedInCycleId = cycle.Id,
                    CreatedAt = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();
            }

            using var resetResponse = await client.PostAsync("/market/reset", null);
            Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);

            using (var scope = configuredFactory.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Empty(await context.SectorSentimentSnapshots.ToArrayAsync());
                Assert.Empty(await context.SectorSentimentSnapshotArchives.ToArrayAsync());

                var secondSeed = await context.Industries
                    .OrderBy(item => item.Id)
                    .Select(item => new ExpectedIndustry(item.SentimentValue, item.SentimentVolatility, item.SectorBeta))
                    .ToArrayAsync();
                Assert.Equal(firstSeed, secondSeed);

                var industryId = await context.Industries.OrderBy(item => item.Id).Select(item => item.Id).FirstAsync();
                var cycleId = await context.MarketCycles.OrderBy(item => item.Id).Select(item => item.Id).FirstAsync();
                var live = new SectorSentimentSnapshot
                {
                    IndustryId = industryId,
                    SentimentValue = 0,
                    CreatedInCycleId = cycleId,
                    CreatedAt = DateTime.UtcNow,
                };
                var archived = new SectorSentimentSnapshotArchive
                {
                    IndustryId = industryId,
                    SentimentValue = 0,
                    CreatedInCycleId = cycleId,
                    CreatedAt = DateTime.UtcNow,
                };
                context.SectorSentimentSnapshots.Add(live);
                context.SectorSentimentSnapshotArchives.Add(archived);
                await context.SaveChangesAsync();

                Assert.True(live.Id > 1);
                Assert.True(archived.Id > 1);
            }
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory(string databasePath, bool? sentimentEnabled = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={databasePath}");
            if (sentimentEnabled is bool enabled)
            {
                builder.UseSetting("IndustrySentiment:Enabled", enabled.ToString());
            }
        });

    private static (SeededCompany[] Companies, ExpectedIndustry[] Industries) ExpectedSeedValues(int industryCount)
    {
        var random = new Random(20260619);
        var namesType = typeof(MarketService).Assembly.GetType("TraderAi.Services.DemoMarketNames")!;
        namesType.GetMethod("PickPeople", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [600, random]);
        namesType.GetMethod("PickCompanies", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [100, random]);

        for (var index = 0; index < 600; index++)
        {
            var isWhale = random.NextDouble() < 0.15;
            random.NextInt64(isWhale ? 2_000_000 : 200_000, isWhale ? 10_000_001 : 800_001);
        }

        var companies = Enumerable.Range(0, 100)
            .Select(_ => new SeededCompany(random.Next(20, 301), random.Next(2000, 20001)))
            .ToArray();

        random.Next(10, 26);
        var industries = Enumerable.Range(0, industryCount)
            .Select(_ => new ExpectedIndustry(
                random.Next(-300, 301),
                (decimal)random.NextDouble() * 3m,
                0.6m + ((decimal)random.NextDouble() * 0.9m)))
            .ToArray();

        return (companies, industries);
    }

    private sealed record SeededCompany(decimal Price, int IssuedSharesCount);

    private sealed record ExpectedIndustry(int SentimentValue, decimal SentimentVolatility, decimal SectorBeta);
}
