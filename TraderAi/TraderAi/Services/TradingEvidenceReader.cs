using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

internal sealed record TradingEvidencePoint(
    int MarketRunId,
    int CycleNumber,
    int TradingDayNumber,
    int TradingCycleNumber = 0);

internal sealed record EffectiveAuditEvidenceRow(
    CompanyRating Rating,
    int CreatedCycleNumber,
    CompanyAuditEvidence Evidence,
    EffectiveAuditEvidence DecisionEvidence);

internal sealed record TradingEvidenceBatch(
    IReadOnlyDictionary<int, EffectiveAuditEvidenceRow> EffectiveAudits,
    IReadOnlyDictionary<int, LatestFinancialEvidence> LatestFinancials)
{
    public static TradingEvidenceBatch Empty { get; } = new(
        new Dictionary<int, EffectiveAuditEvidenceRow>(),
        new Dictionary<int, LatestFinancialEvidence>());
}

internal static class TradingEvidenceReader
{
    public static async Task<TradingEvidenceBatch> LoadAsync(
        AppDbContext dbContext,
        IReadOnlyCollection<int> companyIds,
        TradingEvidencePoint point)
    {
        if (companyIds.Count == 0)
        {
            return TradingEvidenceBatch.Empty;
        }

        var auditRows = await (
                from evidence in dbContext.CompanyAuditEvidence.AsNoTracking()
                join rating in dbContext.CompanyRatings.AsNoTracking()
                    on evidence.CompanyRatingId equals rating.Id
                join ratingCycle in dbContext.MarketCycles.AsNoTracking()
                    on rating.CreatedInCycleId equals ratingCycle.Id
                where companyIds.Contains(evidence.CompanyId)
                    && evidence.EffectiveTradingDayNumber <= point.TradingDayNumber
                    && ratingCycle.MarketRunId == point.MarketRunId
                    && ratingCycle.CycleNumber <= point.CycleNumber
                    && !(
                        from candidateEvidence in dbContext.CompanyAuditEvidence.AsNoTracking()
                        join candidateRating in dbContext.CompanyRatings.AsNoTracking()
                            on candidateEvidence.CompanyRatingId equals candidateRating.Id
                        join candidateCycle in dbContext.MarketCycles.AsNoTracking()
                            on candidateRating.CreatedInCycleId equals candidateCycle.Id
                        where candidateEvidence.CompanyId == evidence.CompanyId
                            && candidateEvidence.EffectiveTradingDayNumber <= point.TradingDayNumber
                            && candidateCycle.MarketRunId == point.MarketRunId
                            && candidateCycle.CycleNumber <= point.CycleNumber
                            && (candidateEvidence.EffectiveTradingDayNumber
                                    > evidence.EffectiveTradingDayNumber
                                || (candidateEvidence.EffectiveTradingDayNumber
                                        == evidence.EffectiveTradingDayNumber
                                    && candidateEvidence.CompanyRatingId > evidence.CompanyRatingId))
                        select candidateEvidence.CompanyRatingId)
                    .Any()
                select new { Evidence = evidence, Rating = rating, ratingCycle.CycleNumber })
            .ToListAsync();
        var effectiveAudits = auditRows.ToDictionary(
            row => row.Evidence.CompanyId,
            row => new EffectiveAuditEvidenceRow(
                row.Rating,
                row.CycleNumber,
                row.Evidence,
                BuildEffectiveAuditEvidence(row.Rating.Rating, row.Evidence)));

        var eligibleFinancialSnapshots =
            from snapshot in dbContext.CompanyFinancialSnapshots.AsNoTracking()
            join snapshotCycle in dbContext.MarketCycles.AsNoTracking()
                on snapshot.CreatedInCycleId equals snapshotCycle.Id
            where companyIds.Contains(snapshot.CompanyId)
                && snapshot.TradingDayNumber <= point.TradingDayNumber
                && snapshotCycle.MarketRunId == point.MarketRunId
                && snapshotCycle.CycleNumber <= point.CycleNumber
            select snapshot;
        // Keeping top-two selection in SQL bounds returned rows while preserving the previous-checkpoint delta.
        var financialGroups = await eligibleFinancialSnapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .Select(group => group
                .OrderByDescending(snapshot => snapshot.Id)
                .Take(2)
                .ToList())
            .ToListAsync();
        var financialCheckpointsByCompany = financialGroups
            .SelectMany(group => group)
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(snapshot => snapshot.Id).ToList());

