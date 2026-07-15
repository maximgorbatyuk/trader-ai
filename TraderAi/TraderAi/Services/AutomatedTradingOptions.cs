using TraderAi.Models;

namespace TraderAi.Services;

public sealed class AutomatedTradingOptions
{
    public const string SectionName = "AutomatedTrading";

    public decimal LowMinimumExposurePercent { get; set; } = 20m;
    public decimal LowMaximumExposurePercent { get; set; } = 35m;
    public decimal LowMaximumOrderNotionalPercent { get; set; } = 1m;
    public decimal MediumMinimumExposurePercent { get; set; } = 35m;
    public decimal MediumMaximumExposurePercent { get; set; } = 55m;
    public decimal MediumMaximumOrderNotionalPercent { get; set; } = 2m;
    public decimal HighMinimumExposurePercent { get; set; } = 50m;
    public decimal HighMaximumExposurePercent { get; set; } = 70m;
    public decimal HighMaximumOrderNotionalPercent { get; set; } = 3m;
    public decimal MaximumIssuedSharesPerOrderPercent { get; set; } = 2m;
    public decimal MaximumPassiveBidIssuedSharesPercent { get; set; } = 0.25m;
    public decimal MinimumMeaningfulQuantityPercent { get; set; } = 25m;
    public decimal MaximumHighRiskMarginLiabilityPercent { get; set; } = 10m;

    public bool IsValid() =>
        IsExposureRangeValid(LowMinimumExposurePercent, LowMaximumExposurePercent)
        && IsExposureRangeValid(MediumMinimumExposurePercent, MediumMaximumExposurePercent)
        && IsExposureRangeValid(HighMinimumExposurePercent, HighMaximumExposurePercent)
        && IsPositivePercentage(LowMaximumOrderNotionalPercent)
        && IsPositivePercentage(MediumMaximumOrderNotionalPercent)
        && IsPositivePercentage(HighMaximumOrderNotionalPercent)
        && IsPositivePercentage(MaximumIssuedSharesPerOrderPercent)
        && IsPositivePercentage(MaximumPassiveBidIssuedSharesPercent)
        && MaximumPassiveBidIssuedSharesPercent <= MaximumIssuedSharesPerOrderPercent
        && IsPositivePercentage(MinimumMeaningfulQuantityPercent)
        && IsPositivePercentage(MaximumHighRiskMarginLiabilityPercent);

    public AutomatedTradingTarget GetTarget(RiskProfile riskProfile) => riskProfile switch
    {
        RiskProfile.Low => new(
            LowMinimumExposurePercent,
            LowMaximumExposurePercent,
            LowMaximumOrderNotionalPercent),
        RiskProfile.Medium => new(
            MediumMinimumExposurePercent,
            MediumMaximumExposurePercent,
            MediumMaximumOrderNotionalPercent),
        RiskProfile.High => new(
            HighMinimumExposurePercent,
            HighMaximumExposurePercent,
            HighMaximumOrderNotionalPercent),
        _ => throw new ArgumentOutOfRangeException(nameof(riskProfile)),
    };

    private static bool IsExposureRangeValid(decimal minimum, decimal maximum) =>
        minimum >= 0m && maximum <= 100m && minimum <= maximum;

    private static bool IsPositivePercentage(decimal value) => value is > 0m and <= 100m;
}

public sealed record AutomatedTradingTarget(
    decimal MinimumExposurePercent,
    decimal MaximumExposurePercent,
    decimal MaximumOrderNotionalPercent);
