using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class CompanyFinancialOptionsTests
{
    [Fact]
    public void DefaultsAreValidAndModerate()
    {
        var options = new CompanyFinancialOptions();
        var random = new RandomChanceRatesOptions();

        Assert.True(options.Enabled);
        Assert.Equal(6, options.StabilityWindowSnapshots);
        Assert.Equal(1m, ProfitabilityWeightSum(options));
        Assert.Equal(1m, ClosureRiskWeightSum(options));
        Assert.True(options.IsValid());
        Assert.True(random.IsValid());
        Assert.InRange(
            (random.RandomMagnitudeBands.FinancialSeedBusinessRiskScoreMin
                + random.RandomMagnitudeBands.FinancialSeedBusinessRiskScoreMax) / 2m,
            45m,
            55m);
        Assert.True(
            random.RandomMagnitudeBands.FinancialSeedLiabilitiesToAssetsMax
            < options.MaximumLiabilitiesToAssetsRatio);
        Assert.True(random.RandomMagnitudeBands.FinancialSeedLiabilitiesToAssetsMax < 1m);
        Assert.InRange(
            random.RandomMagnitudeBands.FinancialSeedManagementForecastDeviationMin,
            -0.10m,
            0m);
        Assert.InRange(
            random.RandomMagnitudeBands.FinancialSeedManagementForecastDeviationMax,
            0m,
            0.10m);
    }

    [Fact]
    public void IsValidRejectsNonPositiveStabilityWindow()
    {
        var options = new CompanyFinancialOptions
        {
            StabilityWindowSnapshots = 0,
        };

        Assert.False(options.IsValid());
    }

    [Fact]
    public void IsValidRejectsUnorderedOrOutOfRangeLevels()
    {
        var unordered = new CompanyFinancialOptions
        {
            LowLevelMaximumScore = 70m,
            HighLevelMinimumScore = 60m,
        };
        var outsideScoreRange = new CompanyFinancialOptions
        {
            HighLevelMinimumScore = 101m,
        };

        Assert.False(unordered.IsValid());
        Assert.False(outsideScoreRange.IsValid());
    }

    [Fact]
    public void IsValidRejectsNegativeOrUnbalancedWeightSets()
    {
        var negativeProfitabilityWeight = new CompanyFinancialOptions
        {
            ProfitabilityNetMarginWeight = -0.05m,
            ProfitabilityCashFlowWeight = 0.30m,
        };
        var unbalancedClosureRiskWeights = new CompanyFinancialOptions
        {
            ClosureRiskIndustryWeight = 0.20m,
        };

        Assert.False(negativeProfitabilityWeight.IsValid());
        Assert.False(unbalancedClosureRiskWeights.IsValid());
    }

    [Fact]
    public void IsValidAllowsBoundedNegativeEquityForGradualDeterioration()
    {
        var gradualNegativeEquity = new CompanyFinancialOptions
        {
            MaximumLiabilitiesToAssetsRatio = 1.25m,
        };

        Assert.True(gradualNegativeEquity.IsValid());
    }

    [Fact]
    public void IsValidRejectsUnsafeFinancialInvariantsAndDividendRules()
    {
        var excessiveLiabilities = new CompanyFinancialOptions
        {
            MaximumLiabilitiesToAssetsRatio = 2.01m,
        };
        var invalidDividendCoverage = new CompanyFinancialOptions
        {
            MinimumExpectedDividendCoverageRatio = 0m,
        };
        var invalidIndustryWeight = new CompanyFinancialOptions
        {
            IndustryImpulseWeight = 1.10m,
        };

        Assert.False(excessiveLiabilities.IsValid());
        Assert.False(invalidDividendCoverage.IsValid());
        Assert.False(invalidIndustryWeight.IsValid());
    }

    [Fact]
    public void RandomOptionsRejectZeroBigInvestmentFractionMinimum()
    {
        var zeroMinimum = new RandomChanceRatesOptions();
        zeroMinimum.RandomMagnitudeBands.BigInvestmentFractionMin = 0d;

        Assert.False(zeroMinimum.IsValid());
    }

    [Fact]
    public void RandomOptionsRejectBigInvestmentFractionsAboveDecimalBounds()
    {
        var aboveDecimalMaximum = new RandomChanceRatesOptions();
        aboveDecimalMaximum.RandomMagnitudeBands.BigInvestmentFractionMax = double.MaxValue;

        Assert.False(aboveDecimalMaximum.IsValid());
    }

    [Fact]
    public void RandomOptionsRejectInvalidChanceSeedAndUpdateRanges()
    {
        var invalidChance = new RandomChanceRatesOptions();
        invalidChance.EventTriggerChances.FinancialMetricChange = 1.01;

        var unorderedSeedRange = new RandomChanceRatesOptions();
        unorderedSeedRange.RandomMagnitudeBands.FinancialSeedRevenueToAssetsMin = 0.80m;
        unorderedSeedRange.RandomMagnitudeBands.FinancialSeedRevenueToAssetsMax = 0.20m;

        var unsafeBalanceSeed = new RandomChanceRatesOptions();
        unsafeBalanceSeed.RandomMagnitudeBands.FinancialSeedLiabilitiesToAssetsMax = 1.01m;

        var invalidConfidence = new RandomChanceRatesOptions();
        invalidConfidence.RandomMagnitudeBands.FinancialSeedManagementConfidenceMax = 101m;

        var negativeUpdate = new RandomChanceRatesOptions();
        negativeUpdate.RandomMagnitudeBands.FinancialOperatingUpdateMin = -0.01m;

        var unorderedUpdate = new RandomChanceRatesOptions();
        unorderedUpdate.RandomMagnitudeBands.FinancialForecastUpdateMin = 0.10m;
        unorderedUpdate.RandomMagnitudeBands.FinancialForecastUpdateMax = 0.05m;

        Assert.False(invalidChance.IsValid());
        Assert.False(unorderedSeedRange.IsValid());
        Assert.False(unsafeBalanceSeed.IsValid());
        Assert.False(invalidConfidence.IsValid());
        Assert.False(negativeUpdate.IsValid());
        Assert.False(unorderedUpdate.IsValid());
    }

    private static decimal ProfitabilityWeightSum(CompanyFinancialOptions options) =>
        options.ProfitabilityNetMarginWeight
        + options.ProfitabilityReturnOnAssetsWeight
        + options.ProfitabilityCashFlowWeight
        + options.ProfitabilityRevenueTrendWeight
        + options.ProfitabilityManagementOutlookWeight;

    private static decimal ClosureRiskWeightSum(CompanyFinancialOptions options) =>
        options.ClosureRiskEarningsAndCashFlowWeight
        + options.ClosureRiskLeverageWeight
        + options.ClosureRiskLiabilitiesWeight
        + options.ClosureRiskBusinessWeight
        + options.ClosureRiskIndustryWeight;
}
