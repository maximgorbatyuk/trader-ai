using TraderAi.Models;

namespace TraderAi.Api;

public sealed record CompanyPositionResponse(
    int Shares,
    decimal OwnershipPct,
    decimal MarketValue);

public sealed record CompanyResponse(
    int Id,
    string Name,
    bool IsFavorite,
    int IndustryId,
    string? IndustryName,
    int IssuedSharesCount,
    decimal? CurrentPrice,
    decimal PriceChangePct,
    string? CurrentRating,
    bool IsHalted,
    string LuldState,
    string? LimitDirection,
    decimal? ReferencePrice,
    decimal? LowerBandPrice,
    decimal? UpperBandPrice,
    decimal? MinimumOrderPrice,
    decimal? MaximumOrderPrice,
    int? LimitStateStartedCycleNumber,
    int? PauseUntilCycleNumber,
    CompanyPositionResponse? PlayerPosition,
    CompanyPositionResponse? FundPosition);

public sealed record CompanyAttentionResponse(
    int CompanyId,
    string Name,
    string? IndustryName,
    decimal? CurrentPrice,
    decimal PriceChangePct,
    string? CurrentRating,
    int Shares,
    decimal MarketValue,
    bool PriceDeclining,
    bool BadNewsImpact,
    bool HighRisk,
    decimal? FinancialClosureRiskScore,
    bool RecentMerge);

public sealed record PagedCompaniesResponse(CompanyResponse[] Items, int Total, int Page, int PageSize);

public sealed record CompanyDetailResponse(
    int Id,
    string Name,
    bool IsFavorite,
    int IndustryId,
    string? IndustryName,
    int IssuedSharesCount,
    decimal? CurrentPrice,
    decimal PriceChangePct,
    decimal MarketCap,
    decimal IssuerCash,
    int SharesHeldByIssuer,
    int SharesOutstanding,
    int ShareholderCount,
    DateTime CreatedAt,
    string? CurrentRating,
    string? PreviousRating,
    bool IsClosed,
    int? ClosedInCycleNumber,
    bool IsHalted,
    int? HaltedUntilCycleNumber,
    string LuldState,
    string? LimitDirection,
    decimal? ReferencePrice,
    decimal? LowerBandPrice,
    decimal? UpperBandPrice,
    decimal? MinimumOrderPrice,
    decimal? MaximumOrderPrice,
    int? LimitStateStartedCycleNumber,
    int? PauseUntilCycleNumber,
    int RemainingPauseCycles,
    int RemainingPauseSeconds,
    CompanyFinancialSummaryResponse? LatestFinancial);

public sealed record CompanyDividendEventResponse(
    int Id,
    decimal DeclaredAmount,
    decimal FundedAmount,
    string FundingOutcome,
    decimal IssuerCashBeforeFunding,
    int CreatedInCycleId,
    int TradingDayNumber,
    DateTime CreatedAt);

public sealed record CompanyFinancialSummaryResponse(
    int Id,
    int CreatedInCycleId,
    int TradingDayNumber,
    string Moment,
    DateTime CreatedAt,
    decimal Revenue,
    decimal NetProfit,
    decimal OperatingCashFlow,
    decimal TotalAssets,
    decimal TotalLiabilities,
    decimal TotalDebt,
    decimal ExpectedDividendPerShare,
    decimal ExpectedDividendPool,
    decimal DividendCoverageRatio,
    CompanyDividendEventResponse? LatestDividend,
    decimal BusinessRiskScore,
    decimal ManagementRevenueForecast,
    decimal ManagementProfitForecast,
    decimal ManagementOperatingCashFlowForecast,
    string ManagementOutlook,
    decimal ManagementConfidenceScore,
    decimal ProfitabilityScore,
    string ProfitabilityLevel,
    decimal StabilityScore,
    string FinancialVolatilityLevel,
    decimal ClosureRiskScore,
    string ClosureRiskLevel,
    string ChangedMetrics);

