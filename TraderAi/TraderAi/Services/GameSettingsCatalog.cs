using System.Text;
using Microsoft.Extensions.Configuration;

namespace TraderAi.Services;

public enum GameSettingValueType
{
    Boolean,
    Integer,
    Decimal,
    Text,
    Url,
    StringList,
    MultilineText,
    Secret,
}

public sealed record GameSettingDefinition(
    string Key,
    string Section,
    string? Subsection,
    string Name,
    string Description,
    GameSettingValueType ValueType);

public static class GameSettingsCatalog
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedLeafNamesBySection =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["MarketLoop"] = ["Enabled", "IntervalSeconds"],
            ["TradingClock"] = ["TradingCyclesPerDay", "TradingCycleSeconds", "BreakDurationSeconds"],
            ["Settlement"] = ["SettlementLagTradingDays"],
            ["Margin"] =
            [
                "Enabled", "InitialMarginRate", "MaintenanceMarginRate", "DailyInterestRate",
                "MaintenanceBufferRate", "ForcedSaleDiscountRate",
            ],
            ["AutomatedTrading"] =
            [
                "LowMinimumExposurePercent", "LowMaximumExposurePercent", "LowMaximumOrderNotionalPercent",
                "MediumMinimumExposurePercent", "MediumMaximumExposurePercent", "MediumMaximumOrderNotionalPercent",
                "HighMinimumExposurePercent", "HighMaximumExposurePercent", "HighMaximumOrderNotionalPercent",
                "MaximumIssuedSharesPerOrderPercent", "MaximumPassiveBidIssuedSharesPercent",
                "MinimumMeaningfulQuantityPercent", "MaximumHighRiskMarginLiabilityPercent",
                "PassiveBuyPremiumMinPercent", "PassiveBuyPremiumMaxPercent",
                "BuyOrdersPerCycleMin", "BuyOrdersPerCycleMax",
            ],
            ["News"] = ["Enabled", "CyclesBetweenPosts"],
            ["Crisis"] = ["Enabled"],
            ["ScienceInvestigation"] = ["Enabled"],
            ["Bankruptcy"] = ["Enabled"],
            ["CollectiveFund"] =
            [
                "Enabled", "MinimumMembershipTradingDays", "CashBufferFraction", "PreLeaveCashBufferFraction",
                "JoinBalanceCeiling", "FundGrowthWindowCycles", "MaxMembers", "SoftCloseMembers",
                "ManagerProfitFeeEnabled", "ManagerProfitFeeShare",
            ],
            ["MarketExit"] = ["Enabled"],
            ["StockSplit"] = ["Enabled"],
            ["CompanyFinancial"] =
            [
                "Enabled", "StabilityWindowSnapshots",
                "ProfitabilityNetMarginWeight", "ProfitabilityReturnOnAssetsWeight",
                "ProfitabilityCashFlowWeight", "ProfitabilityOperatingTrendWeight",
                "ProfitabilityManagementOutlookWeight",
                "ClosureRiskEarningsAndCashFlowWeight", "ClosureRiskLeverageWeight",
                "ClosureRiskLiabilitiesWeight", "ClosureRiskBusinessWeight", "ClosureRiskIndustryWeight",
                "LowLevelMaximumScore", "HighLevelMinimumScore",
                "MaximumLiabilitiesToAssetsRatio", "MaximumDebtToLiabilitiesRatio",
                "MaximumForecastDeviationRatio", "IndustryImpulseWeight",
                "MinimumExpectedDividendCoverageRatio", "MaximumExpectedDividendPayoutRatio",
            ],
            ["TradingSignal"] =
            [
                "MomentumWeight", "OrderFlowWeight", "IndustryWeight", "AuditWeight", "FundamentalWeight",
                "EvidenceWeight", "PersonalityNoiseWeight", "MinimumWaitWeight",
                "AggressiveActivityFactor", "BalancedActivityFactor", "ConservativeActivityFactor",
                "LowRiskQualityResponseFactor", "LowRiskGrowthResponseFactor",
                "MediumRiskQualityResponseFactor", "MediumRiskGrowthResponseFactor",
                "HighRiskQualityResponseFactor", "HighRiskGrowthResponseFactor",
            ],
            ["Auditor"] =
            [
                "Enabled", "AuditIntervalTradingDays",
                "ModerateAdjustedReturnPercent", "StrongAdjustedReturnPercent",
                "ModerateCycleJumpPercent", "StrongCycleJumpPercent",
                "ModerateFreeShareDilutionPercent", "IndustryDirectionDeadband",
                "StrongPositiveReturnScore", "ModeratePositiveReturnScore",
                "ModerateNegativeReturnScore", "StrongNegativeReturnScore",
                "ModerateCycleJumpScore", "StrongCycleJumpScore",
                "ModerateFreeShareEmissionScore", "StrongFreeShareEmissionScore",
                "StockSplitScore", "ReverseSplitScore",
                "DividendPaidScore", "DividendReducedScore", "DividendSkippedScore",
                "DividendCoveredScore", "DividendUncoveredScore",
                "IndustryRisingScore", "IndustryFallingScore",
                "HighProfitabilityScore", "MediumProfitabilityScore", "LowProfitabilityScore",
                "LowVolatilityScore", "MediumVolatilityScore", "HighVolatilityScore",
                "LowClosureRiskScore", "MediumClosureRiskScore", "HighClosureRiskScore",
                "PositiveManagementOutlookScore", "NeutralManagementOutlookScore",
                "NegativeManagementOutlookScore",
                "MinimumDenominationScore", "MaximumDenominationScore",
                "MinimumTotalScore", "MaximumTotalScore",
                "ExtraRaisedExpectationsThreshold", "RaisedExpectationsThreshold",
                "LowRiskThreshold", "HighRiskThreshold",
                "ModerateDecisionPull", "StrongDecisionPull",
            ],
            ["ShareEmission"] = ["Enabled"],
            ["BigInvestment"] = ["Enabled"],
            ["PrimaryIssuance"] = ["Enabled", "FloatScarcityThresholdPercent", "MaximumDailyIssuancePercent"],
            ["CompanyLifecycle"] = ["Enabled"],
            ["Loan"] = ["Enabled"],
            ["TradeFee"] = ["Enabled", "FeeRate"],
            ["VolatilityHalt"] =
            [
                "Enabled", "ReferenceWindowSeconds", "LimitStateDurationSeconds", "TradingPauseDurationSeconds",
                "UpperBandPercent", "LowerBandPercent", "AllowedOrderLowerPercent", "AllowedOrderUpperPercent",
                "DemandRatchetStepPercent",
            ],
            ["ConcentrationCap"] = ["Enabled", "MaxSingleCompanyWeightPercent", "PriceCutPercent"],
            ["IndustrySentiment"] =
            [
                "Enabled", "SentimentValueMin", "SentimentValueMax", "SentimentVolatilityMin",
                "SentimentVolatilityMax", "SectorBetaMin", "SectorBetaMax", "SentimentValueLimit",
                "SentimentDecayPerCycle",
            ],
            ["AiTrading"] =
            [
                "Enabled", "ScanIntervalMilliseconds", "RequestTimeoutSeconds", "MaxResponseTokens", "MaxConcurrentRequests",
                "MaxOrdersPerDecision", "PredictionHorizonCycles", "MaxPredictionsPerDecision", "HistoryCycles", "RetryBaseDelaySeconds", "RetryMaxDelaySeconds",
                "AuthErrorRetrySeconds", "MaxInvalidJsonRetries", "MaxTransportRetries", "SystemPromptTemplate", "FinalDecisionInstruction",
            ],
            ["RandomChanceRates"] = [],
        };

    private static readonly HashSet<string> AllowedProviderLeafNames =
    [
        "DisplayName", "Endpoint", "ApiKey", "Models", "RequestTimeoutSeconds", "MaxResponseTokens",
        "MaxInvalidJsonRetries", "MaxTransportRetries",
    ];

    private static readonly HashSet<string> AllowedProviderIntegerLeafNames =
    [
        "RequestTimeoutSeconds", "MaxResponseTokens", "MaxInvalidJsonRetries", "MaxTransportRetries",
    ];

    private static readonly HashSet<string> AllowedRandomChanceRateKeys =
    [
        "EventTriggerChances:FundJoin",
        "EventTriggerChances:FundOpen",
        "EventTriggerChances:FundLeaveBase",
        "EventTriggerChances:FundLeaveMax",
        "EventTriggerChances:FundSwitchBase",
        "EventTriggerChances:BankruptcyMax",
        "EventTriggerChances:BankruptcyPerDebtPercent",
        "EventTriggerChances:CompanyAppearanceHigh",
        "EventTriggerChances:CompanyAppearanceMid",
        "EventTriggerChances:CompanyAppearanceLow",
        "EventTriggerChances:ExitStarvationBase",
        "EventTriggerChances:ExitFundLoss",
        "EventTriggerChances:TraderAppearanceBase",
        "EventTriggerChances:LocalCrisisStepPerTradingDay",
        "EventTriggerChances:GlobalCrisisStepPerTradingDay",
        "EventTriggerChances:ScienceStepPerCycle",
        "EventTriggerChances:ShareEmissionPerBand",
        "EventTriggerChances:ShareEmissionMax",
        "EventTriggerChances:DividendStableCapitalization",
        "EventTriggerChances:DividendVolatileCapitalization",
        "EventTriggerChances:NewsImpact",
        "EventTriggerChances:NewsCompanyScope",
        "EventTriggerChances:IndustrySentimentRevisionBase",
        "EventTriggerChances:ScienceSentimentPush",
        "EventTriggerChances:OutsideBandOrder",
        "EventTriggerChances:NoSellOrderBuyChance",
        "EventTriggerChances:BigInvestment",
        "EventTriggerChances:BigInvestmentMax",
        "EventTriggerChances:FinancialMetricChange",
        "ChanceModifiers:CrisisBankruptcyMultiplier",
        "ChanceModifiers:GlobalCrisisExitMultiplier",
        "ChanceModifiers:LocalCrisisExitMultiplier",
        "ChanceModifiers:CrisisScienceChanceFactor",
        "ChanceModifiers:CrisisNewsIncreaseSuppression",
        "ChanceModifiers:CrisisBuySuppression",
        "ChanceModifiers:CompanyClosureAppearanceBoost",
        "ChanceModifiers:FundGrowthJoinBonus",
        "ChanceModifiers:IndustrySentimentVolatilityFactor",
        "ChanceModifiers:CrisisSentimentForcedDown",
        "ChanceModifiers:CompanyNewsSentimentBonus",
        "RandomMagnitudeBands:DividendRateMin",
        "RandomMagnitudeBands:DividendRateMax",
        "RandomMagnitudeBands:PrimaryIssuanceRateMin",
        "RandomMagnitudeBands:PrimaryIssuanceRateMax",
        "RandomMagnitudeBands:ShareEmissionRateMin",
        "RandomMagnitudeBands:ShareEmissionRateMax",
        "RandomMagnitudeBands:CrisisIndustryDropMinPercent",
        "RandomMagnitudeBands:CrisisIndustryDropMaxPercent",
        "RandomMagnitudeBands:ScienceIndustryLiftMinPercent",
        "RandomMagnitudeBands:ScienceIndustryLiftMaxPercent",
        "RandomMagnitudeBands:GlobalCrisisIndustryShareMin",
        "RandomMagnitudeBands:GlobalCrisisIndustryShareMax",
        "RandomMagnitudeBands:BigInvestmentFractionMin",
        "RandomMagnitudeBands:BigInvestmentFractionMax",
        "RandomMagnitudeBands:FinancialSeedAssetsToMarketCapMin",
        "RandomMagnitudeBands:FinancialSeedAssetsToMarketCapMax",
        "RandomMagnitudeBands:FinancialSeedRevenueToAssetsMin",
        "RandomMagnitudeBands:FinancialSeedRevenueToAssetsMax",
        "RandomMagnitudeBands:FinancialSeedNetMarginMin",
        "RandomMagnitudeBands:FinancialSeedNetMarginMax",
        "RandomMagnitudeBands:FinancialSeedOperatingCashFlowToProfitMin",
        "RandomMagnitudeBands:FinancialSeedOperatingCashFlowToProfitMax",
        "RandomMagnitudeBands:FinancialSeedLiabilitiesToAssetsMin",
        "RandomMagnitudeBands:FinancialSeedLiabilitiesToAssetsMax",
        "RandomMagnitudeBands:FinancialSeedDebtToLiabilitiesMin",
        "RandomMagnitudeBands:FinancialSeedDebtToLiabilitiesMax",
        "RandomMagnitudeBands:FinancialSeedExpectedDividendYieldMin",
        "RandomMagnitudeBands:FinancialSeedExpectedDividendYieldMax",
        "RandomMagnitudeBands:FinancialSeedBusinessRiskScoreMin",
        "RandomMagnitudeBands:FinancialSeedBusinessRiskScoreMax",
        "RandomMagnitudeBands:FinancialSeedManagementForecastDeviationMin",
        "RandomMagnitudeBands:FinancialSeedManagementForecastDeviationMax",
        "RandomMagnitudeBands:FinancialSeedManagementConfidenceMin",
        "RandomMagnitudeBands:FinancialSeedManagementConfidenceMax",
        "RandomMagnitudeBands:FinancialOperatingUpdateMin",
        "RandomMagnitudeBands:FinancialOperatingUpdateMax",
        "RandomMagnitudeBands:FinancialBalanceSheetUpdateMin",
        "RandomMagnitudeBands:FinancialBalanceSheetUpdateMax",
        "RandomMagnitudeBands:FinancialDividendUpdateMin",
        "RandomMagnitudeBands:FinancialDividendUpdateMax",
        "RandomMagnitudeBands:FinancialRiskScoreUpdateMin",
        "RandomMagnitudeBands:FinancialRiskScoreUpdateMax",
        "RandomMagnitudeBands:FinancialForecastUpdateMin",
        "RandomMagnitudeBands:FinancialForecastUpdateMax",
        "RandomMagnitudeBands:PassivePriceOffsetMinPercent",
        "RandomMagnitudeBands:PassivePriceOffsetMaxPercent",
    ];

    private static readonly HashSet<string> IntegerKeys =
    [
        "MarketLoop:IntervalSeconds",
        "TradingClock:TradingCyclesPerDay",
        "TradingClock:TradingCycleSeconds",
        "TradingClock:BreakDurationSeconds",
        "Settlement:SettlementLagTradingDays",
        "Auditor:AuditIntervalTradingDays",
        "CompanyFinancial:StabilityWindowSnapshots",
        "Auditor:StrongPositiveReturnScore",
        "Auditor:ModeratePositiveReturnScore",
        "Auditor:ModerateNegativeReturnScore",
        "Auditor:StrongNegativeReturnScore",
        "Auditor:ModerateCycleJumpScore",
        "Auditor:StrongCycleJumpScore",
        "Auditor:ModerateFreeShareEmissionScore",
        "Auditor:StrongFreeShareEmissionScore",
        "Auditor:StockSplitScore",
        "Auditor:ReverseSplitScore",
        "Auditor:DividendPaidScore",
        "Auditor:DividendReducedScore",
        "Auditor:DividendSkippedScore",
        "Auditor:DividendCoveredScore",
        "Auditor:DividendUncoveredScore",
        "Auditor:IndustryRisingScore",
        "Auditor:IndustryFallingScore",
        "Auditor:HighProfitabilityScore",
        "Auditor:MediumProfitabilityScore",
        "Auditor:LowProfitabilityScore",
        "Auditor:LowVolatilityScore",
        "Auditor:MediumVolatilityScore",
        "Auditor:HighVolatilityScore",
        "Auditor:LowClosureRiskScore",
        "Auditor:MediumClosureRiskScore",
        "Auditor:HighClosureRiskScore",
        "Auditor:PositiveManagementOutlookScore",
        "Auditor:NeutralManagementOutlookScore",
        "Auditor:NegativeManagementOutlookScore",
        "Auditor:MinimumDenominationScore",
        "Auditor:MaximumDenominationScore",
        "Auditor:MinimumTotalScore",
        "Auditor:MaximumTotalScore",
        "Auditor:ExtraRaisedExpectationsThreshold",
        "Auditor:RaisedExpectationsThreshold",
        "Auditor:LowRiskThreshold",
        "Auditor:HighRiskThreshold",
        "News:CyclesBetweenPosts",
        "CollectiveFund:MinimumMembershipTradingDays",
        "CollectiveFund:FundGrowthWindowCycles",
        "CollectiveFund:MaxMembers",
        "CollectiveFund:SoftCloseMembers",
        "VolatilityHalt:ReferenceWindowSeconds",
        "VolatilityHalt:LimitStateDurationSeconds",
        "VolatilityHalt:TradingPauseDurationSeconds",
        "IndustrySentiment:SentimentValueMin",
        "IndustrySentiment:SentimentValueMax",
        "IndustrySentiment:SentimentValueLimit",
        "IndustrySentiment:SentimentDecayPerCycle",
        "AiTrading:ScanIntervalMilliseconds",
        "AiTrading:RequestTimeoutSeconds",
        "AiTrading:MaxResponseTokens",
        "AiTrading:MaxConcurrentRequests",
        "AiTrading:MaxOrdersPerDecision",
        "AiTrading:PredictionHorizonCycles",
        "AiTrading:MaxPredictionsPerDecision",
        "AiTrading:HistoryCycles",
        "AiTrading:RetryBaseDelaySeconds",
        "AiTrading:RetryMaxDelaySeconds",
        "AiTrading:AuthErrorRetrySeconds",
        "AiTrading:MaxInvalidJsonRetries",
        "AiTrading:MaxTransportRetries",
        "AutomatedTrading:BuyOrdersPerCycleMin",
        "AutomatedTrading:BuyOrdersPerCycleMax",
    ];

    private static readonly HashSet<string> MultilineTextKeys =
    [
        "AiTrading:SystemPromptTemplate",
        "AiTrading:FinalDecisionInstruction",
    ];

    private static readonly IReadOnlyDictionary<string, string> Descriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MarketLoop:Enabled"] = "Controls whether the background market loop is allowed to advance a running market.",
            ["MarketLoop:IntervalSeconds"] = "Sets the delay between automatic market-cycle attempts.",
            ["TradingClock:TradingCyclesPerDay"] = "Sets how many trading cycles make up one simulated trading day.",
            ["TradingClock:TradingCycleSeconds"] = "Sets the simulated duration and loop cadence of one trading cycle.",
            ["TradingClock:BreakDurationSeconds"] = "Sets the non-trading break between simulated trading days.",
            ["Settlement:SettlementLagTradingDays"] = "Sets how many trading days pass before executed cash and shares become settled.",
            ["Auditor:AuditIntervalTradingDays"] = "Sets the number of trading days between evidence-backed company audits.",
            ["Auditor:ModerateAdjustedReturnPercent"] = "Sets the adjusted return threshold for a moderate audit score.",
            ["Auditor:StrongAdjustedReturnPercent"] = "Sets the adjusted return threshold for a strong audit score.",
            ["Auditor:ModerateCycleJumpPercent"] = "Sets the adjusted single-cycle move that starts reducing an audit score.",
            ["Auditor:StrongCycleJumpPercent"] = "Sets the adjusted single-cycle move that applies the strongest volatility penalty.",
            ["Auditor:ModerateFreeShareDilutionPercent"] = "Sets the free-share dilution boundary between moderate and strong audit penalties.",
            ["Auditor:IndustryDirectionDeadband"] = "Sets the sentiment-point range treated as a plateau during an audit.",
            ["Auditor:HighProfitabilityScore"] = "Sets the audit contribution for high company profitability.",
            ["Auditor:MediumProfitabilityScore"] = "Sets the audit contribution for medium company profitability.",
            ["Auditor:LowProfitabilityScore"] = "Sets the audit contribution for low company profitability.",
            ["Auditor:LowVolatilityScore"] = "Sets the audit contribution for low financial volatility.",
            ["Auditor:MediumVolatilityScore"] = "Sets the audit contribution for medium financial volatility.",
            ["Auditor:HighVolatilityScore"] = "Sets the audit contribution for high financial volatility.",
            ["Auditor:LowClosureRiskScore"] = "Sets the audit contribution for low future-closure risk.",
            ["Auditor:MediumClosureRiskScore"] = "Sets the audit contribution for medium future-closure risk.",
            ["Auditor:HighClosureRiskScore"] = "Sets the audit contribution for high future-closure risk.",
            ["Auditor:PositiveManagementOutlookScore"] = "Sets the maximum confidence-weighted contribution for positive management guidance.",
            ["Auditor:NeutralManagementOutlookScore"] = "Sets the maximum confidence-weighted contribution for neutral management guidance.",
            ["Auditor:NegativeManagementOutlookScore"] = "Sets the maximum confidence-weighted contribution for negative management guidance.",
            ["Auditor:MinimumDenominationScore"] = "Sets the lower clamp for the combined split and reverse-split contribution.",
            ["Auditor:MaximumDenominationScore"] = "Sets the upper clamp for the combined split and reverse-split contribution.",
            ["Auditor:MinimumTotalScore"] = "Sets the lower clamp for the final audit score.",
            ["Auditor:MaximumTotalScore"] = "Sets the upper clamp for the final audit score.",
            ["Auditor:ModerateDecisionPull"] = "Sets the advisory trading-decision adjustment for moderate audit outlooks.",
            ["Auditor:StrongDecisionPull"] = "Sets the advisory trading-decision adjustment for strong audit outlooks.",
            ["CompanyFinancial:StabilityWindowSnapshots"] = "Sets how many financial-history snapshots determine the company's stability and volatility.",
            ["CompanyFinancial:ProfitabilityNetMarginWeight"] = "Sets the net-margin share of the profitability score.",
            ["CompanyFinancial:ProfitabilityReturnOnAssetsWeight"] = "Sets the return-on-assets share of the profitability score.",
            ["CompanyFinancial:ProfitabilityCashFlowWeight"] = "Sets the operating-cash-flow share of the profitability score.",
            ["CompanyFinancial:ProfitabilityOperatingTrendWeight"] = "Sets the combined revenue-and-profit trend share of the profitability score.",
            ["CompanyFinancial:ProfitabilityManagementOutlookWeight"] = "Sets the management-outlook share of the profitability score.",
            ["CompanyFinancial:ClosureRiskEarningsAndCashFlowWeight"] = "Sets the earnings and cash-flow share of the future-closure risk score.",
            ["CompanyFinancial:ClosureRiskLeverageWeight"] = "Sets the debt-leverage share of the future-closure risk score.",
            ["CompanyFinancial:ClosureRiskLiabilitiesWeight"] = "Sets the liabilities share of the future-closure risk score.",
            ["CompanyFinancial:ClosureRiskBusinessWeight"] = "Sets the company-specific business-risk share of the future-closure score.",
            ["CompanyFinancial:ClosureRiskIndustryWeight"] = "Sets the industry-condition share of the future-closure risk score.",
            ["CompanyFinancial:LowLevelMaximumScore"] = "Sets the highest score classified as Low.",
            ["CompanyFinancial:HighLevelMinimumScore"] = "Sets the lowest score classified as High.",
            ["CompanyFinancial:MaximumLiabilitiesToAssetsRatio"] = "Caps liabilities relative to assets when financial values are updated.",
            ["CompanyFinancial:MaximumDebtToLiabilitiesRatio"] = "Caps debt relative to total liabilities when financial values are updated.",
            ["CompanyFinancial:MaximumForecastDeviationRatio"] = "Caps a management forecast's deviation from its current financial value.",
            ["CompanyFinancial:IndustryImpulseWeight"] = "Sets how strongly the industry's direction influences company financial changes.",
            ["CompanyFinancial:MinimumExpectedDividendCoverageRatio"] = "Sets the minimum cash-flow coverage required for an expected dividend to be considered covered.",
            ["CompanyFinancial:MaximumExpectedDividendPayoutRatio"] = "Caps expected dividends relative to eligible profit and operating cash flow.",
            ["TradingSignal:MomentumWeight"] = "Sets the recent-price-momentum share of directional evidence.",
            ["TradingSignal:OrderFlowWeight"] = "Sets the normalized participant order-flow share of directional evidence.",
            ["TradingSignal:IndustryWeight"] = "Sets the industry-direction share of directional evidence.",
            ["TradingSignal:AuditWeight"] = "Sets the effective-audit share of directional evidence.",
            ["TradingSignal:FundamentalWeight"] = "Sets the company-fundamentals share of directional evidence.",
            ["TradingSignal:EvidenceWeight"] = "Sets the action-distribution share derived from directional evidence.",
            ["TradingSignal:PersonalityNoiseWeight"] = "Sets the action-distribution share derived from temperament noise.",
            ["TradingSignal:MinimumWaitWeight"] = "Keeps waiting available before eligible actions are normalized; the value must be above 0 and no more than 1.",
            ["TradingSignal:AggressiveActivityFactor"] = "Scales trading activity for aggressive participants within the allowed range above 0 and no more than 5.",
            ["TradingSignal:BalancedActivityFactor"] = "Scales trading activity for balanced participants within the allowed range above 0 and no more than 5.",
            ["TradingSignal:ConservativeActivityFactor"] = "Scales trading activity for conservative participants within the allowed range above 0 and no more than 5.",
            ["TradingSignal:LowRiskQualityResponseFactor"] = "Scales low-risk participants' response to quality and stability within the allowed range above 0 and no more than 5.",
            ["TradingSignal:LowRiskGrowthResponseFactor"] = "Scales low-risk participants' response to growth and guidance within the allowed range above 0 and no more than 5.",
            ["TradingSignal:MediumRiskQualityResponseFactor"] = "Scales medium-risk participants' response to quality and stability within the allowed range above 0 and no more than 5.",
            ["TradingSignal:MediumRiskGrowthResponseFactor"] = "Scales medium-risk participants' response to growth and guidance within the allowed range above 0 and no more than 5.",
            ["TradingSignal:HighRiskQualityResponseFactor"] = "Scales high-risk participants' response to quality and stability within the allowed range above 0 and no more than 5.",
            ["TradingSignal:HighRiskGrowthResponseFactor"] = "Scales high-risk participants' response to growth and guidance within the allowed range above 0 and no more than 5.",
            ["Margin:Enabled"] = "Controls whether participants can use margin accounts.",
            ["Margin:InitialMarginRate"] = "Sets the portion of a margin purchase that must be covered by the participant's own equity.",
            ["Margin:MaintenanceMarginRate"] = "Sets the minimum equity ratio required to avoid a margin call.",
            ["Margin:DailyInterestRate"] = "Sets the interest charged on outstanding margin debit each trading day.",
            ["Margin:MaintenanceBufferRate"] = "Adds a safety buffer when calculating the amount a margin call must restore.",
            ["Margin:ForcedSaleDiscountRate"] = "Sets the discount used when placing forced-sale orders for a margin call.",
            ["News:Enabled"] = "Controls whether the simulation publishes generated market news.",
            ["News:CyclesBetweenPosts"] = "Sets the minimum cycle cadence between generated news posts.",
            ["CollectiveFund:MinimumMembershipTradingDays"] = "Sets how long a member must remain in a fund before leaving voluntarily.",
            ["CollectiveFund:CashBufferFraction"] = "Sets the share of fund worth kept liquid during ordinary automated trading.",
            ["CollectiveFund:PreLeaveCashBufferFraction"] = "Sets the higher liquid reserve used before a member becomes eligible to leave.",
            ["CollectiveFund:JoinBalanceCeiling"] = "Limits fund-joining candidates to traders below this cash balance.",
            ["CollectiveFund:FundGrowthWindowCycles"] = "Sets the history window used to measure fund growth.",
            ["CollectiveFund:MaxMembers"] = "Sets the hard maximum number of members in a collective fund.",
            ["CollectiveFund:SoftCloseMembers"] = "Sets the membership level at which a fund stops accepting new members.",
            ["CollectiveFund:ManagerProfitFeeShare"] = "Sets the manager's share of eligible member profit when the fee is enabled.",
            ["PrimaryIssuance:FloatScarcityThresholdPercent"] = "Defines when remaining issuer float is scarce enough to permit primary issuance.",
            ["PrimaryIssuance:MaximumDailyIssuancePercent"] = "Caps primary issuance as a percentage of issued shares per trading day.",
            ["TradeFee:FeeRate"] = "Sets the fee charged on eligible secondary-market trades.",
            ["VolatilityHalt:ReferenceWindowSeconds"] = "Sets the rolling time window used to establish the LULD reference price.",
            ["VolatilityHalt:LimitStateDurationSeconds"] = "Sets how long a security may remain in limit state before trading pauses.",
            ["VolatilityHalt:TradingPauseDurationSeconds"] = "Sets the duration of a LULD trading pause.",
            ["VolatilityHalt:UpperBandPercent"] = "Sets the allowed percentage above the LULD reference price.",
            ["VolatilityHalt:LowerBandPercent"] = "Sets the allowed percentage below the LULD reference price.",
            ["VolatilityHalt:AllowedOrderLowerPercent"] = "Sets the lowest order price allowed relative to the current market price.",
            ["VolatilityHalt:AllowedOrderUpperPercent"] = "Sets the highest order price allowed relative to the current market price.",
            ["VolatilityHalt:DemandRatchetStepPercent"] = "Sets the price-reference step used when excess demand persists at the upper band.",
            ["ConcentrationCap:MaxSingleCompanyWeightPercent"] = "Sets the maximum share of total market capitalization one company may represent.",
            ["ConcentrationCap:PriceCutPercent"] = "Sets the price reduction applied to a company above the concentration cap.",
            ["IndustrySentiment:SentimentValueMin"] = "Sets the lowest sentiment value assigned when an industry is created.",
            ["IndustrySentiment:SentimentValueMax"] = "Sets the highest sentiment value assigned when an industry is created.",
            ["IndustrySentiment:SentimentVolatilityMin"] = "Sets the lowest sentiment volatility assigned to an industry.",
            ["IndustrySentiment:SentimentVolatilityMax"] = "Sets the highest sentiment volatility assigned to an industry.",
            ["IndustrySentiment:SectorBetaMin"] = "Sets the lowest sensitivity of an industry to external market events.",
            ["IndustrySentiment:SectorBetaMax"] = "Sets the highest sensitivity of an industry to external market events.",
            ["IndustrySentiment:SentimentValueLimit"] = "Caps the absolute sentiment value an industry can reach.",
            ["IndustrySentiment:SentimentDecayPerCycle"] = "Sets how quickly industry sentiment moves back toward neutral each cycle.",
            ["AiTrading:Enabled"] = "Controls whether configured AI traders can request provider decisions.",
            ["AiTrading:ScanIntervalMilliseconds"] = "Sets how frequently the AI coordinator scans for traders that are ready to act.",
            ["AiTrading:RequestTimeoutSeconds"] = "Sets the maximum duration of one provider request.",
            ["AiTrading:MaxResponseTokens"] = "Caps the number of tokens generated by one provider response; zero disables the cap.",
            ["AiTrading:MaxConcurrentRequests"] = "Caps the number of AI provider requests in flight at the same time.",
            ["AiTrading:MaxOrdersPerDecision"] = "Caps the number of orders an AI trader may return in one decision.",
            ["AiTrading:PredictionHorizonCycles"] = "Sets the required forecast horizon for every AI prediction.",
            ["AiTrading:MaxPredictionsPerDecision"] = "Caps the number of forecasts an AI trader may return in one decision.",
            ["AiTrading:HistoryCycles"] = "Sets how much recent market history is included in an AI decision snapshot.",
            ["AiTrading:RetryBaseDelaySeconds"] = "Sets the initial delay before retrying a failed AI provider request.",
            ["AiTrading:RetryMaxDelaySeconds"] = "Caps the retry delay for repeated AI provider failures.",
            ["AiTrading:AuthErrorRetrySeconds"] = "Sets the retry delay after an AI provider authentication error.",
            ["AiTrading:MaxInvalidJsonRetries"] = "Caps retries after an AI provider returns unusable decision JSON.",
            ["AiTrading:MaxTransportRetries"] = "Caps retries after transport failures within one scheduled decision cycle.",
            ["AiTrading:SystemPromptTemplate"] = "Sets the shared base instructions every AI trader receives; \"{maxOrders}\" is replaced with the per-decision order cap.",
            ["AiTrading:FinalDecisionInstruction"] = "Sets the extra guidance appended to an AI trader's end-of-day planning decision.",
        };

    public static IReadOnlyList<GameSettingDefinition> Create(IConfiguration configuration)
    {
        var definitions = new List<GameSettingDefinition>();
        foreach (var sectionName in AllowedLeafNamesBySection.Keys)
        {
            Walk(configuration.GetSection(sectionName), sectionName, definitions, configuration);
        }

        return definitions
            .OrderBy(definition => definition.Section, StringComparer.Ordinal)
            .ThenBy(definition => definition.Subsection, StringComparer.Ordinal)
            .ThenBy(definition => definition.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static void Walk(
        IConfigurationSection section,
        string path,
        ICollection<GameSettingDefinition> definitions,
        IConfiguration configuration)
    {
        var children = section.GetChildren().ToList();
        if (path.EndsWith(":Models", StringComparison.Ordinal) && children.Count > 0)
        {
            AddDefinition(path, GameSettingValueType.StringList, definitions, configuration);
            return;
        }

        if (children.Count > 0)
        {
            foreach (var child in children)
            {
                Walk(child, $"{path}:{child.Key}", definitions, configuration);
            }

            return;
        }

        if (section.Value is not null)
        {
            AddDefinition(path, ValueType(path, section.Value), definitions, configuration);
        }
    }

    private static void AddDefinition(
        string key,
        GameSettingValueType valueType,
        ICollection<GameSettingDefinition> definitions,
        IConfiguration configuration)
    {
        var segments = key.Split(':');
        if (!IsAllowedKey(segments))
        {
            return;
        }

        var section = Humanize(segments[0]);
        var subsection = segments.Length > 2 ? Humanize(segments[^2]) : null;
        var name = Humanize(segments[^1]);

        if (segments.Length >= 4 && segments[0] == "AiTrading" && segments[1] == "Providers")
        {
            var providerName = configuration[$"AiTrading:Providers:{segments[2]}:DisplayName"] ?? segments[2];
            name = $"{providerName} {name.ToLowerInvariant()}";
            subsection = "Providers";
        }

        definitions.Add(new GameSettingDefinition(
            key,
            section,
            subsection,
            name,
            Description(key, section, name, segments),
            valueType));
    }

    private static bool IsAllowedKey(IReadOnlyList<string> segments)
    {
        if (segments.Count == 4
            && segments[0] == "AiTrading"
            && segments[1] == "Providers")
        {
            return AllowedProviderLeafNames.Contains(segments[3]);
        }

        if (segments.Count == 3 && segments[0] == "RandomChanceRates")
        {
            return AllowedRandomChanceRateKeys.Contains($"{segments[1]}:{segments[2]}");
        }

        return segments.Count == 2
            && AllowedLeafNamesBySection.TryGetValue(segments[0], out var leafNames)
            && leafNames.Contains(segments[1]);
    }

    private static GameSettingValueType ValueType(string key, string value)
    {
        if (key.EndsWith(":Endpoint", StringComparison.Ordinal))
        {
            return GameSettingValueType.Url;
        }

        if (key.EndsWith(":ApiKey", StringComparison.Ordinal))
        {
            return GameSettingValueType.Secret;
        }

        if (MultilineTextKeys.Contains(key))
        {
            return GameSettingValueType.MultilineText;
        }

        if (bool.TryParse(value, out _))
        {
            return GameSettingValueType.Boolean;
        }

        if (IntegerKeys.Contains(key)
            || key.StartsWith("AiTrading:Providers:", StringComparison.Ordinal)
            && AllowedProviderIntegerLeafNames.Contains(key.Split(':')[^1]))
        {
            return GameSettingValueType.Integer;
        }

        return decimal.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _)
            ? GameSettingValueType.Decimal
            : GameSettingValueType.Text;
    }

    private static string Description(string key, string section, string name, IReadOnlyList<string> segments)
    {
        if (Descriptions.TryGetValue(key, out var description))
        {
            return description;
        }

        if (segments.Count >= 4 && segments[0] == "AiTrading" && segments[1] == "Providers")
        {
            return segments[^1] switch
            {
                "DisplayName" => "Sets the provider name shown when configuring an AI trader.",
                "Endpoint" => "Sets the HTTPS chat-completions endpoint used for this AI provider.",
                "ApiKey" => "Sets the API key sent as the bearer token for this provider; it is never displayed once saved.",
                "Models" => "Lists the models offered as suggestions when configuring an AI trader.",
                "RequestTimeoutSeconds" => "Overrides the global request timeout for this provider.",
                "MaxResponseTokens" => "Overrides the global response-token cap for this provider; zero disables the cap.",
                "MaxInvalidJsonRetries" => "Overrides the global invalid-JSON retry limit for this provider.",
                "MaxTransportRetries" => "Overrides the global transport retry limit for this provider.",
                _ => $"Configures {name.ToLowerInvariant()} for this AI provider.",
            };
        }

        if (segments.Count >= 3 && segments[0] == "RandomChanceRates")
        {
            return segments[1] switch
            {
                "EventTriggerChances" => $"Sets the probability used for {name.ToLowerInvariant()} when its market condition is eligible.",
                "ChanceModifiers" => $"Adjusts the probability effect represented by {name.ToLowerInvariant()}.",
                "RandomMagnitudeBands" => $"Sets the random magnitude boundary represented by {name.ToLowerInvariant()}.",
                _ => $"Configures {name.ToLowerInvariant()} for randomized market behavior.",
            };
        }

        if (segments[^1] == "Enabled")
        {
            return $"Controls whether {section.ToLowerInvariant()} behavior participates in the simulation.";
        }

        return $"Configures {name.ToLowerInvariant()} for {section.ToLowerInvariant()} behavior in the simulation.";
    }

    private static string Humanize(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character) && !char.IsUpper(value[index - 1]))
            {
                builder.Append(' ');
                character = char.ToLowerInvariant(character);
            }

            builder.Append(index == 0 ? char.ToUpperInvariant(character) : character);
        }

        return builder.ToString().Replace("Ai ", "AI ", StringComparison.Ordinal);
    }
}
