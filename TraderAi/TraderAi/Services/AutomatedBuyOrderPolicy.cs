using Microsoft.Extensions.Options;
using TraderAi.Models;

namespace TraderAi.Services;

public enum AutomatedExposurePosition
{
    Below,
    Within,
    Above,
}

public sealed record AutomatedExposureAssessment(
    AutomatedTradingTarget Target,
    decimal CurrentExposurePercent,
    AutomatedExposurePosition Position);

public sealed record AutomatedBuyOrderInput(
    RiskProfile RiskProfile,
    decimal NetWorth,
    decimal HoldingsValue,
    decimal ReservedBuyNotional,
    decimal AvailableCash,
    decimal BuyingPower,
    decimal MarginLiability,
    decimal OrderPrice,
    int IssuedShares,
    int ExecutableSellQuantity);

public sealed record AutomatedBuyOrderEnvelope(
    AutomatedExposureAssessment Exposure,
    decimal MaximumBudget,
    int MinimumQuantity,
    int MaximumQuantity,
    bool IsPassive);

public sealed class AutomatedBuyOrderPolicy(IOptions<AutomatedTradingOptions> configuredOptions)
{
    private readonly AutomatedTradingOptions options = configuredOptions.Value;

    public AutomatedExposureAssessment? AssessExposure(
        RiskProfile riskProfile,
        decimal netWorth,
        decimal holdingsValue)
    {
        if (netWorth <= 0m || holdingsValue < 0m)
        {
            return null;
        }

        var target = options.GetTarget(riskProfile);
        var currentExposurePercent = holdingsValue / netWorth * 100m;
        var position = currentExposurePercent < target.MinimumExposurePercent
            ? AutomatedExposurePosition.Below
            : currentExposurePercent > target.MaximumExposurePercent
                ? AutomatedExposurePosition.Above
                : AutomatedExposurePosition.Within;

        return new AutomatedExposureAssessment(target, currentExposurePercent, position);
    }

    public AutomatedBuyOrderEnvelope? BuildBuyEnvelope(AutomatedBuyOrderInput input)
    {
        if (input.OrderPrice <= 0m || input.IssuedShares <= 0)
        {
            return null;
        }

        var exposure = AssessExposure(input.RiskProfile, input.NetWorth, input.HoldingsValue);
        if (exposure is null || exposure.Position == AutomatedExposurePosition.Above)
        {
            return null;
        }

        var exposureHeadroom = input.NetWorth * exposure.Target.MaximumExposurePercent / 100m
            - input.HoldingsValue
            - Math.Max(0m, input.ReservedBuyNotional);
        var maximumBudget = GetMaximumBudget(input);
        var maximumOrderNotional = input.NetWorth * exposure.Target.MaximumOrderNotionalPercent / 100m;
        var maximumIssuedQuantity = input.IssuedShares * options.MaximumIssuedSharesPerOrderPercent / 100m;
        var isPassive = input.ExecutableSellQuantity <= 0;
        var maximumSupplyQuantity = isPassive
            ? input.IssuedShares * options.MaximumPassiveBidIssuedSharesPercent / 100m
            : input.ExecutableSellQuantity;

        var maximumQuantityDecimal = new[]
        {
            maximumBudget / input.OrderPrice,
            exposureHeadroom / input.OrderPrice,
            maximumOrderNotional / input.OrderPrice,
            maximumIssuedQuantity,
            maximumSupplyQuantity,
        }.Min();
        if (maximumQuantityDecimal <= 0m)
        {
            return null;
        }

        var maximumQuantity = (int)Math.Floor(Math.Min(maximumQuantityDecimal, int.MaxValue));
        if (maximumQuantity <= 0)
        {
            return null;
        }

        var minimumQuantity = exposure.Position == AutomatedExposurePosition.Below
            ? Math.Max(1, (int)Math.Ceiling(maximumQuantity * options.MinimumMeaningfulQuantityPercent / 100m))
            : 1;

        return new AutomatedBuyOrderEnvelope(
            exposure,
            maximumBudget,
            minimumQuantity,
            maximumQuantity,
            isPassive);
    }

    private decimal GetMaximumBudget(AutomatedBuyOrderInput input)
    {
        var availableCash = Math.Max(0m, input.AvailableCash);
        if (input.RiskProfile != RiskProfile.High)
        {
            return availableCash;
        }

        var maximumMarginLiability = input.NetWorth * options.MaximumHighRiskMarginLiabilityPercent / 100m;
        var committedFutureMargin = Math.Max(0m, -input.AvailableCash);
        var remainingMarginRoom = Math.Max(
            0m,
            maximumMarginLiability - Math.Max(0m, input.MarginLiability) - committedFutureMargin);
        return Math.Min(Math.Max(0m, input.BuyingPower), availableCash + remainingMarginRoom);
    }
}
