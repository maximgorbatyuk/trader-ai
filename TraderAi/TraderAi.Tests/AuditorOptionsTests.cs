using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AuditorOptionsTests
{
    [Fact]
    public void DefaultsDescribeTheApprovedDeterministicAuditModel()
    {
        var options = new AuditorOptions();

        Assert.Equal(2, Value<int>(options, "AuditIntervalTradingDays"));
        Assert.Equal(3m, Value<decimal>(options, "ModerateAdjustedReturnPercent"));
        Assert.Equal(10m, Value<decimal>(options, "StrongAdjustedReturnPercent"));
        Assert.Equal(5m, Value<decimal>(options, "ModerateCycleJumpPercent"));
        Assert.Equal(10m, Value<decimal>(options, "StrongCycleJumpPercent"));
        Assert.Equal(5m, Value<decimal>(options, "ModerateFreeShareDilutionPercent"));
        Assert.Equal(10m, Value<decimal>(options, "IndustryDirectionDeadband"));
        Assert.Equal(5, Value<int>(options, "ExtraRaisedExpectationsThreshold"));
        Assert.Equal(2, Value<int>(options, "RaisedExpectationsThreshold"));
        Assert.Equal(-2, Value<int>(options, "LowRiskThreshold"));
        Assert.Equal(-5, Value<int>(options, "HighRiskThreshold"));
        Assert.Equal(-20, Value<int>(options, "MinimumTotalScore"));
        Assert.Equal(20, Value<int>(options, "MaximumTotalScore"));
        Assert.Equal(-4, Value<int>(options, "MinimumDenominationScore"));
        Assert.Equal(2, Value<int>(options, "MaximumDenominationScore"));
        Assert.Equal(0.05m, Value<decimal>(options, "ModerateDecisionPull"));
        Assert.Equal(0.10m, Value<decimal>(options, "StrongDecisionPull"));
    }

    [Fact]
    public void DefaultsExposeEveryApprovedFactorWeight()
    {
        var options = new AuditorOptions();

        Assert.Equal(3, Value<int>(options, "StrongPositiveReturnScore"));
        Assert.Equal(2, Value<int>(options, "ModeratePositiveReturnScore"));
        Assert.Equal(-2, Value<int>(options, "ModerateNegativeReturnScore"));
        Assert.Equal(-3, Value<int>(options, "StrongNegativeReturnScore"));
        Assert.Equal(-1, Value<int>(options, "ModerateCycleJumpScore"));
        Assert.Equal(-2, Value<int>(options, "StrongCycleJumpScore"));
        Assert.Equal(-1, Value<int>(options, "ModerateFreeShareEmissionScore"));
        Assert.Equal(-2, Value<int>(options, "StrongFreeShareEmissionScore"));
        Assert.Equal(1, Value<int>(options, "StockSplitScore"));
        Assert.Equal(-2, Value<int>(options, "ReverseSplitScore"));
        Assert.Equal(1, Value<int>(options, "DividendPaidScore"));
        Assert.Equal(-2, Value<int>(options, "DividendReducedScore"));
        Assert.Equal(-3, Value<int>(options, "DividendSkippedScore"));
        Assert.Equal(1, Value<int>(options, "DividendCoveredScore"));
        Assert.Equal(-1, Value<int>(options, "DividendUncoveredScore"));
        Assert.Equal(1, Value<int>(options, "IndustryRisingScore"));
        Assert.Equal(-1, Value<int>(options, "IndustryFallingScore"));
        Assert.Equal(2, Value<int>(options, "HighProfitabilityScore"));
        Assert.Equal(0, Value<int>(options, "MediumProfitabilityScore"));
        Assert.Equal(-2, Value<int>(options, "LowProfitabilityScore"));
        Assert.Equal(1, Value<int>(options, "LowVolatilityScore"));
        Assert.Equal(0, Value<int>(options, "MediumVolatilityScore"));
        Assert.Equal(-2, Value<int>(options, "HighVolatilityScore"));
        Assert.Equal(2, Value<int>(options, "LowClosureRiskScore"));
        Assert.Equal(0, Value<int>(options, "MediumClosureRiskScore"));
        Assert.Equal(-3, Value<int>(options, "HighClosureRiskScore"));
        Assert.Equal(2, Value<int>(options, "PositiveManagementOutlookScore"));
        Assert.Equal(0, Value<int>(options, "NeutralManagementOutlookScore"));
        Assert.Equal(-2, Value<int>(options, "NegativeManagementOutlookScore"));
    }

    [Theory]
    [InlineData("AuditIntervalTradingDays", -1)]
    [InlineData("ModerateAdjustedReturnPercent", 11)]
    [InlineData("ModerateCycleJumpPercent", 11)]
    [InlineData("ModerateFreeShareDilutionPercent", -1)]
    [InlineData("RaisedExpectationsThreshold", 6)]
    [InlineData("LowRiskThreshold", -6)]
    [InlineData("MinimumTotalScore", -101)]
    [InlineData("MaximumTotalScore", 101)]
    [InlineData("MinimumDenominationScore", 3)]
    [InlineData("HighProfitabilityScore", 101)]
    [InlineData("ModerateDecisionPull", -0.01)]
    [InlineData("StrongDecisionPull", 1.01)]
    public void InvalidRangesOrOrderingAreRejected(string propertyName, double value)
    {
        var options = new AuditorOptions();
        var property = typeof(AuditorOptions).GetProperty(propertyName);

        Assert.NotNull(property);
        property.SetValue(
            options,
            property.PropertyType == typeof(int)
                ? (object)Convert.ToInt32(value)
                : Convert.ToDecimal(value));

        var isValid = typeof(AuditorOptions).GetMethod("IsValid");
        Assert.NotNull(isValid);
        Assert.False(Assert.IsType<bool>(isValid.Invoke(options, null)));
    }

    [Theory]
    [InlineData("HighRiskThreshold", -1)]
    [InlineData("LowRiskThreshold", 2)]
    [InlineData("RaisedExpectationsThreshold", 5)]
    [InlineData("MinimumTotalScore", -4)]
    [InlineData("MaximumTotalScore", 4)]
    public void StatusThresholdsMustRemainStrictlyOrderedInsideTotalScoreBounds(
        string propertyName,
        int value)
    {
        var options = new AuditorOptions();
        typeof(AuditorOptions).GetProperty(propertyName)!.SetValue(options, value);

        Assert.False(options.IsValid());
    }

    [Fact]
    public void RandomChanceOptionsNoLongerExposeObsoleteAuditSettings()
    {
        var eventProperties = typeof(EventTriggerChances).GetProperties().Select(property => property.Name);
        var modifierProperties = typeof(ChanceModifiers).GetProperties().Select(property => property.Name);

        Assert.DoesNotContain("AuditorIssueOnBigMove", eventProperties);
        Assert.DoesNotContain("AuditorIssueOnStable", eventProperties);
        Assert.DoesNotContain("AuditorRaiseExpectationsChance", eventProperties);
        Assert.DoesNotContain("AuditorHighRatingBuyRevision", eventProperties);
        Assert.DoesNotContain("AuditorExtraRatingBuyRevision", eventProperties);
        Assert.DoesNotContain("CrisisAuditorIssueMultiplier", modifierProperties);
    }

    private static T Value<T>(AuditorOptions options, string propertyName)
    {
        var property = typeof(AuditorOptions).GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<T>(property.GetValue(options));
    }
}
