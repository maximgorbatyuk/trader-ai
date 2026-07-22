using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public enum AiPredictionClusterUnit
{
    Call,
    TradingDay,
}

public sealed record AiPredictionMetric(
    double? Value,
    double? Lower95,
    double? Upper95,
    string UncertaintyStatus);

public sealed record AiPredictionCalibrationBin(
    decimal LowerConfidence,
    decimal UpperConfidence,
    int Count,
    double MeanConfidence,
    double ObservedFrequency);

public sealed record AiProviderPredictionQuality(
    string ProviderId,
    string ProviderLabel,
    string Model,
    int TotalPredictionCount,
    int MaturePredictionCount,
    int CommonWindowPredictionCount,
    int ClusterCount,
    string ClusteringUnit,
    AiPredictionMetric DirectionalAccuracy,
    AiPredictionMetric MeanBrierScore,
    IReadOnlyList<AiPredictionCalibrationBin> CalibrationBins,
    int TargetErrorCount,
    double? MeanAbsolutePercentageError,
    int ExcludedImmatureCount,
    int ExcludedSplitCrossingCount,
    int ExcludedMissingPriceCount,
    int? CommonStartCycle,
    int? CommonEndCycle);

public sealed record AiPredictionQualityReport(
    int? CommonStartCycle,
    int? CommonEndCycle,
    IReadOnlyList<AiProviderPredictionQuality> Groups);

public sealed class AiPredictionEvaluationService(AppDbContext dbContext)
{
    private const double ConfidenceMultiplier = 1.96d;
    private const int MinimumClusters = 5;

    public async Task<AiPredictionQualityReport> EvaluateAsync(AiPredictionClusterUnit clusterUnit)
    {
        var market = await dbContext.Markets
            .Select(candidate => new { candidate.CurrentRunId, candidate.CurrentCycleId })
            .SingleOrDefaultAsync();
        if (market?.CurrentCycleId is not int currentCycleId)
        {
            return EmptyReport();
        }

        var currentCycleNumber = await dbContext.MarketCycles
            .Where(cycle => cycle.Id == currentCycleId && cycle.MarketRunId == market.CurrentRunId)
            .Select(cycle => (int?)cycle.CycleNumber)
            .SingleOrDefaultAsync();
        if (currentCycleNumber is null)
        {
            return EmptyReport();
        }

        var predictions = await (
            from prediction in dbContext.AiPredictions
            join call in dbContext.AiTraderCalls on prediction.AiTraderCallId equals call.Id
            where prediction.MarketRunId == market.CurrentRunId
                && call.MarketRunId == market.CurrentRunId
            select new PredictionInput(
                prediction.AiTraderCallId,
                call.ProviderId,
                call.ProviderLabel,
                call.Model,
                prediction.CompanyId,
                prediction.SnapshotCycleNumber,
                prediction.SnapshotTradingDayNumber,
                prediction.BaselinePrice,
                prediction.Direction,
                prediction.Confidence,
                prediction.HorizonCycles,
                prediction.TargetPrice))
            .ToListAsync();
        if (predictions.Count == 0)
        {
            return EmptyReport();
        }

        var companyIds = predictions.Select(prediction => prediction.CompanyId).Distinct().ToArray();
        var closureCycles = await (
            from company in dbContext.Companies
            join cycle in dbContext.MarketCycles on company.ClosedInCycleId equals cycle.Id
            where companyIds.Contains(company.Id) && cycle.MarketRunId == market.CurrentRunId
            select new { company.Id, cycle.CycleNumber })
            .ToDictionaryAsync(entry => entry.Id, entry => entry.CycleNumber);
        var denominationEvents = await dbContext.StockDenominationEvents
            .Where(action => companyIds.Contains(action.CompanyId))
            .Select(action => new { action.CompanyId, action.EffectiveInCycleNumber })
            .ToListAsync();
        var eventsByCompany = denominationEvents
            .GroupBy(action => action.CompanyId)
            .ToDictionary(group => group.Key, group => group.Select(action => action.EffectiveInCycleNumber).ToArray());

        var states = predictions
            .GroupBy(prediction => new GroupKey(
                prediction.ProviderId, prediction.ProviderLabel, prediction.Model))
            .ToDictionary(group => group.Key, group => new GroupState(group.Count()));

        foreach (var prediction in predictions)
        {
            var state = states[new GroupKey(prediction.ProviderId, prediction.ProviderLabel, prediction.Model)];
            var targetCycleLong = (long)prediction.SnapshotCycleNumber + prediction.HorizonCycles;
            if (targetCycleLong > currentCycleNumber.Value || targetCycleLong > int.MaxValue)
            {
                state.ExcludedImmatureCount++;
                continue;
            }

            state.MaturePredictionCount++;
            state.MatureSnapshotCycles.Add(prediction.SnapshotCycleNumber);
            var targetCycle = (int)targetCycleLong;
            if (eventsByCompany.TryGetValue(prediction.CompanyId, out var eventCycles)
                && eventCycles.Any(cycle => cycle > prediction.SnapshotCycleNumber && cycle <= targetCycle))
            {
                state.ExcludedSplitCrossingCount++;
                continue;
            }

            decimal futurePrice;
            if (closureCycles.TryGetValue(prediction.CompanyId, out var closureCycle)
                && closureCycle > prediction.SnapshotCycleNumber
                && closureCycle <= targetCycle)
            {
                futurePrice = 0m;
            }
            else
            {
                var price = await PriceSnapshotQueries.LatestPriceAtOrBeforeCycleAsync(
                    dbContext, market.CurrentRunId, prediction.CompanyId, targetCycle);
                if (price is null)
                {
                    state.ExcludedMissingPriceCount++;
                    continue;
                }

                futurePrice = price.Value;
            }

            state.Scores.Add(Score(prediction, futurePrice));
        }

        var matureStates = states.Values.Where(state => state.MatureSnapshotCycles.Count > 0).ToList();
        int? commonStartCycle = matureStates.Count == 0
            ? null
            : matureStates.Max(state => state.MatureSnapshotCycles.Min());
        int? commonEndCycle = matureStates.Count == 0
            ? null
            : matureStates.Min(state => state.MatureSnapshotCycles.Max());
        if (commonStartCycle > commonEndCycle)
        {
            commonStartCycle = null;
            commonEndCycle = null;
        }

        var groups = states
            .OrderBy(entry => entry.Key.ProviderId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.Model, StringComparer.Ordinal)
            .Select(entry => BuildQuality(
                entry.Key,
                entry.Value,
                clusterUnit,
                commonStartCycle,
                commonEndCycle))
            .ToList();
        return new AiPredictionQualityReport(commonStartCycle, commonEndCycle, groups);
    }

