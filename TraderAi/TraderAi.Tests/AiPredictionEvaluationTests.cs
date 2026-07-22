using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AiPredictionEvaluationTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly AiPredictionEvaluationService service;
    private int runId;
    private int industryId;
    private readonly Dictionary<int, int> cycleIds = new();

    public AiPredictionEvaluationTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        service = new AiPredictionEvaluationService(context);
    }

    [Fact]
    public async Task ScoresDirectionsConfidenceTargetsClosureAndImmaturity()
    {
        await SeedMarketAsync(currentCycleNumber: 20);
        var liveCompany = await AddCompanyAsync("Live");
        var archivedCompany = await AddCompanyAsync("Archived");
        var closedCompany = await AddCompanyAsync("Closed", closedInCycleNumber: 8);
        await AddPriceAsync(liveCompany, cycleNumber: 10, price: 110m);
        await AddPriceAsync(archivedCompany, cycleNumber: 10, price: 110m, archived: true);
        AddPrediction("glm", "GLM", "model-a", liveCompany, snapshotCycle: 1, day: 1,
            baseline: 100m, AiPredictionDirection.Up, confidence: 0.8m, horizon: 9, target: 120m);
        AddPrediction("glm", "GLM", "model-a", archivedCompany, snapshotCycle: 1, day: 1,
            baseline: 100m, AiPredictionDirection.Down, confidence: 0.7m, horizon: 9, target: 80m);
        AddPrediction("glm", "GLM", "model-a", closedCompany, snapshotCycle: 1, day: 1,
            baseline: 50m, AiPredictionDirection.Down, confidence: 0.9m, horizon: 9);
        AddPrediction("glm", "GLM", "model-a", liveCompany, snapshotCycle: 15, day: 2,
            baseline: 110m, AiPredictionDirection.Up, confidence: 0.6m, horizon: 10);
        await context.SaveChangesAsync();

        var report = await service.EvaluateAsync(AiPredictionClusterUnit.Call);

        var quality = Assert.Single(report.Groups);
        Assert.Equal(4, quality.TotalPredictionCount);
        Assert.Equal(3, quality.MaturePredictionCount);
        Assert.Equal(3, quality.CommonWindowPredictionCount);
        Assert.Equal(1, quality.ExcludedImmatureCount);
        Assert.Equal(2d / 3d, quality.DirectionalAccuracy.Value!.Value, precision: 10);
        Assert.Equal(0.18d, quality.MeanBrierScore.Value!.Value, precision: 10);
        Assert.Equal(2, quality.TargetErrorCount);
        Assert.Equal(0.1818181818d, quality.MeanAbsolutePercentageError!.Value, precision: 9);
        Assert.Equal("InsufficientClusters", quality.DirectionalAccuracy.UncertaintyStatus);
    }

    [Fact]
    public async Task ExcludesSplitCrossingAndMissingPriceWindows()
    {
        await SeedMarketAsync(currentCycleNumber: 20);
        var splitCompany = await AddCompanyAsync("Split");
        var missingCompany = await AddCompanyAsync("Missing");
        AddPrediction("glm", "GLM", "model-a", splitCompany, snapshotCycle: 1, day: 1,
            baseline: 100m, AiPredictionDirection.Up, confidence: 0.8m, horizon: 9);
        AddPrediction("glm", "GLM", "model-a", missingCompany, snapshotCycle: 1, day: 1,
            baseline: 100m, AiPredictionDirection.Up, confidence: 0.8m, horizon: 9);
        context.StockDenominationEvents.Add(new StockDenominationEvent
        {
            CompanyId = splitCompany,
            ActionType = StockDenominationActionType.Split,
            Ratio = 4,
            IssuedSharesBefore = 100,
            IssuedSharesAfter = 400,
            PriceBefore = 100m,
            PriceAfter = 25m,
            EffectiveInCycleId = cycleIds[5],
            EffectiveInCycleNumber = 5,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var report = await service.EvaluateAsync(AiPredictionClusterUnit.Call);

        var quality = Assert.Single(report.Groups);
        Assert.Equal(2, quality.MaturePredictionCount);
        Assert.Equal(1, quality.ExcludedSplitCrossingCount);
        Assert.Equal(1, quality.ExcludedMissingPriceCount);
        Assert.Equal(0, quality.CommonWindowPredictionCount);
        Assert.Equal("NoData", quality.DirectionalAccuracy.UncertaintyStatus);
    }

    [Fact]
    public async Task UsesACommonProviderWindowAndRequestedClusterUnit()
    {
        await SeedMarketAsync(currentCycleNumber: 30);
        var companyId = await AddCompanyAsync("Common");
        foreach (var targetCycle in Enumerable.Range(2, 20))
        {
            await AddPriceAsync(companyId, targetCycle, targetCycle is 5 or 7 ? 90m : 110m);
        }

        foreach (var snapshot in new[] { 1, 5, 10 })
        {
            AddPrediction("glm", "GLM", "model-a", companyId, snapshot, snapshot,
                100m, AiPredictionDirection.Up, 0.8m, horizon: 1);
        }

        foreach (var snapshot in new[] { 3, 4, 5, 6, 8 })
        {
            AddPrediction("minimax", "MiniMax", "model-b", companyId, snapshot, snapshot,
                100m, AiPredictionDirection.Up, 0.8m, horizon: 1);
        }
        await context.SaveChangesAsync();

        var byCall = await service.EvaluateAsync(AiPredictionClusterUnit.Call);
        var byDay = await service.EvaluateAsync(AiPredictionClusterUnit.TradingDay);

        Assert.Equal(3, byCall.CommonStartCycle);
        Assert.Equal(8, byCall.CommonEndCycle);
        var glm = Assert.Single(byCall.Groups, group => group.ProviderId == "glm");
        var minimax = Assert.Single(byCall.Groups, group => group.ProviderId == "minimax");
        Assert.Equal(1, glm.CommonWindowPredictionCount);
        Assert.Equal(5, minimax.CommonWindowPredictionCount);
        Assert.Equal("call", minimax.ClusteringUnit);
        Assert.Equal(5, minimax.ClusterCount);
        Assert.Equal("Available", minimax.DirectionalAccuracy.UncertaintyStatus);
        Assert.Equal(0.6d, minimax.DirectionalAccuracy.Value);
        Assert.InRange(minimax.DirectionalAccuracy.Lower95!.Value, 0.119d, 0.121d);
        Assert.Equal(1d, minimax.DirectionalAccuracy.Upper95);
        Assert.Equal(0.28d, minimax.MeanBrierScore.Value);
        Assert.Equal("tradingDay", Assert.Single(byDay.Groups, group => group.ProviderId == "minimax").ClusteringUnit);
    }

    private async Task SeedMarketAsync(int currentCycleNumber)
    {
        var run = new MarketRun { StartedAt = DateTime.UtcNow };
        var industry = new Industry { Name = "Tech" };
        var day = new TradingDay { DayNumber = 1, State = TradingSessionState.Trading, OpenedInCycleId = 0 };
        context.AddRange(run, industry, day);
        await context.SaveChangesAsync();
        runId = run.Id;
        industryId = industry.Id;

        for (var number = 1; number <= currentCycleNumber; number++)
        {
            var cycle = new MarketCycle
            {
                MarketRunId = run.Id,
                CycleNumber = number,
                TradingDayId = day.Id,
                TradingCycleNumber = number,
                Status = CycleStatus.Completed,
            };
            context.MarketCycles.Add(cycle);
            await context.SaveChangesAsync();
            cycleIds[number] = cycle.Id;
        }

        day.OpenedInCycleId = cycleIds[1];
        context.Markets.Add(new Market
        {
            Name = "Market",
            Status = MarketStatus.Running,
            CurrentRunId = run.Id,
            CurrentCycleId = cycleIds[currentCycleNumber],
            CurrentTradingDayId = day.Id,
        });
        await context.SaveChangesAsync();
    }

    private async Task<int> AddCompanyAsync(string name, int? closedInCycleNumber = null)
    {
        var company = new Company
        {
            Name = name,
            IndustryId = industryId,
            IssuedSharesCount = 1_000,
            ClosedInCycleId = closedInCycleNumber is null ? null : cycleIds[closedInCycleNumber.Value],
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        return company.Id;
    }

    private async Task AddPriceAsync(int companyId, int cycleNumber, decimal price, bool archived = false)
    {
        if (archived)
        {
            context.PriceSnapshotArchives.Add(new PriceSnapshotArchive
            {
                Id = 10_000 + companyId * 100 + cycleNumber,
                MarketRunId = runId,
                CompanyId = companyId,
                Price = price,
                CreatedInCycleId = cycleIds[cycleNumber],
                CreatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            context.PriceSnapshots.Add(new PriceSnapshot
            {
                CompanyId = companyId,
                Price = price,
                CreatedInCycleId = cycleIds[cycleNumber],
                CreatedAt = DateTime.UtcNow,
            });
        }

        await context.SaveChangesAsync();
    }

    private void AddPrediction(
        string providerId,
        string providerLabel,
        string model,
        int companyId,
        int snapshotCycle,
        int day,
        decimal baseline,
        AiPredictionDirection direction,
        decimal confidence,
        int horizon,
        decimal? target = null)
    {
        var call = new AiTraderCall
        {
            MarketRunId = runId,
            ParticipantId = 1,
            ParticipantName = providerLabel,
            ProviderId = providerId,
            ProviderLabel = providerLabel,
            Model = model,
            SnapshotCycleId = cycleIds[snapshotCycle],
            SnapshotCycleNumber = snapshotCycle,
            PromptHash = "hash",
            RequestJson = "{}",
            Status = AiTraderCallStatus.Completed,
            RequestedAt = DateTime.UtcNow,
        };
        call.Predictions.Add(new AiPrediction
        {
            MarketRunId = runId,
            ParticipantId = 1,
            CompanyId = companyId,
            SnapshotCycleNumber = snapshotCycle,
            SnapshotTradingDayNumber = day,
            BaselinePrice = baseline,
            Direction = direction,
            Confidence = confidence,
            HorizonCycles = horizon,
            TargetPrice = target,
            Reason = "Forecast.",
        });
        context.AiTraderCalls.Add(call);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
