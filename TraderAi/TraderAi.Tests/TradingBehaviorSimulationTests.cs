using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class TradingBehaviorSimulationTests
{
    private static readonly int[] FixedSeeds = [17, 101, 313, 509, 701, 907, 1201, 1601];
    private const int IterationsPerSeed = 128;

    [Fact]
    public async Task NeutralMirroredMarketIsSymmetricWithoutMaterialUpwardDrift()
    {
        var result = await RunSimulationAsync(
            SyntheticSignal.Neutral,
            Temperament.Balanced,
            priceScale: 1m);

        Assert.Equal(FixedSeeds.Length * IterationsPerSeed, result.TotalDecisions);
        Assert.InRange(Math.Abs(result.Buys - result.Sells), 0, result.TotalDecisions / 20);
        Assert.InRange(Math.Abs(result.MeanPassiveOffset), 0m, 0.005m);
        Assert.True(result.MidpointExecutions > 0);
        Assert.Equal(0, result.MidpointViolations);
        Assert.InRange(Math.Abs(result.AggregatePriceDirection), 0m, 0.01m);
        Assert.Equal(0, result.SelectedUp);
        Assert.Equal(0, result.SelectedDown);
        Assert.Equal(result.Buys + result.Sells, result.SelectedFlat);
    }

    [Fact]
    public async Task SimulationIsInvariantToPriceAndProportionalOrderBookScale()
    {
        var baselineOrderFlow = await CaptureProductionOrderFlowAsync(bookScale: 1);
        var scaledOrderFlow = await CaptureProductionOrderFlowAsync(bookScale: 10);
        Assert.Equal(0.5m, baselineOrderFlow);
        Assert.Equal(baselineOrderFlow, scaledOrderFlow);

        var baseline = await RunSimulationAsync(
            SyntheticSignal.Positive,
            Temperament.Balanced,
            priceScale: 1m,
            bookScale: 1,
            orderFlowImbalance: baselineOrderFlow);
        var priceScaled = await RunSimulationAsync(
            SyntheticSignal.Positive,
            Temperament.Balanced,
            priceScale: 10m,
            bookScale: 1,
            orderFlowImbalance: baselineOrderFlow);
        var bookScaled = await RunSimulationAsync(
            SyntheticSignal.Positive,
            Temperament.Balanced,
            priceScale: 1m,
            bookScale: 10,
            orderFlowImbalance: scaledOrderFlow);

        foreach (var scaled in new[] { priceScaled, bookScaled })
        {
            Assert.Equal(baseline.Buys, scaled.Buys);
            Assert.Equal(baseline.Sells, scaled.Sells);
            Assert.Equal(baseline.Waits, scaled.Waits);
            Assert.Equal(baseline.SelectedUp, scaled.SelectedUp);
            Assert.Equal(baseline.SelectedDown, scaled.SelectedDown);
            Assert.Equal(baseline.SelectedFlat, scaled.SelectedFlat);
            Assert.Equal(baseline.MidpointExecutions, scaled.MidpointExecutions);
            Assert.Equal(0, scaled.MidpointViolations);
            Assert.InRange(Math.Abs(baseline.MeanPassiveOffset - scaled.MeanPassiveOffset), 0m, 0.0002m);
        }
        Assert.InRange(
            Math.Abs(baseline.AggregatePriceDirection - priceScaled.AggregatePriceDirection),
            0m,
            0.0002m);
        Assert.Equal(baseline.AggregatePriceDirection, bookScaled.AggregatePriceDirection);
        Assert.Equal(baseline.ExecutedQuantity * 10, bookScaled.ExecutedQuantity);
    }

    [Fact]
    public async Task MirroredFundamentalsAndAuditsShiftActionsAndPredictionsWithoutCertainty()
    {
        var neutral = await RunSimulationAsync(
            SyntheticSignal.Neutral,
            Temperament.Balanced,
            priceScale: 1m,
            orderFlowImbalance: 0m);
        var positive = await RunSimulationAsync(
            SyntheticSignal.Positive,
            Temperament.Balanced,
            priceScale: 1m,
            orderFlowImbalance: 0m);
        var negative = await RunSimulationAsync(
            SyntheticSignal.Negative,
            Temperament.Balanced,
            priceScale: 1m,
            orderFlowImbalance: 0m);

        Assert.All(new[] { neutral, positive, negative }, result => Assert.Equal(0m, result.OrderFlowImbalance));
        Assert.True(positive.Buys > neutral.Buys);
        Assert.True(negative.Sells > neutral.Sells);
        Assert.True(positive.Buys > negative.Buys);
        Assert.True(negative.Sells > positive.Sells);
        Assert.Equal(positive.Buys + positive.Sells, positive.SelectedUp);
        Assert.Equal(negative.Buys + negative.Sells, negative.SelectedDown);
        Assert.True(positive.Sells > 0);
        Assert.True(positive.Waits > 0);
        Assert.True(negative.Buys > 0);
        Assert.True(negative.Waits > 0);
        Assert.InRange(Math.Abs(positive.Buys - negative.Sells), 0, positive.TotalDecisions / 20);
        Assert.InRange(Math.Abs(positive.Sells - negative.Buys), 0, positive.TotalDecisions / 20);
        Assert.True(positive.AggregatePriceDirection > neutral.AggregatePriceDirection);
        Assert.True(negative.AggregatePriceDirection < neutral.AggregatePriceDirection);
        Assert.Equal(0, positive.MidpointViolations);
        Assert.Equal(0, negative.MidpointViolations);
    }

    [Fact]
    public async Task AggressiveAndConservativeTemperamentsProduceDifferentNeutralDistributions()
    {
        var conservative = await RunSimulationAsync(
            SyntheticSignal.Neutral,
            Temperament.Conservative,
            priceScale: 1m,
            includeMatching: false);
        var aggressive = await RunSimulationAsync(
            SyntheticSignal.Neutral,
            Temperament.Aggressive,
            priceScale: 1m,
            includeMatching: false);

        Assert.True(aggressive.Buys + aggressive.Sells > conservative.Buys + conservative.Sells);
        Assert.True(aggressive.Waits < conservative.Waits);
        Assert.NotEqual(
            (conservative.Buys, conservative.Sells, conservative.Waits),
            (aggressive.Buys, aggressive.Sells, aggressive.Waits));
        Assert.InRange(Math.Abs(conservative.Buys - conservative.Sells), 0, conservative.TotalDecisions / 20);
        Assert.InRange(Math.Abs(aggressive.Buys - aggressive.Sells), 0, aggressive.TotalDecisions / 20);
        Assert.Equal(conservative.Buys + conservative.Sells, conservative.SelectedFlat);
        Assert.Equal(aggressive.Buys + aggressive.Sells, aggressive.SelectedFlat);
    }

    private static async Task<SimulationResult> RunSimulationAsync(
        SyntheticSignal signal,
        Temperament temperament,
        decimal priceScale,
        int bookScale = 1,
        bool includeMatching = true,
        decimal orderFlowImbalance = 0m)
    {
        var referencePrice = 100m * priceScale;
        var quote = Quote(signal, referencePrice, bookScale, orderFlowImbalance);
        var intents = new List<OrderIntent>();
        var buys = 0;
        var sells = 0;
        var waits = 0;
        var selectedUp = 0;
        var selectedDown = 0;
        var selectedFlat = 0;
        var passiveOffsetSum = 0m;

        foreach (var seed in FixedSeeds)
        {
            for (var iteration = 0; iteration < IterationsPerSeed; iteration++)
            {
                var context = Context(quote, temperament, referencePrice, bookScale);
                var decisionSeed = unchecked((seed * 397) ^ (iteration * 7919));
                var engine = new RuleBasedDecisionEngine(
                    new ScaleAwareTradeSizer(bookScale),
                    Options.Create(new RandomChanceRatesOptions()),
                    new Random(decisionSeed));
                var evaluation = engine.Evaluate(context);
                var decided = engine.Decide(context);
                if (decided.Count == 0)
                {
                    waits++;
                    continue;
                }

                var intent = Assert.Single(decided);
                intents.Add(intent);
                if (intent.Type == OrderType.Buy)
                {
                    buys++;
                }
                else
                {
                    sells++;
                }

                var direction = evaluation.DirectionalScores[intent.CompanyId];
                if (direction > 0m)
                {
                    selectedUp++;
                }
                else if (direction < 0m)
                {
                    selectedDown++;
                }
                else
                {
                    selectedFlat++;
                }

                passiveOffsetSum += (intent.LimitPrice - referencePrice) / referencePrice;
            }
        }

        var matching = includeMatching
            ? await RunMatchingAsync(referencePrice, intents)
            : new MatchingSummary(0, 0, 0, 0m);
        return new SimulationResult(
            buys,
            sells,
            waits,
            selectedUp,
            selectedDown,
            selectedFlat,
            intents.Count == 0 ? 0m : passiveOffsetSum / intents.Count,
            matching.Executions,
            matching.MidpointViolations,
            matching.AggregatePriceDirection,
            matching.ExecutedQuantity,
            quote.OrderFlowImbalance);
    }

    private static DecisionContext Context(
        CompanyQuote quote,
        Temperament temperament,
        decimal referencePrice,
        int bookScale)
    {
        var availableCash = referencePrice * 10_000m * bookScale;
        var participant = new Participant
        {
            Name = "Synthetic trader",
            Type = ParticipantType.Individual,
            Temperament = temperament,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = availableCash,
            CurrentBalance = availableCash,
            IsActive = true,
        };
        return new DecisionContext(
            participant,
            availableCash,
            [quote],
            new Dictionary<int, int> { [quote.CompanyId] = 10_000 * bookScale },
            new HashSet<int>());
    }

    private static CompanyQuote Quote(
        SyntheticSignal signal,
        decimal referencePrice,
        int bookScale,
        decimal orderFlowImbalance)
    {
        return new CompanyQuote(
            CompanyId: 1,
            Price: referencePrice,
            OrderFlowImbalance: orderFlowImbalance,
            Bounds: OrderPriceBounds.FromReference(referencePrice, 15m, 10m, 25m, 15m),
            IssuedShares: 10_000 * bookScale,
            Audit: signal switch
            {
                SyntheticSignal.Positive => Audit(CompanyRiskRating.ExtraRaisedExpectations),
                SyntheticSignal.Negative => Audit(CompanyRiskRating.HighRisk),
                _ => null,
            },
            Financials: signal switch
            {
                SyntheticSignal.Positive => Financial(
                    profitability: 90m,
                    stability: 90m,
                    closureRisk: 10m,
                    outlook: ManagementOutlook.Positive,
                    coverage: 2m),
                SyntheticSignal.Negative => Financial(
                    profitability: 10m,
                    stability: 10m,
                    closureRisk: 90m,
                    outlook: ManagementOutlook.Negative,
                    coverage: 0m),
                _ => null,
            });
    }

    private static async Task<decimal> CaptureProductionOrderFlowAsync(int bookScale)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        var company = await context.Companies.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        var buyer = await context.Participants.SingleAsync(participant => participant.Name == "Bob");
        var seller = await context.Participants.SingleAsync(participant => participant.Name == "Alice");
        var sellerHolding = await context.Holdings.SingleAsync();
        var buyQuantity = 75 * bookScale;
        var sellQuantity = 25 * bookScale;
        var buyPrice = 90m;
        var now = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);

        company.IssuedSharesCount = 10_000 * bookScale;
        sellerHolding.Quantity = 100 * bookScale;
        sellerHolding.SettledQuantity = sellerHolding.Quantity;
        seller.InitialBalance = 10_000m * bookScale;
        seller.CurrentBalance = seller.InitialBalance;
        seller.SettledCashBalance = seller.InitialBalance;
        buyer.InitialBalance = 100_000m * bookScale;
        buyer.CurrentBalance = buyer.InitialBalance;
        buyer.SettledCashBalance = buyer.InitialBalance;
        buyer.ReservedBalance = buyQuantity * buyPrice;
        context.Orders.AddRange(
            new Order
            {
                ParticipantId = buyer.Id,
                CompanyId = company.Id,
                Type = OrderType.Buy,
                Status = OrderStatus.Open,
                Quantity = buyQuantity,
                LimitPrice = buyPrice,
                ReservedCashAmount = buyer.ReservedBalance,
                CreatedInCycleId = cycle.Id,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Order
            {
                ParticipantId = seller.Id,
                CompanyId = company.Id,
                Type = OrderType.Sell,
                Status = OrderStatus.Open,
                Quantity = sellQuantity,
                LimitPrice = 110m,
                CreatedInCycleId = cycle.Id,
                CreatedAt = now.AddTicks(1),
                UpdatedAt = now.AddTicks(1),
            },
            new Order
            {
                // A large issuer ask proves that only participant quantities enter the directional flow.
                ParticipantId = null,
                CompanyId = company.Id,
                Type = OrderType.Sell,
                Status = OrderStatus.Open,
                Quantity = 5_000 * bookScale,
                LimitPrice = 100m,
                CreatedInCycleId = cycle.Id,
                CreatedAt = now.AddTicks(2),
                UpdatedAt = now.AddTicks(2),
            });
        await context.SaveChangesAsync();

        var engine = new QuoteCapturingDecisionEngine();
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            engine,
            new MarketCycleLock(),
            new Random(1));

        var generated = await service.GenerateDecisionsAsync();

        Assert.True(generated.Success);
        return Assert.Single(engine.LastQuotes!).OrderFlowImbalance;
    }

    private static EffectiveAuditEvidence Audit(CompanyRiskRating rating) => new(
        rating,
        TotalScore: 0,
        EvaluationStartTradingDayNumber: 1,
        EvaluationEndTradingDayNumber: 2,
        EffectiveTradingDayNumber: 3,
        AdjustedReturnScore: 0,
        CycleJumpScore: 0,
        FreeShareEmissionScore: 0,
        DenominationScore: 0,
        DividendOutcomeScore: 0,
        DividendCoverageScore: 0,
        IndustryScore: 0,
        ProfitabilityFactorScore: 0,
        StabilityFactorScore: 0,
        ClosureRiskFactorScore: 0,
        ManagementOutlookFactorScore: 0);

    private static LatestFinancialEvidence Financial(
        decimal profitability,
        decimal stability,
        decimal closureRisk,
        ManagementOutlook outlook,
        decimal coverage)
    {
        var current = new CompanyFinancialValues(
            Revenue: 1_000m,
            NetProfit: 100m,
            OperatingCashFlow: 120m,
            TotalAssets: 2_000m,
            TotalLiabilities: 700m,
            TotalDebt: 300m,
            ExpectedDividendPerShare: 2m,
            ExpectedDividendPool: 200m,
            DividendCoverageRatio: coverage,
            BusinessRiskScore: closureRisk,
            ManagementRevenueForecast: outlook == ManagementOutlook.Positive ? 1_200m : 800m,
            ManagementProfitForecast: outlook == ManagementOutlook.Positive ? 130m : 70m,
            ManagementOperatingCashFlowForecast: outlook == ManagementOutlook.Positive ? 150m : 90m);
        var deltas = new CompanyFinancialDeltas(
            Revenue: 0m,
            NetProfit: 0m,
            OperatingCashFlow: 0m,
            TotalAssets: 0m,
            TotalLiabilities: 0m,
            TotalDebt: 0m,
            ExpectedDividendPerShare: 0m,
            ExpectedDividendPool: 0m,
            DividendCoverageRatio: 0m,
            BusinessRiskScore: 0m,
            ManagementRevenueForecast: 0m,
            ManagementProfitForecast: 0m,
            ManagementOperatingCashFlowForecast: 0m,
            ManagementConfidenceScore: 0m);

        return new LatestFinancialEvidence(
            SnapshotId: 1,
            TradingDayNumber: 1,
            CompanyFinancialSnapshotMoment.Midday,
            current,
            deltas,
            ProfitabilityScore: profitability,
            ProfitabilityLevel: profitability >= 50m ? CompanyMetricLevel.High : CompanyMetricLevel.Low,
            StabilityScore: stability,
            FinancialVolatilityLevel: stability >= 50m ? CompanyMetricLevel.Low : CompanyMetricLevel.High,
            ClosureRiskScore: closureRisk,
            ClosureRiskLevel: closureRisk >= 50m ? CompanyMetricLevel.High : CompanyMetricLevel.Low,
            ManagementOutlook: outlook,
            ManagementConfidenceScore: 80m,
            LatestDividendOutcome: DividendFundingOutcome.Paid,
            LatestDividendDeclaredAmount: 200m,
            LatestDividendFundedAmount: 200m);
    }

    private static async Task<MatchingSummary> RunMatchingAsync(
        decimal referencePrice,
        IReadOnlyList<OrderIntent> intents)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var now = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);
        var cycle = new MarketCycle
        {
            CycleNumber = 1,
            Status = CycleStatus.Running,
            StartedAt = now,
        };
        var company = new Company
        {
            Name = "Synthetic company",
            IssuedSharesCount = Math.Max(
                1,
                intents.Where(intent => intent.Type == OrderType.Sell).Sum(intent => intent.Quantity)),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var buyNotional = intents
            .Where(intent => intent.Type == OrderType.Buy)
            .Sum(intent => intent.Quantity * intent.LimitPrice);
        var buyer = new Participant
        {
            Name = "Synthetic buyer",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = Math.Max(referencePrice, buyNotional * 2m),
            CurrentBalance = Math.Max(referencePrice, buyNotional * 2m),
            SettledCashBalance = Math.Max(referencePrice, buyNotional * 2m),
            IsActive = true,
        };
        var seller = new Participant
        {
            Name = "Synthetic seller",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 0m,
            CurrentBalance = 0m,
            IsActive = true,
        };
        context.AddRange(cycle, company, buyer, seller);
        await context.SaveChangesAsync();

        var sellQuantity = intents
            .Where(intent => intent.Type == OrderType.Sell)
            .Sum(intent => intent.Quantity);
        if (sellQuantity > 0)
        {
            context.Holdings.Add(new Holding
            {
                ParticipantId = seller.Id,
                CompanyId = company.Id,
                Quantity = sellQuantity,
                SettledQuantity = sellQuantity,
                AverageCost = referencePrice,
            });
        }

        var orders = intents.Select((intent, index) => new Order
        {
            ParticipantId = intent.Type == OrderType.Buy ? buyer.Id : seller.Id,
            CompanyId = company.Id,
            Type = intent.Type,
            Status = OrderStatus.Open,
            Quantity = intent.Quantity,
            LimitPrice = intent.LimitPrice,
            ReservedCashAmount = intent.Type == OrderType.Buy
                ? intent.Quantity * intent.LimitPrice
                : 0m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now.AddTicks(index),
            UpdatedAt = now.AddTicks(index),
        }).ToList();
        buyer.ReservedBalance = orders
            .Where(order => order.Type == OrderType.Buy)
            .Sum(order => order.ReservedCashAmount);
        context.Orders.AddRange(orders);
        await context.SaveChangesAsync();

        await new MatchingEngine(context).RunAsync(cycle);
        await context.SaveChangesAsync();

        var ordersById = await context.Orders
            .AsNoTracking()
            .ToDictionaryAsync(order => order.Id);
        var fills = await context.OrderFills
            .AsNoTracking()
            .OrderBy(fill => fill.Id)
            .ToListAsync();
        var midpointViolations = fills.Count(fill =>
        {
            var buyLimit = ordersById[fill.BuyOrderId].LimitPrice;
            var sellLimit = ordersById[fill.SellOrderId].LimitPrice;
            var expected = Math.Round((buyLimit + sellLimit) / 2m, 2, MidpointRounding.AwayFromZero);
            return fill.ExecutionPrice != expected;
        });
        var aggregateDirection = fills.Count == 0
            ? 0m
            : fills.Average(fill => (fill.ExecutionPrice - referencePrice) / referencePrice);
        return new MatchingSummary(
            fills.Count,
            midpointViolations,
            fills.Sum(fill => fill.Quantity),
            aggregateDirection);
    }

    private enum SyntheticSignal
    {
        Neutral,
        Positive,
        Negative,
    }

    private sealed record SimulationResult(
        int Buys,
        int Sells,
        int Waits,
        int SelectedUp,
        int SelectedDown,
        int SelectedFlat,
        decimal MeanPassiveOffset,
        int MidpointExecutions,
        int MidpointViolations,
        decimal AggregatePriceDirection,
        int ExecutedQuantity,
        decimal OrderFlowImbalance)
    {
        public int TotalDecisions => Buys + Sells + Waits;
    }

    private sealed record MatchingSummary(
        int Executions,
        int MidpointViolations,
        int ExecutedQuantity,
        decimal AggregatePriceDirection);

    private sealed class ScaleAwareTradeSizer(int scale) : ITradeSizer
    {
        public int Size(Temperament temperament, int maxQuantity) => Math.Min(scale, maxQuantity);
    }

    private sealed class QuoteCapturingDecisionEngine : IDecisionEngine
    {
        public IReadOnlyList<CompanyQuote>? LastQuotes { get; private set; }

        public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
        {
            LastQuotes = context.Companies.ToArray();
            return [];
        }
    }
}
