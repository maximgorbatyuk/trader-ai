namespace TraderAi.Services;

public sealed class TradingSignalOptions
{
    public const string SectionName = "TradingSignal";

    public decimal MomentumWeight { get; set; } = 0.24m;
    public decimal OrderFlowWeight { get; set; } = 0.21m;
    public decimal IndustryWeight { get; set; } = 0.15m;
    public decimal AuditWeight { get; set; } = 0.15m;
    public decimal FundamentalWeight { get; set; } = 0.25m;

    public decimal EvidenceWeight { get; set; } = 0.75m;
    public decimal PersonalityNoiseWeight { get; set; } = 0.25m;
    public decimal MinimumWaitWeight { get; set; } = 0.20m;

    public decimal AggressiveActivityFactor { get; set; } = 1.25m;
    public decimal BalancedActivityFactor { get; set; } = 1m;
    public decimal ConservativeActivityFactor { get; set; } = 0.75m;

    public decimal LowRiskQualityResponseFactor { get; set; } = 1.25m;
    public decimal LowRiskGrowthResponseFactor { get; set; } = 0.75m;
    public decimal MediumRiskQualityResponseFactor { get; set; } = 1m;
    public decimal MediumRiskGrowthResponseFactor { get; set; } = 1m;
    public decimal HighRiskQualityResponseFactor { get; set; } = 0.75m;
    public decimal HighRiskGrowthResponseFactor { get; set; } = 1.25m;

    public bool IsValid() =>
        IsProbabilityDistribution(
            MomentumWeight,
            OrderFlowWeight,
            IndustryWeight,
            AuditWeight,
            FundamentalWeight)
        && IsProbabilityDistribution(EvidenceWeight, PersonalityNoiseWeight)
        && MinimumWaitWeight > 0m
        && Positive(
            AggressiveActivityFactor,
            BalancedActivityFactor,
            ConservativeActivityFactor,
            LowRiskQualityResponseFactor,
            LowRiskGrowthResponseFactor,
            MediumRiskQualityResponseFactor,
            MediumRiskGrowthResponseFactor,
            HighRiskQualityResponseFactor,
            HighRiskGrowthResponseFactor);

    private static bool IsProbabilityDistribution(params decimal[] weights) =>
        weights.All(weight => weight >= 0m)
        && weights.Sum() == 1m;

    private static bool Positive(params decimal[] factors) =>
        factors.All(factor => factor > 0m);
}
