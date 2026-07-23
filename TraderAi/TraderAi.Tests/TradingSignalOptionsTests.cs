using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class TradingSignalOptionsTests
{
    [Fact]
    public void CompanyQuoteCarriesImmutableDirectionalEvidenceSeparatelyFromExecutionState()
    {
        var audit = new EffectiveAuditEvidence(
            CompanyRiskRating.RaisedExpectations,
            TotalScore: 8,
            EvaluationStartTradingDayNumber: 4,
            EvaluationEndTradingDayNumber: 5,
            EffectiveTradingDayNumber: 6,
            AdjustedReturnScore: 2,
            CycleJumpScore: -1,
            FreeShareEmissionScore: 0,
            DenominationScore: 0,
            DividendOutcomeScore: 1,
            DividendCoverageScore: 1,
            IndustryScore: 1,
            ProfitabilityFactorScore: 2,
            StabilityFactorScore: 1,
            ClosureRiskFactorScore: 1,
            ManagementOutlookFactorScore: 0);
        var financial = new LatestFinancialEvidence(
            SnapshotId: 10,
            TradingDayNumber: 5,
            CompanyFinancialSnapshotMoment.Midday,
            new CompanyFinancialValues(
                Revenue: 1_000m,
                NetProfit: 100m,
                OperatingCashFlow: 120m,
                TotalAssets: 2_000m,
                TotalLiabilities: 700m,
                TotalDebt: 300m,
                ExpectedDividendPerShare: 2m,
                ExpectedDividendPool: 200m,
                DividendCoverageRatio: 1.5m,
                BusinessRiskScore: 25m,
                ManagementRevenueForecast: 1_100m,
                ManagementProfitForecast: 120m,
                ManagementOperatingCashFlowForecast: 130m),
            new CompanyFinancialDeltas(
                Revenue: 50m,
                NetProfit: 10m,
                OperatingCashFlow: 15m,
                TotalAssets: 20m,
                TotalLiabilities: -10m,
                TotalDebt: -5m,
                ExpectedDividendPerShare: 0.1m,
                ExpectedDividendPool: 10m,
                DividendCoverageRatio: 0.1m,
                BusinessRiskScore: -2m,
                ManagementRevenueForecast: 60m,
                ManagementProfitForecast: 15m,
                ManagementOperatingCashFlowForecast: 20m,
                ManagementConfidenceScore: 3m),
            ProfitabilityScore: 75m,
            CompanyMetricLevel.High,
            StabilityScore: 80m,
            CompanyMetricLevel.Low,
            ClosureRiskScore: 20m,
            CompanyMetricLevel.Low,
            ManagementOutlook.Positive,
            ManagementConfidenceScore: 85m,
            LatestDividendOutcome: DividendFundingOutcome.Paid,
            LatestDividendDeclaredAmount: 180m,
            LatestDividendFundedAmount: 180m);
        var bounds = OrderPriceBounds.FromReference(
            referencePrice: 100m,
            upperBandPercent: 15m,
            lowerBandPercent: 10m,
            allowedUpperPercent: 25m,
            allowedLowerPercent: 15m);

        var quote = new CompanyQuote(
            CompanyId: 1,
            Price: 100m,
            PriceChangePct: 0.02m,
            OrderFlowImbalance: 0.25m,
            LongRangeChangePct: 0.10m,
            SectorSentiment: 100,
            Audit: audit,
            Financials: financial,
            Bounds: bounds,
            IssuedShares: 1_000,
            BestExecutableSellPrice: 101m,
            BestExecutableSellQuantity: 20,
            IndividualBuyBlockedForBatch: false,
            OpenSellQuantity: 50);

        Assert.Equal(0.25m, quote.OrderFlowImbalance);
        Assert.Same(audit, quote.Audit);
        Assert.Same(financial, quote.Financials);
        Assert.Same(bounds, quote.Bounds);
    }

    [Fact]
    public void DefaultsAreValid()
    {
        Assert.True(new TradingSignalOptions().IsValid());
    }

    [Theory]
    [InlineData(-0.01, 0.26, 0.20, 0.15, 0.40)]
    [InlineData(0.24, 0.21, 0.20, 0.15, 0.19)]
    [InlineData(0.24, 0.21, 0.20, 0.15, 0.41)]
    public void DirectionalComponentWeightsMustBeNonNegativeAndSumToOne(
        double momentum,
        double orderFlow,
        double industry,
        double audit,
        double fundamental)
    {
        var options = new TradingSignalOptions
        {
            MomentumWeight = (decimal)momentum,
            OrderFlowWeight = (decimal)orderFlow,
            IndustryWeight = (decimal)industry,
            AuditWeight = (decimal)audit,
            FundamentalWeight = (decimal)fundamental,
        };

        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData(-0.01, 1.01)]
    [InlineData(0.75, 0.24)]
    [InlineData(0.75, 0.26)]
    public void EvidenceAndPersonalityNoiseWeightsMustFormAProbabilityBlend(
        double evidence,
        double personalityNoise)
    {
        var options = new TradingSignalOptions
        {
            EvidenceWeight = (decimal)evidence,
            PersonalityNoiseWeight = (decimal)personalityNoise,
        };

        Assert.False(options.IsValid());
    }

    [Fact]
    public void MinimumWaitWeightMustRemainPositive()
    {
        Assert.False(new TradingSignalOptions { MinimumWaitWeight = 0m }.IsValid());
        Assert.False(new TradingSignalOptions { MinimumWaitWeight = -0.01m }.IsValid());
    }

    [Fact]
    public void WaitAndPersonalityFactorsAcceptTheirExactUpperBounds()
    {
        var options = new TradingSignalOptions
        {
            MinimumWaitWeight = 1m,
            AggressiveActivityFactor = 5m,
            BalancedActivityFactor = 5m,
            ConservativeActivityFactor = 5m,
            LowRiskQualityResponseFactor = 5m,
            LowRiskGrowthResponseFactor = 5m,
            MediumRiskQualityResponseFactor = 5m,
            MediumRiskGrowthResponseFactor = 5m,
            HighRiskQualityResponseFactor = 5m,
            HighRiskGrowthResponseFactor = 5m,
        };

        Assert.True(options.IsValid());
    }

    [Theory]
    [InlineData("1.000001")]
    [InlineData("79228162514264337593543950335")]
    public void MinimumWaitWeightRejectsValuesAboveOne(string value)
    {
        Assert.False(new TradingSignalOptions
        {
            MinimumWaitWeight = decimal.Parse(
                value,
                System.Globalization.CultureInfo.InvariantCulture),
        }.IsValid());
    }

    [Theory]
    [InlineData(nameof(TradingSignalOptions.AggressiveActivityFactor))]
    [InlineData(nameof(TradingSignalOptions.BalancedActivityFactor))]
    [InlineData(nameof(TradingSignalOptions.ConservativeActivityFactor))]
    [InlineData(nameof(TradingSignalOptions.LowRiskQualityResponseFactor))]
    [InlineData(nameof(TradingSignalOptions.LowRiskGrowthResponseFactor))]
    [InlineData(nameof(TradingSignalOptions.MediumRiskQualityResponseFactor))]
    [InlineData(nameof(TradingSignalOptions.MediumRiskGrowthResponseFactor))]
    [InlineData(nameof(TradingSignalOptions.HighRiskQualityResponseFactor))]
    [InlineData(nameof(TradingSignalOptions.HighRiskGrowthResponseFactor))]
    public void PersonalityResponseFactorsMustRemainPositive(string propertyName)
    {
        var options = new TradingSignalOptions();
        typeof(TradingSignalOptions).GetProperty(propertyName)!.SetValue(options, 0m);

        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData(nameof(TradingSignalOptions.AggressiveActivityFactor))]
    [InlineData(nameof(TradingSignalOptions.BalancedActivityFactor))]
    [InlineData(nameof(TradingSignalOptions.ConservativeActivityFactor))]
    [InlineData(nameof(TradingSignalOptions.LowRiskQualityResponseFactor))]
    [InlineData(nameof(TradingSignalOptions.LowRiskGrowthResponseFactor))]
    [InlineData(nameof(TradingSignalOptions.MediumRiskQualityResponseFactor))]
    [InlineData(nameof(TradingSignalOptions.MediumRiskGrowthResponseFactor))]
    [InlineData(nameof(TradingSignalOptions.HighRiskQualityResponseFactor))]
    [InlineData(nameof(TradingSignalOptions.HighRiskGrowthResponseFactor))]
    public void PersonalityResponseFactorsRejectValuesAboveFive(string propertyName)
    {
        var options = new TradingSignalOptions();
        typeof(TradingSignalOptions).GetProperty(propertyName)!.SetValue(options, 5.000001m);

        Assert.False(options.IsValid());
    }

    [Fact]
    public void PersonalityResponseFactorsRejectDecimalMaximum()
    {
        var options = new TradingSignalOptions
        {
            AggressiveActivityFactor = decimal.MaxValue,
        };

        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData(-0.01, 0.05)]
    [InlineData(0.06, 0.05)]
    [InlineData(0.01, 101)]
    public void PassivePriceOffsetUsesOneOrderedMagnitudeBandForBothSides(
        double minimum,
        double maximum)
    {
        var options = new RandomChanceRatesOptions();
        options.RandomMagnitudeBands.PassivePriceOffsetMinPercent = (decimal)minimum;
        options.RandomMagnitudeBands.PassivePriceOffsetMaxPercent = (decimal)maximum;

        Assert.False(options.IsValid());
    }
}
