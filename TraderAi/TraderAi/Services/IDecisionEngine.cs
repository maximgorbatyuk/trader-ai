using TraderAi.Models;

namespace TraderAi.Services;

public sealed record EffectiveAuditEvidence(
    CompanyRiskRating Rating,
    int TotalScore,
    int EvaluationStartTradingDayNumber,
    int EvaluationEndTradingDayNumber,
    int EffectiveTradingDayNumber,
    int AdjustedReturnScore,
    int CycleJumpScore,
    int FreeShareEmissionScore,
    int DenominationScore,
    int DividendOutcomeScore,
    int DividendCoverageScore,
    int IndustryScore,
    int ProfitabilityFactorScore,
    int StabilityFactorScore,
    int ClosureRiskFactorScore,
    int ManagementOutlookFactorScore);

public sealed record CompanyFinancialValues(
    decimal Revenue,
    decimal NetProfit,
    decimal OperatingCashFlow,
    decimal TotalAssets,
    decimal TotalLiabilities,
    decimal TotalDebt,
    decimal ExpectedDividendPerShare,
    decimal ExpectedDividendPool,
    decimal DividendCoverageRatio,
    decimal BusinessRiskScore,
    decimal ManagementRevenueForecast,
    decimal ManagementProfitForecast,
    decimal ManagementOperatingCashFlowForecast);

public sealed record CompanyFinancialDeltas(
    decimal Revenue,
    decimal NetProfit,
    decimal OperatingCashFlow,
    decimal TotalAssets,
    decimal TotalLiabilities,
    decimal TotalDebt,
    decimal ExpectedDividendPerShare,
    decimal ExpectedDividendPool,
    decimal DividendCoverageRatio,
    decimal BusinessRiskScore,
    decimal ManagementRevenueForecast,
    decimal ManagementProfitForecast,
    decimal ManagementOperatingCashFlowForecast,
    decimal ManagementConfidenceScore);

public sealed record LatestFinancialEvidence(
    int SnapshotId,
    int TradingDayNumber,
    CompanyFinancialSnapshotMoment Moment,
    CompanyFinancialValues Current,
    CompanyFinancialDeltas Deltas,
    decimal ProfitabilityScore,
    CompanyMetricLevel ProfitabilityLevel,
    decimal StabilityScore,
    CompanyMetricLevel FinancialVolatilityLevel,
    decimal ClosureRiskScore,
    CompanyMetricLevel ClosureRiskLevel,
    ManagementOutlook ManagementOutlook,
    decimal ManagementConfidenceScore,
    DividendFundingOutcome? LatestDividendOutcome,
    decimal? LatestDividendDeclaredAmount,
    decimal? LatestDividendFundedAmount);

// Direction evidence stays normalized or compact and immutable; execution limits and available liquidity
// remain separate fields so portfolio constraints cannot be mistaken for a price forecast.
public sealed record CompanyQuote(
    int CompanyId,
    decimal Price,
    decimal PriceChangePct = 0m,
    decimal OrderFlowImbalance = 0m,
    decimal LongRangeChangePct = 0m,
    int SectorSentiment = 0,
    OrderPriceBounds? Bounds = null,
    int IssuedShares = 0,
    decimal? BestExecutableSellPrice = null,
    int BestExecutableSellQuantity = 0,
    bool IndividualBuyBlockedForBatch = false,
    int OpenSellQuantity = 0,
    EffectiveAuditEvidence? Audit = null,
    LatestFinancialEvidence? Financials = null);

// Everything a decision engine needs for one participant, supplied by the caller so the engine
// stays a pure function with no database access. CrisisActive is set while a market crisis window is open,
// which pulls conservative and low-risk traders back from buying. LoanLiability leans a trader toward selling;
// the optional financial fields let the automated-buy policy run while legacy contexts keep their old behavior.
public sealed record DecisionContext(
    Participant Participant,
    decimal AvailableCash,
    IReadOnlyList<CompanyQuote> Companies,
    IReadOnlyDictionary<int, int> SharesOwnedByCompany,
    IReadOnlySet<int> CompaniesWithOpenOrders,
    bool CrisisActive = false,
    decimal LoanLiability = 0m,
    decimal HoldingsValue = 0m,
    decimal NetWorth = 0m,
    decimal AvailableBalance = 0m,
    decimal BuyingPower = 0m,
    decimal MarginLiability = 0m,
    decimal ReservedBuyNotional = 0m,
    bool HasAutomatedTradingData = false);

public sealed record OrderIntent(OrderType Type, int CompanyId, int Quantity, decimal LimitPrice);

public interface IDecisionEngine
{
    IReadOnlyList<OrderIntent> Decide(DecisionContext context);
}