    private static AiProviderPredictionQuality BuildQuality(
        GroupKey key,
        GroupState state,
        AiPredictionClusterUnit clusterUnit,
        int? commonStartCycle,
        int? commonEndCycle)
    {
        var commonScores = commonStartCycle is int start && commonEndCycle is int end
            ? state.Scores.Where(score => score.SnapshotCycleNumber >= start && score.SnapshotCycleNumber <= end).ToList()
            : [];
        var clusterCount = commonScores
            .Select(score => ClusterKey(score, clusterUnit))
            .Distinct()
            .Count();
        var accuracy = Metric(commonScores, score => score.Correct ? 1d : 0d, clusterUnit, clamp: true);
        var brier = Metric(commonScores, score => score.BrierScore, clusterUnit, clamp: false);
        var targetErrors = commonScores
            .Where(score => score.AbsolutePercentageError is not null)
            .Select(score => score.AbsolutePercentageError!.Value)
            .ToList();

        return new AiProviderPredictionQuality(
            key.ProviderId,
            key.ProviderLabel,
            key.Model,
            state.TotalPredictionCount,
            state.MaturePredictionCount,
            commonScores.Count,
            clusterCount,
            clusterUnit == AiPredictionClusterUnit.Call ? "call" : "tradingDay",
            accuracy,
            brier,
            Calibration(commonScores),
            targetErrors.Count,
            targetErrors.Count == 0 ? null : targetErrors.Average(),
            state.ExcludedImmatureCount,
            state.ExcludedSplitCrossingCount,
            state.ExcludedMissingPriceCount,
            commonStartCycle,
            commonEndCycle);
    }