        var latestDividendRows = await (
                from dividend in dbContext.CompanyDividendEvents.AsNoTracking()
                join dividendCycle in dbContext.MarketCycles.AsNoTracking()
                    on dividend.CreatedInCycleId equals dividendCycle.Id
                where companyIds.Contains(dividend.CompanyId)
                    && dividend.TradingDayNumber <= point.TradingDayNumber
                    && dividendCycle.MarketRunId == point.MarketRunId
                    && dividendCycle.CycleNumber <= point.CycleNumber
                    && !(
                        from candidate in dbContext.CompanyDividendEvents.AsNoTracking()
                        join candidateCycle in dbContext.MarketCycles.AsNoTracking()
                            on candidate.CreatedInCycleId equals candidateCycle.Id
                        where candidate.CompanyId == dividend.CompanyId
                            && candidate.TradingDayNumber <= point.TradingDayNumber
                            && candidateCycle.MarketRunId == point.MarketRunId
                            && candidateCycle.CycleNumber <= point.CycleNumber
                            && candidate.Id > dividend.Id
                        select candidate.Id)
                    .Any()
                select dividend)
            .ToListAsync();
        var latestDividendByCompany = latestDividendRows.ToDictionary(dividend => dividend.CompanyId);
        var latestFinancials = financialCheckpointsByCompany.ToDictionary(
            entry => entry.Key,
            entry => BuildLatestFinancialEvidence(
                entry.Value[0],
                entry.Value.Count > 1 ? entry.Value[1] : null,
                latestDividendByCompany.GetValueOrDefault(entry.Key)));

        return new TradingEvidenceBatch(effectiveAudits, latestFinancials);
    }

    private static EffectiveAuditEvidence BuildEffectiveAuditEvidence(
        CompanyRiskRating rating,
        CompanyAuditEvidence evidence) =>
        new(
            rating,
            evidence.TotalScore,
            evidence.EvaluationStartTradingDayNumber,
            evidence.EvaluationEndTradingDayNumber,
            evidence.EffectiveTradingDayNumber,
            evidence.AdjustedReturnScore,
            evidence.CycleJumpScore,
            evidence.FreeShareEmissionScore,
            evidence.DenominationScore,
            evidence.DividendOutcomeScore,
            evidence.DividendCoverageScore,
            evidence.IndustryScore,
            evidence.ProfitabilityFactorScore,
            evidence.StabilityFactorScore,
            evidence.ClosureRiskFactorScore,
            evidence.ManagementOutlookFactorScore);

    private static LatestFinancialEvidence BuildLatestFinancialEvidence(
        CompanyFinancialSnapshot current,
        CompanyFinancialSnapshot? previous,
        CompanyDividendEvent? latestDividend) =>
        new(
            current.Id,
            current.TradingDayNumber,
            current.Moment,
            FinancialValues(current),
            FinancialDeltas(current, previous),
            current.ProfitabilityScore,
            current.ProfitabilityLevel,
            current.StabilityScore,
            current.FinancialVolatilityLevel,
            current.ClosureRiskScore,
            current.ClosureRiskLevel,
            current.ManagementOutlook,
            current.ManagementConfidenceScore,
            latestDividend?.FundingOutcome,
            latestDividend?.DeclaredAmount,
            latestDividend?.FundedAmount);

    private static CompanyFinancialValues FinancialValues(CompanyFinancialSnapshot snapshot) =>
        new(
            snapshot.Revenue,
            snapshot.NetProfit,
            snapshot.OperatingCashFlow,
            snapshot.TotalAssets,
            snapshot.TotalLiabilities,
            snapshot.TotalDebt,
            snapshot.ExpectedDividendPerShare,
            snapshot.ExpectedDividendPool,
            snapshot.DividendCoverageRatio,
            snapshot.BusinessRiskScore,
            snapshot.ManagementRevenueForecast,
            snapshot.ManagementProfitForecast,
            snapshot.ManagementOperatingCashFlowForecast);

    private static CompanyFinancialDeltas FinancialDeltas(
        CompanyFinancialSnapshot current,
        CompanyFinancialSnapshot? previous) =>
        previous is null
            ? new CompanyFinancialDeltas(0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m)
            : new CompanyFinancialDeltas(
                current.Revenue - previous.Revenue,
                current.NetProfit - previous.NetProfit,
                current.OperatingCashFlow - previous.OperatingCashFlow,
                current.TotalAssets - previous.TotalAssets,
                current.TotalLiabilities - previous.TotalLiabilities,
                current.TotalDebt - previous.TotalDebt,
                current.ExpectedDividendPerShare - previous.ExpectedDividendPerShare,
                current.ExpectedDividendPool - previous.ExpectedDividendPool,
                current.DividendCoverageRatio - previous.DividendCoverageRatio,
                current.BusinessRiskScore - previous.BusinessRiskScore,
                current.ManagementRevenueForecast - previous.ManagementRevenueForecast,
                current.ManagementProfitForecast - previous.ManagementProfitForecast,
                current.ManagementOperatingCashFlowForecast - previous.ManagementOperatingCashFlowForecast,
                current.ManagementConfidenceScore - previous.ManagementConfidenceScore);
}