public sealed record CompanyFinancialValuesResponse(
    decimal? Revenue,
    decimal? NetProfit,
    decimal? OperatingCashFlow,
    decimal? TotalAssets,
    decimal? TotalLiabilities,
    decimal? TotalDebt,
    decimal? ExpectedDividendPerShare,
    decimal? ExpectedDividendPool,
    decimal? DividendCoverageRatio,
    decimal? BusinessRiskScore,
    decimal? ManagementRevenueForecast,
    decimal? ManagementProfitForecast,
    decimal? ManagementOperatingCashFlowForecast,
    decimal? ManagementConfidenceScore,
    decimal? ProfitabilityScore,
    decimal? StabilityScore,
    decimal? ClosureRiskScore);

public sealed record CompanyFinancialHistoryItemResponse(
    CompanyFinancialSummaryResponse Current,
    CompanyFinancialValuesResponse? Previous,
    CompanyFinancialValuesResponse? AbsoluteDelta,
    CompanyFinancialValuesResponse? PercentageDelta);

public sealed record PagedCompanyFinancialsResponse(
    CompanyFinancialHistoryItemResponse[] Items,
    int Total,
    int Page,
    int PageSize);

public sealed record CorporateCashMovementResponse(
    int Id,
    string Type,
    decimal Amount,
    int CreatedInCycleId,
    int CreatedInCycleNumber,
    DateTime CreatedAt);

public sealed record PagedCorporateCashMovementsResponse(
    CorporateCashMovementResponse[] Items,
    int Total,
    int Page,
    int PageSize);

public sealed record ShareholderResponse(
    int OwnerId,
    string OwnerName,
    int Shares,
    decimal MarketValue,
    decimal CostBasis,
    decimal PctOfIssued);

public sealed record CompanyRatingResponse(
    int Id,
    string Rating,
    decimal? ImpactPercent,
    string AuditorName,
    int CyclesAgo,
    DateTime CreatedAt);

public sealed record ShareEmissionResponse(
    int Id,
    int SharesEmitted,
    int RecipientCount,
    int CyclesAgo,
    DateTime CreatedAt);

public sealed record ClosedCompanyResponse(
    int Id,
    string Name,
    int IndustryId,
    string? IndustryName,
    int IssuedSharesCount,
    decimal? FinalPrice,
    int CreatedInCycleNumber,
    int ClosedInCycleNumber,
    DateTime? ClosedAt);

public sealed record PagedClosedCompaniesResponse(ClosedCompanyResponse[] Items, int Total, int Page, int PageSize);

public sealed record AuditorResponse(int Id, string Name, string Description, int AuditCount);

public sealed record AuditRowResponse(
    int Id,
    int CompanyId,
    string CompanyName,
    string Rating,
    decimal? ImpactPercent,
    int CyclesAgo,
    DateTime CreatedAt);

public sealed record PagedAuditsResponse(AuditRowResponse[] Items, int Total, int Page, int PageSize);

public sealed record CompanyAuditSummaryResponse(
    int Id,
    int CompanyId,
    string CompanyName,
    string Rating,
    decimal? ImpactPercent,
    int AuditorId,
    string AuditorName,
    int CreatedInCycleId,
    int CreatedInCycleNumber,
    DateTime CreatedAt,
    bool EvidenceAvailable,
    int? EvaluationStartTradingDayNumber,
    int? EvaluationEndTradingDayNumber,
    int? EffectiveTradingDayNumber,
    int? TotalScore,
    decimal? AdjustedReturnPercent,
    decimal? MaximumAdjustedCycleMovePercent,
    string? LatestDividendOutcome,
    decimal? DividendCoverageRatio,
    string? IndustryTrend,
    int? ProfitabilityFactorScore,
    int? StabilityFactorScore,
    int? ClosureRiskFactorScore,
    int? ManagementOutlookFactorScore,
    CompanyAuditFinancialFactorsResponse? FinancialFactors);

