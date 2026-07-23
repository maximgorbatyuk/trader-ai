namespace TraderAi.Services;

public sealed class CompanyFinancialOptions
{
    public const string SectionName = "CompanyFinancial";

    public bool Enabled { get; set; } = true;

    public int StabilityWindowSnapshots { get; set; } = 6;

    public decimal ProfitabilityNetMarginWeight { get; set; } = 0.30m;

    public decimal ProfitabilityReturnOnAssetsWeight { get; set; } = 0.20m;

    public decimal ProfitabilityCashFlowWeight { get; set; } = 0.20m;

    public decimal ProfitabilityRevenueTrendWeight { get; set; } = 0.15m;

    public decimal ProfitabilityManagementOutlookWeight { get; set; } = 0.15m;

    public decimal ClosureRiskEarningsAndCashFlowWeight { get; set; } = 0.30m;

    public decimal ClosureRiskLeverageWeight { get; set; } = 0.25m;

    public decimal ClosureRiskLiabilitiesWeight { get; set; } = 0.20m;

    public decimal ClosureRiskBusinessWeight { get; set; } = 0.15m;

    public decimal ClosureRiskIndustryWeight { get; set; } = 0.10m;

    public decimal LowLevelMaximumScore { get; set; } = 34m;

    public decimal HighLevelMinimumScore { get; set; } = 67m;

    public decimal MaximumLiabilitiesToAssetsRatio { get; set; } = 1.25m;

    public decimal MaximumDebtToLiabilitiesRatio { get; set; } = 1m;

    public decimal MaximumForecastDeviationRatio { get; set; } = 0.25m;

    public decimal IndustryImpulseWeight { get; set; } = 0.15m;

    public decimal MinimumExpectedDividendCoverageRatio { get; set; } = 1.20m;

    public decimal MaximumExpectedDividendPayoutRatio { get; set; } = 0.60m;

    public bool IsValid()
    {
        var profitabilityWeights = new[]
        {
            ProfitabilityNetMarginWeight,
            ProfitabilityReturnOnAssetsWeight,
            ProfitabilityCashFlowWeight,
            ProfitabilityRevenueTrendWeight,
            ProfitabilityManagementOutlookWeight,
        };
        var closureRiskWeights = new[]
        {
            ClosureRiskEarningsAndCashFlowWeight,
            ClosureRiskLeverageWeight,
            ClosureRiskLiabilitiesWeight,
            ClosureRiskBusinessWeight,
            ClosureRiskIndustryWeight,
        };

        return StabilityWindowSnapshots >= 2
            && LowLevelMaximumScore is >= 0m and <= 100m
            && HighLevelMinimumScore is >= 0m and <= 100m
            && LowLevelMaximumScore < HighLevelMinimumScore
            && WeightsAreValid(profitabilityWeights)
            && WeightsAreValid(closureRiskWeights)
            && MaximumLiabilitiesToAssetsRatio is > 0m and <= 2m
            && MaximumDebtToLiabilitiesRatio is > 0m and <= 1m
            && MaximumForecastDeviationRatio is >= 0m and <= 1m
            && IndustryImpulseWeight is >= 0m and <= 1m
            && MinimumExpectedDividendCoverageRatio is > 0m and <= 10m
            && MaximumExpectedDividendPayoutRatio is >= 0m and <= 1m;
    }

    private static bool WeightsAreValid(IReadOnlyCollection<decimal> weights) =>
        weights.All(weight => weight is >= 0m and <= 1m)
        && weights.Sum() == 1m;
}