    private static PredictionScore Score(PredictionInput prediction, decimal futurePrice)
    {
        var probabilityUp = prediction.Direction == AiPredictionDirection.Up
            ? prediction.Confidence
            : 1m - prediction.Confidence;
        var actualUp = futurePrice > prediction.BaselinePrice ? 1m : 0m;
        var difference = probabilityUp - actualUp;
        var correct = prediction.Direction == AiPredictionDirection.Up
            ? futurePrice > prediction.BaselinePrice
            : futurePrice < prediction.BaselinePrice;
        double? targetError = prediction.TargetPrice is { } targetPrice && futurePrice != 0m
            ? (double)(Math.Abs(targetPrice - futurePrice) / Math.Abs(futurePrice))
            : null;
        return new PredictionScore(
            prediction.CallId,
            prediction.SnapshotCycleNumber,
            prediction.SnapshotTradingDayNumber,
            (double)prediction.Confidence,
            correct,
            (double)(difference * difference),
            targetError);
    }

    private static AiPredictionMetric Metric(
        IReadOnlyList<PredictionScore> scores,
        Func<PredictionScore, double> value,
        AiPredictionClusterUnit clusterUnit,
        bool clamp)
    {
        if (scores.Count == 0)
        {
            return new AiPredictionMetric(null, null, null, "NoData");
        }

        var mean = scores.Average(value);
        var clusters = scores.GroupBy(score => ClusterKey(score, clusterUnit)).ToList();
        if (clusters.Count < MinimumClusters)
        {
            return new AiPredictionMetric(mean, null, null, "InsufficientClusters");
        }

        var residualSquares = clusters.Sum(cluster =>
        {
            var clusterResidual = cluster.Sum(score => value(score) - mean);
            return clusterResidual * clusterResidual;
        });
        var variance = (double)clusters.Count / (clusters.Count - 1)
            * residualSquares / (scores.Count * scores.Count);
        var margin = ConfidenceMultiplier * Math.Sqrt(Math.Max(0d, variance));
        var lower = mean - margin;
        var upper = mean + margin;
        if (clamp)
        {
            lower = Math.Clamp(lower, 0d, 1d);
            upper = Math.Clamp(upper, 0d, 1d);
        }

        return new AiPredictionMetric(mean, lower, upper, "Available");
    }

    private static IReadOnlyList<AiPredictionCalibrationBin> Calibration(IReadOnlyList<PredictionScore> scores)
        => scores
            .GroupBy(score => Math.Min(9, (int)Math.Floor(score.Confidence * 10d)))
            .OrderBy(group => group.Key)
            .Select(group => new AiPredictionCalibrationBin(
                group.Key / 10m,
                (group.Key + 1) / 10m,
                group.Count(),
                group.Average(score => score.Confidence),
                group.Average(score => score.Correct ? 1d : 0d)))
            .ToList();

    private static string ClusterKey(PredictionScore score, AiPredictionClusterUnit clusterUnit)
        => clusterUnit == AiPredictionClusterUnit.Call
            ? $"call:{score.CallId}"
            : $"day:{score.SnapshotTradingDayNumber}";

    private static AiPredictionQualityReport EmptyReport() => new(null, null, []);

    private sealed record PredictionInput(
        long CallId,
        string ProviderId,
        string ProviderLabel,
        string Model,
        int CompanyId,
        int SnapshotCycleNumber,
        int SnapshotTradingDayNumber,
        decimal BaselinePrice,
        AiPredictionDirection Direction,
        decimal Confidence,
        int HorizonCycles,
        decimal? TargetPrice);

    private sealed record PredictionScore(
        long CallId,
        int SnapshotCycleNumber,
        int SnapshotTradingDayNumber,
        double Confidence,
        bool Correct,
        double BrierScore,
        double? AbsolutePercentageError);

    private sealed record GroupKey(string ProviderId, string ProviderLabel, string Model);

    private sealed class GroupState(int totalPredictionCount)
    {
        public int TotalPredictionCount { get; } = totalPredictionCount;
        public int MaturePredictionCount { get; set; }
        public int ExcludedImmatureCount { get; set; }
        public int ExcludedSplitCrossingCount { get; set; }
        public int ExcludedMissingPriceCount { get; set; }
        public List<int> MatureSnapshotCycles { get; } = [];
        public List<PredictionScore> Scores { get; } = [];
    }
}
