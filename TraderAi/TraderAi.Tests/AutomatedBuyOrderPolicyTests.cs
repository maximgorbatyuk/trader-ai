using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AutomatedBuyOrderPolicyTests
{
    private readonly AutomatedTradingOptions options = new();

    [Theory]
    [InlineData(RiskProfile.Low, 20, 35, 1)]
    [InlineData(RiskProfile.Medium, 35, 55, 2)]
    [InlineData(RiskProfile.High, 50, 70, 3)]
    public void DefaultsExposeApprovedRiskRangesAndOrderCaps(
        RiskProfile riskProfile,
        decimal minimumExposurePercent,
        decimal maximumExposurePercent,
        decimal maximumOrderNotionalPercent)
    {
        var target = options.GetTarget(riskProfile);

        Assert.Equal(minimumExposurePercent, target.MinimumExposurePercent);
        Assert.Equal(maximumExposurePercent, target.MaximumExposurePercent);
        Assert.Equal(maximumOrderNotionalPercent, target.MaximumOrderNotionalPercent);
        Assert.Equal(2m, options.MaximumIssuedSharesPerOrderPercent);
        Assert.Equal(0.25m, options.MaximumPassiveBidIssuedSharesPercent);
        Assert.Equal(25m, options.MinimumMeaningfulQuantityPercent);
        Assert.Equal(10m, options.MaximumHighRiskMarginLiabilityPercent);
    }

    [Fact]
    public void DefaultOptionsAreValid()
    {
        Assert.True(options.IsValid());
    }

    [Fact]
    public void InvalidPercentagesAndInvertedRangesAreRejected()
    {
        var invalidOptions = new[]
        {
            new AutomatedTradingOptions { LowMinimumExposurePercent = 36m, LowMaximumExposurePercent = 35m },
            new AutomatedTradingOptions { MediumMaximumOrderNotionalPercent = -1m },
            new AutomatedTradingOptions { HighMaximumExposurePercent = 101m },
            new AutomatedTradingOptions { MinimumMeaningfulQuantityPercent = 0m },
            new AutomatedTradingOptions { MinimumMeaningfulQuantityPercent = 101m },
            new AutomatedTradingOptions { MaximumIssuedSharesPerOrderPercent = 0m },
            new AutomatedTradingOptions { MaximumPassiveBidIssuedSharesPercent = 3m },
            new AutomatedTradingOptions { MaximumHighRiskMarginLiabilityPercent = 101m },
        };

        Assert.All(invalidOptions, candidate => Assert.False(candidate.IsValid()));
    }

    [Fact]
    public void PolicyCanBeResolvedFromConfiguredOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<AutomatedTradingOptions>>(Options.Create(options));
        services.AddScoped<AutomatedBuyOrderPolicy>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<AutomatedBuyOrderPolicy>());
    }

    [Theory]
    [InlineData(RiskProfile.Low, 19, AutomatedExposurePosition.Below)]
    [InlineData(RiskProfile.Low, 20, AutomatedExposurePosition.Within)]
    [InlineData(RiskProfile.Low, 36, AutomatedExposurePosition.Above)]
    [InlineData(RiskProfile.Medium, 34, AutomatedExposurePosition.Below)]
    [InlineData(RiskProfile.Medium, 45, AutomatedExposurePosition.Within)]
    [InlineData(RiskProfile.Medium, 56, AutomatedExposurePosition.Above)]
    [InlineData(RiskProfile.High, 49, AutomatedExposurePosition.Below)]
    [InlineData(RiskProfile.High, 60, AutomatedExposurePosition.Within)]
    [InlineData(RiskProfile.High, 71, AutomatedExposurePosition.Above)]
    public void AssessesExposureAgainstTheRiskTarget(
        RiskProfile riskProfile,
        decimal exposurePercent,
        AutomatedExposurePosition expectedPosition)
    {
        var policy = CreatePolicy();

        var assessment = policy.AssessExposure(riskProfile, 10_000m, exposurePercent * 100m);

        Assert.NotNull(assessment);
        Assert.Equal(exposurePercent, assessment.CurrentExposurePercent);
        Assert.Equal(expectedPosition, assessment.Position);
    }

    [Theory]
    [InlineData(RiskProfile.Low)]
    [InlineData(RiskProfile.Medium)]
    public void LowAndMediumRiskUseOnlyAvailableCash(RiskProfile riskProfile)
    {
        var policy = CreatePolicy();
        var input = ValidInput(riskProfile) with
        {
            AvailableCash = 50m,
            BuyingPower = 5_000m,
            OrderPrice = 10m,
        };

        var envelope = policy.BuildBuyEnvelope(input);

        Assert.NotNull(envelope);
        Assert.Equal(50m, envelope.MaximumBudget);
        Assert.Equal(5, envelope.MaximumQuantity);
    }

    [Theory]
    [InlineData(RiskProfile.Low, 0)]
    [InlineData(RiskProfile.Low, -100)]
    [InlineData(RiskProfile.Medium, 0)]
    [InlineData(RiskProfile.Medium, -100)]
    public void LowAndMediumRiskCannotBuyWithoutPositiveAvailableCash(
        RiskProfile riskProfile,
        decimal availableCash)
    {
        var policy = CreatePolicy();
        var input = ValidInput(riskProfile) with
        {
            AvailableCash = availableCash,
            BuyingPower = 100_000m,
        };

        Assert.Null(policy.BuildBuyEnvelope(input));
    }

    [Fact]
    public void HighRiskMarginBudgetIsCappedAtTenPercentOfNetWorth()
    {
        var policy = CreatePolicy();
        var input = ValidInput(RiskProfile.High) with
        {
            NetWorth = 100_000m,
            HoldingsValue = 40_000m,
            AvailableCash = 100m,
            BuyingPower = 20_000m,
            MarginLiability = 9_000m,
            OrderPrice = 10m,
            IssuedShares = 100_000,
            ExecutableSellQuantity = 10_000,
        };

        var envelope = policy.BuildBuyEnvelope(input);

        Assert.NotNull(envelope);
        Assert.Equal(1_100m, envelope.MaximumBudget);
        Assert.Equal(110, envelope.MaximumQuantity);
    }

    [Fact]
    public void HighRiskBudgetCannotExceedBuyingPower()
    {
        var policy = CreatePolicy();
        var input = ValidInput(RiskProfile.High) with
        {
            AvailableCash = 100m,
            BuyingPower = 700m,
            MarginLiability = 0m,
            OrderPrice = 10m,
        };

        var envelope = policy.BuildBuyEnvelope(input);

        Assert.NotNull(envelope);
        Assert.Equal(700m, envelope.MaximumBudget);
        Assert.Equal(30, envelope.MaximumQuantity);
    }

    [Fact]
    public void HighRiskNegativeAvailableCashReducesRemainingMarginRoom()
    {
        var policy = CreatePolicy();
        var input = ValidInput(RiskProfile.High) with
        {
            NetWorth = 100_000m,
            HoldingsValue = 40_000m,
            AvailableCash = -500m,
            BuyingPower = 20_000m,
            MarginLiability = 9_000m,
            OrderPrice = 10m,
            IssuedShares = 100_000,
            ExecutableSellQuantity = 10_000,
        };

        var envelope = policy.BuildBuyEnvelope(input);

        Assert.NotNull(envelope);
        Assert.Equal(500m, envelope.MaximumBudget);
        Assert.Equal(50, envelope.MaximumQuantity);
    }

    [Fact]
    public void ExposureAtOrAboveMaximumProducesNoBuyEnvelope()
    {
        var policy = CreatePolicy();
        var atMaximum = ValidInput(RiskProfile.Low) with { HoldingsValue = 3_500m };
        var aboveMaximum = atMaximum with { HoldingsValue = 3_600m };

        Assert.Null(policy.BuildBuyEnvelope(atMaximum));
        Assert.Null(policy.BuildBuyEnvelope(aboveMaximum));
        Assert.Equal(
            AutomatedExposurePosition.Above,
            policy.AssessExposure(RiskProfile.Low, aboveMaximum.NetWorth, aboveMaximum.HoldingsValue)?.Position);
    }

    [Fact]
    public void MaximumQuantityIsCappedByRiskOrderNotional()
    {
        var policy = CreatePolicy();
        var input = ValidInput(RiskProfile.Medium) with
        {
            AvailableCash = 10_000m,
            BuyingPower = 10_000m,
            OrderPrice = 10m,
            IssuedShares = 100_000,
            ExecutableSellQuantity = 10_000,
        };

        var envelope = policy.BuildBuyEnvelope(input);

        Assert.NotNull(envelope);
        Assert.Equal(20, envelope.MaximumQuantity);
    }

    [Fact]
    public void MaximumQuantityIsCappedByTwoPercentOfIssuedShares()
    {
        var policy = CreatePolicy();
        var input = ValidInput(RiskProfile.Medium) with
        {
            NetWorth = 100_000m,
            HoldingsValue = 30_000m,
            AvailableCash = 10_000m,
            BuyingPower = 10_000m,
            OrderPrice = 10m,
            IssuedShares = 1_000,
            ExecutableSellQuantity = 1_000,
        };

        var envelope = policy.BuildBuyEnvelope(input);

        Assert.NotNull(envelope);
        Assert.Equal(20, envelope.MaximumQuantity);
    }

    [Fact]
    public void MaximumQuantityIsCappedByExecutableSellSupply()
    {
        var policy = CreatePolicy();
        var input = ValidInput(RiskProfile.Medium) with
        {
            IssuedShares = 10_000,
            ExecutableSellQuantity = 7,
        };

        var envelope = policy.BuildBuyEnvelope(input);

        Assert.NotNull(envelope);
        Assert.False(envelope.IsPassive);
        Assert.Equal(7, envelope.MaximumQuantity);
    }

    [Fact]
    public void NoExecutableSupplyUsesThePassiveBidCap()
    {
        var policy = CreatePolicy();
        var input = ValidInput(RiskProfile.Medium) with
        {
            NetWorth = 100_000m,
            HoldingsValue = 30_000m,
            AvailableCash = 10_000m,
            BuyingPower = 10_000m,
            IssuedShares = 10_000,
            ExecutableSellQuantity = 0,
        };

        var envelope = policy.BuildBuyEnvelope(input);

        Assert.NotNull(envelope);
        Assert.True(envelope.IsPassive);
        Assert.Equal(25, envelope.MaximumQuantity);
    }

    [Fact]
    public void BelowMinimumExposureRequiresAQuarterOfTheMaximumQuantity()
    {
        var policy = CreatePolicy();
        var belowMinimum = ValidInput(RiskProfile.Medium) with
        {
            NetWorth = 100_000m,
            HoldingsValue = 30_000m,
            AvailableCash = 10_000m,
            BuyingPower = 10_000m,
            IssuedShares = 1_000,
            ExecutableSellQuantity = 1_000,
        };
        var withinRange = belowMinimum with { HoldingsValue = 40_000m };

        var belowEnvelope = policy.BuildBuyEnvelope(belowMinimum);
        var withinEnvelope = policy.BuildBuyEnvelope(withinRange);

        Assert.NotNull(belowEnvelope);
        Assert.Equal(20, belowEnvelope.MaximumQuantity);
        Assert.Equal(5, belowEnvelope.MinimumQuantity);
        Assert.NotNull(withinEnvelope);
        Assert.Equal(1, withinEnvelope.MinimumQuantity);
    }

    [Fact]
    public void BelowMinimumMeaningfulQuantityRoundsUp()
    {
        var policy = CreatePolicy();
        var input = ValidInput(RiskProfile.Medium) with
        {
            ExecutableSellQuantity = 5,
        };

        var envelope = policy.BuildBuyEnvelope(input);

        Assert.NotNull(envelope);
        Assert.Equal(5, envelope.MaximumQuantity);
        Assert.Equal(2, envelope.MinimumQuantity);
    }

    [Fact]
    public void BelowMinimumMeaningfulQuantityIsAtLeastOne()
    {
        var policy = CreatePolicy();
        var input = ValidInput(RiskProfile.Medium) with
        {
            ExecutableSellQuantity = 1,
        };

        var envelope = policy.BuildBuyEnvelope(input);

        Assert.NotNull(envelope);
        Assert.Equal(1, envelope.MaximumQuantity);
        Assert.Equal(1, envelope.MinimumQuantity);
    }

    [Fact]
    public void ExposureHeadroomCapsTheMaximumQuantity()
    {
        var policy = CreatePolicy();
        var input = ValidInput(RiskProfile.Medium) with
        {
            NetWorth = 10_000m,
            HoldingsValue = 5_450m,
            AvailableCash = 10_000m,
            BuyingPower = 10_000m,
            OrderPrice = 10m,
            IssuedShares = 100_000,
            ExecutableSellQuantity = 10_000,
        };

        var envelope = policy.BuildBuyEnvelope(input);

        Assert.NotNull(envelope);
        Assert.Equal(5, envelope.MaximumQuantity);
    }

    [Fact]
    public void ReservedBuyNotionalReducesAndCanExhaustExposureHeadroom()
    {
        var policy = CreatePolicy();
        var input = ValidInput(RiskProfile.Medium) with
        {
            NetWorth = 100_000m,
            HoldingsValue = 54_000m,
            ReservedBuyNotional = 400m,
            AvailableCash = 10_000m,
            BuyingPower = 10_000m,
            OrderPrice = 100m,
            IssuedShares = 100_000,
            ExecutableSellQuantity = 10_000,
        };

        var reducedEnvelope = policy.BuildBuyEnvelope(input);
        var exhaustedEnvelope = policy.BuildBuyEnvelope(input with { ReservedBuyNotional = 1_000m });

        Assert.NotNull(reducedEnvelope);
        Assert.Equal(6, reducedEnvelope.MaximumQuantity);
        Assert.Null(exhaustedEnvelope);
    }

    [Fact]
    public void ExcessiveReservedBuyNotionalReturnsNoEnvelopeWithoutOverflow()
    {
        var policy = CreatePolicy();
        var input = ValidInput(RiskProfile.Low) with
        {
            ReservedBuyNotional = 100_000_000m,
            OrderPrice = 0.01m,
        };

        Assert.Null(policy.BuildBuyEnvelope(input));
    }

    [Fact]
    public void EquivalentSequentialCommitmentPreventsASecondEnvelope()
    {
        var policy = CreatePolicy();
        var input = ValidInput(RiskProfile.Medium) with
        {
            NetWorth = 100_000m,
            HoldingsValue = 54_000m,
            AvailableCash = 10_000m,
            BuyingPower = 10_000m,
            OrderPrice = 100m,
            IssuedShares = 100_000,
            ExecutableSellQuantity = 10_000,
        };

        var firstEnvelope = policy.BuildBuyEnvelope(input);

        Assert.NotNull(firstEnvelope);
        var committedNotional = firstEnvelope.MaximumQuantity * input.OrderPrice;
        Assert.Null(policy.BuildBuyEnvelope(input with { ReservedBuyNotional = committedNotional }));
    }

    public static TheoryData<AutomatedBuyOrderInput> InvalidInputs => new()
    {
        ValidInput(RiskProfile.Low) with { NetWorth = 0m },
        ValidInput(RiskProfile.Low) with { NetWorth = -1m },
        ValidInput(RiskProfile.Low) with { OrderPrice = 0m },
        ValidInput(RiskProfile.Low) with { OrderPrice = -1m },
        ValidInput(RiskProfile.Low) with { IssuedShares = 0 },
        ValidInput(RiskProfile.Low) with { IssuedShares = -1 },
    };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void InvalidInputsProduceNoBuyEnvelope(AutomatedBuyOrderInput input)
    {
        var policy = CreatePolicy();

        Assert.Null(policy.BuildBuyEnvelope(input));
    }

    private AutomatedBuyOrderPolicy CreatePolicy() => new(Options.Create(options));

    private static AutomatedBuyOrderInput ValidInput(RiskProfile riskProfile) => new(
        riskProfile,
        NetWorth: 10_000m,
        HoldingsValue: riskProfile switch
        {
            RiskProfile.Low => 1_000m,
            RiskProfile.Medium => 3_000m,
            RiskProfile.High => 4_000m,
            _ => throw new ArgumentOutOfRangeException(nameof(riskProfile)),
        },
        ReservedBuyNotional: 0m,
        AvailableCash: 5_000m,
        BuyingPower: 5_000m,
        MarginLiability: 0m,
        OrderPrice: 10m,
        IssuedShares: 10_000,
        ExecutableSellQuantity: 10_000);
}