public sealed record CompanyAuditFinancialFactorsResponse(
    int FinancialSnapshotId,
    decimal ProfitabilityScore,
    string ProfitabilityLevel,
    decimal StabilityScore,
    string FinancialVolatilityLevel,
    decimal ClosureRiskScore,
    string ClosureRiskLevel,
    string ManagementOutlook,
    decimal ManagementConfidenceScore);

public sealed record PagedCompanyAuditsResponse(
    CompanyAuditSummaryResponse[] Items,
    int Total,
    int Page,
    int PageSize);

public sealed record AuditDenominationEventResponse(
    int Id,
    string ActionType,
    int Ratio,
    int IssuedSharesBefore,
    int IssuedSharesAfter,
    decimal PriceBefore,
    decimal PriceAfter,
    int EffectiveInCycleId,
    int EffectiveInCycleNumber,
    int TradingDayNumber,
    DateTime CreatedAt);

public sealed record AuditShareEmissionEventResponse(
    int Id,
    int SharesEmitted,
    int RecipientCount,
    int CreatedInCycleId,
    int CreatedInCycleNumber,
    int TradingDayNumber,
    DateTime CreatedAt);

public sealed record CompanyAuditDetailResponse(
    int Id,
    int CompanyId,
    string CompanyName,
    string Rating,
    decimal? ImpactPercent,
    int AuditorId,
    string AuditorName,
    int CreatedInCycleId,
    int CreatedInCycleNumber,
    DateTime CreatedAt,
    bool EvidenceAvailable,
    int? EvaluationStartTradingDayNumber,
    int? EvaluationEndTradingDayNumber,
    int? EffectiveTradingDayNumber,
    int? TotalScore,
    int? AdjustedReturnScore,
    int? CycleJumpScore,
    int? FreeShareEmissionScore,
    int? DenominationScore,
    int? DividendOutcomeScore,
    int? DividendCoverageScore,
    int? IndustryScore,
    int? ProfitabilityFactorScore,
    int? StabilityFactorScore,
    int? ClosureRiskFactorScore,
    int? ManagementOutlookFactorScore,
    decimal? StartPrice,
    decimal? EndPrice,
    decimal? AdjustedReturnPercent,
    decimal? MaximumAdjustedCycleMovePercent,
    int? OpeningIssuedShares,
    int? EmittedShares,
    decimal? FreeShareDilutionPercent,
    int? StockSplitCount,
    int? ReverseSplitCount,
    CompanyDividendEventResponse? LatestDividend,
    decimal? IssuerCash,
    decimal? ModeledMaximumDividend,
    decimal? DividendCoverageRatio,
    int? OpeningIndustrySentiment,
    int? ClosingIndustrySentiment,
    string? IndustryTrend,
    CompanyFinancialSummaryResponse? Financial,
    AuditDenominationEventResponse[] DenominationEvents,
    AuditShareEmissionEventResponse[] FreeShareEmissionEvents);

public sealed record PortfolioAuditSummaryItemResponse(
    int Id,
    int CompanyId,
    string CompanyName,
    int CompanyRatingId,
    int PlayerQuantity,
    int ManagedFundQuantity,
    string Rating,
    int? TotalScore,
    decimal? AdjustedReturnPercent,
    decimal? DividendCoverageRatio,
    string? IndustryTrend);

public sealed record PortfolioAuditSummaryResponse(
    int Id,
    int NewsPostId,
    int EvaluationStartTradingDayNumber,
    int EvaluationEndTradingDayNumber,
    int EffectiveTradingDayNumber,
    int ExtraRaisedExpectationsCount,
    int RaisedExpectationsCount,
    int StableCount,
    int LowRiskCount,
    int HighRiskCount,
    decimal AverageScore,
    string OverallDirection,
    DateTime CreatedAt,
    PortfolioAuditSummaryItemResponse[] Items);
