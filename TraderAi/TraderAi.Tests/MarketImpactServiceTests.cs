using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class MarketImpactServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public MarketImpactServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task PositiveSentimentAmplifiesAnIncrease()
    {
        var (cycle, company) = await SeedCompanyAsync(sentimentValue: 500, sectorBeta: 1m);

        await new MarketImpactService(context).ApplyImpactAsync(
            NewsImpactDirection.Increase,
            [company.Id],
            10m,
            cycle.Id,
            DateTime.UtcNow,
            cancelStaleOrders: false,
            applySectorSentiment: true);
        await context.SaveChangesAsync();

        Assert.Equal(115m, await LatestPriceAsync(company.Id));
    }

    [Fact]
    public async Task NeutralSentimentAndBetaPreserveTheRequestedImpact()
    {
        var (cycle, company) = await SeedCompanyAsync(sentimentValue: 0, sectorBeta: 1m);

        await ApplySectorImpactAsync(NewsImpactDirection.Increase, [company.Id], 10m, cycle.Id);

        Assert.Equal(110m, await LatestPriceAsync(company.Id));
    }

    [Fact]
    public async Task NegativeSentimentCushionsAnIncrease()
    {
        var (cycle, company) = await SeedCompanyAsync(sentimentValue: -500, sectorBeta: 1m);

        await ApplySectorImpactAsync(NewsImpactDirection.Increase, [company.Id], 10m, cycle.Id);

        Assert.Equal(105m, await LatestPriceAsync(company.Id));
    }

    [Fact]
    public async Task PositiveSentimentCushionsADecrease()
    {
        var (cycle, company) = await SeedCompanyAsync(sentimentValue: 500, sectorBeta: 1m);

        await ApplySectorImpactAsync(NewsImpactDirection.Decrease, [company.Id], 10m, cycle.Id);

        Assert.Equal(95m, await LatestPriceAsync(company.Id));
    }

    [Fact]
    public async Task NegativeSentimentAmplifiesADecrease()
    {
        var (cycle, company) = await SeedCompanyAsync(sentimentValue: -500, sectorBeta: 1m);

        await ApplySectorImpactAsync(NewsImpactDirection.Decrease, [company.Id], 10m, cycle.Id);

        Assert.Equal(85m, await LatestPriceAsync(company.Id));
    }

    [Theory]
    [InlineData(2000, 115)]
    [InlineData(-2000, 105)]
    public async Task SentimentFactorIsClamped(int sentimentValue, int expectedPrice)
    {
        var (cycle, company) = await SeedCompanyAsync(sentimentValue, sectorBeta: 1m);

        await ApplySectorImpactAsync(NewsImpactDirection.Increase, [company.Id], 10m, cycle.Id);

        Assert.Equal(expectedPrice, await LatestPriceAsync(company.Id));
    }

    [Theory]
    [InlineData(5, 105)]
    [InlineData(15, 115)]
    public async Task SectorBetaCushionsDefensiveSectorsAndAmplifiesCyclicalSectors(
        int betaTenths,
        int expectedPrice)
    {
        var (cycle, company) = await SeedCompanyAsync(sentimentValue: 0, sectorBeta: betaTenths / 10m);

        await ApplySectorImpactAsync(NewsImpactDirection.Increase, [company.Id], 10m, cycle.Id);

        Assert.Equal(expectedPrice, await LatestPriceAsync(company.Id));
    }

    [Fact]
    public async Task CompaniesFromDifferentIndustriesAreScaledIndividuallyInOneCall()
    {
        var (cycle, defensive) = await SeedCompanyAsync(sentimentValue: 500, sectorBeta: 0.5m);
        var cyclical = await AddCompanyAsync(cycle, "Cyclical", sentimentValue: 0, sectorBeta: 1.5m);

        await ApplySectorImpactAsync(
            NewsImpactDirection.Increase,
            [defensive.Id, cyclical.Id],
            10m,
            cycle.Id);

        Assert.Equal(107.5m, await LatestPriceAsync(defensive.Id));
        Assert.Equal(115m, await LatestPriceAsync(cyclical.Id));
    }

    [Fact]
    public async Task DisabledSectorScalingPreservesTheRequestedImpact()
    {
        var (cycle, company) = await SeedCompanyAsync(sentimentValue: 500, sectorBeta: 2m);

        await new MarketImpactService(context).ApplyImpactAsync(
            NewsImpactDirection.Increase,
            [company.Id],
            10m,
            cycle.Id,
            DateTime.UtcNow,
            cancelStaleOrders: false,
            applySectorSentiment: false);
        await context.SaveChangesAsync();

        Assert.Equal(110m, await LatestPriceAsync(company.Id));
    }

    private async Task ApplySectorImpactAsync(
        NewsImpactDirection direction,
        IReadOnlyCollection<int> companyIds,
        decimal percent,
        int cycleId)
    {
        await new MarketImpactService(context).ApplyImpactAsync(
            direction,
            companyIds,
            percent,
            cycleId,
            DateTime.UtcNow,
            cancelStaleOrders: false,
            applySectorSentiment: true);
        await context.SaveChangesAsync();
    }

    private async Task<(MarketCycle Cycle, Company Company)> SeedCompanyAsync(int sentimentValue, decimal sectorBeta)
    {
        var now = DateTime.UtcNow;
        var cycle = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();

        var company = await AddCompanyAsync(cycle, "Alpha", sentimentValue, sectorBeta);
        return (cycle, company);
    }

    private async Task<Company> AddCompanyAsync(
        MarketCycle cycle,
        string name,
        int sentimentValue,
        decimal sectorBeta)
    {
        var now = DateTime.UtcNow;
        var industry = new Industry
        {
            Name = $"{name} Industry",
            SentimentValue = sentimentValue,
            SectorBeta = sectorBeta,
        };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();

        var company = new Company
        {
            Name = name,
            IndustryId = industry.Id,
            IssuedSharesCount = 100,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();

        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = 100m,
            Capitalization = 10_000m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });
        await context.SaveChangesAsync();

        return company;
    }

    private Task<decimal> LatestPriceAsync(int companyId) =>
        context.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == companyId)
            .OrderByDescending(snapshot => snapshot.Id)
            .Select(snapshot => snapshot.Price)
            .FirstAsync();

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
