using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record AiMarketSnapshot(
    int ParticipantId,
    bool IsFundMember,
    AiMarketState Market,
    AiMarketSettings Settings,
    AiParticipantSnapshot Participant,
    IReadOnlyList<AiCompanySnapshot> Companies,
    IReadOnlyList<AiIndustrySnapshot> Industries,
    IReadOnlyList<AiCapitalizationHistoryPoint> CapitalizationHistory,
    IReadOnlyList<AiSentimentHistoryPoint> SentimentHistory,
    IReadOnlyList<AiApplicationFeedback> RecentApplicationFeedback,
    IReadOnlyList<AiBigInvestmentOpportunity> BigInvestmentOpportunities);

public sealed record AiMarketState(
    int CycleNumber,
    int TradingDayNumber,
    int TradingCycleNumber,
    int RemainingTradingCycles,
    string Session,
    bool IsFinalDecisionOfDay,
    AiActiveCrisis? ActiveCrisis);

public sealed record AiActiveCrisis(string Title, string Scope, int CyclesRemaining);

public sealed record AiMarketSettings(
    decimal? TradeFeeRate,
    int SettlementLagTradingDays,
    bool MarginEnabled,
    decimal InitialMarginRate,
    decimal MaintenanceMarginRate,
    int MaxOrdersPerDecision);

public sealed record AiParticipantSnapshot(
    int Id,
    string Temperament,
    string RiskProfile,
    decimal CurrentBalance,
    decimal SettledCash,
    decimal UnsettledCash,
    decimal Reserved,
    decimal Available,
    decimal BuyingPower,
    decimal LoanLiability,
    decimal MarginLiability,
    decimal NetWorth,
    IReadOnlyList<AiHoldingSnapshot> Holdings,
    IReadOnlyList<AiOpenOrder> OpenOrders,
    AiExposureSnapshot? Exposure);

public sealed record AiExposureSnapshot(
    decimal CurrentPercent,
    decimal MinimumPercent,
    decimal MaximumPercent,
    string Position);

public sealed record AiHoldingSnapshot(
    int CompanyId,
    int Quantity,
    int SettledQuantity,
    decimal AverageCost,
    decimal CurrentPrice);

public sealed record AiOpenOrder(
    int OrderId,
    int CompanyId,
    string Side,
    int Quantity,
    int RemainingQuantity,
    decimal LimitPrice,
    string Status,
    bool CanCancel,
    string? CancellationRestriction);

public sealed record AiCompanySnapshot(
    int CompanyId,
    string Name,
    int IndustryId,
    decimal CurrentPrice,
    string? TradingStatus,
    decimal? AllowedMinimumPrice,
    decimal? AllowedMaximumPrice,
    decimal? ActiveLowerPrice,
    decimal? ActiveUpperPrice,
    int IssuedShares,
    decimal? BestExecutableSellPrice,
    int BestExecutableSellQuantity,
    decimal? MaximumPrioritySafeBuyPrice,
    AiBuyEnvelopeSnapshot? BuyEnvelope,
    IReadOnlyList<AiRatingSnapshot> RecentRatings,
    AiCompanyFinancialSnapshot? Financials = null,
    AiAuditEvidenceSnapshot? Audit = null,
    AiDirectionalSignalSnapshot? DirectionalSignals = null);

public sealed record AiBuyEnvelopeSnapshot(
    decimal OrderPrice,
    decimal MaximumBudget,
    int MinimumQuantity,
    int MaximumQuantity,
    bool IsPassive);

public sealed record AiRatingSnapshot(string Rating, decimal? ImpactPercent, int CycleNumber);

public sealed record AiIndustrySnapshot(int Id, string Name, int SentimentValue);

public sealed record AiCapitalizationHistoryPoint(int CompanyId, int CycleNumber, decimal? Capitalization);

public sealed record AiSentimentHistoryPoint(int IndustryId, int CycleNumber, int SentimentValue);

public sealed record AiApplicationFeedback(
    long CallId,
    int SnapshotCycleNumber,
    string Status,
    IReadOnlyList<AiApplicationRejectionFeedback> Rejections);

public sealed record AiApplicationRejectionFeedback(
    string Code,
    string Reason,
    int? MinimumQuantity = null,
    int? MaximumQuantity = null,
    decimal? MinimumPrice = null,
    decimal? MaximumPrice = null,
    decimal? MaximumBudget = null);

// Slim projection of BigInvestmentOpportunity: company name, price, and capitalization already appear in Companies,
// so only the funding bounds are sent to the model.
public sealed record AiBigInvestmentOpportunity(
    int CompanyId,
    decimal CurrentPrice,
    int MinimumShares,
    int MaximumShares);

// Builds a fresh, batched view of the current market for one participant. It reuses the same latest-price map,
// capitalization (price x issued shares), worth, buying-power, and price-bound formulas the rest of the backend
// uses, and only reads recorded history so gaps stay gaps. Returns null when there is no running market cycle.
public sealed class AiMarketSnapshotBuilder(
    AppDbContext dbContext,
    MarginService marginService,
    TradingClockService tradingClockService,
    IOptions<AiTradingOptions> aiOptions,
    IOptions<TradeFeeOptions> tradeFeeOptions,
    IOptions<SettlementOptions> settlementOptions,
    IOptions<MarginOptions> marginOptions,
    IOptions<VolatilityHaltOptions> volatilityHaltOptions,
    IOptions<BigInvestmentOptions> bigInvestmentOptions,
    IOptions<RandomChanceRatesOptions> chanceRates,
    AutomatedBuyOrderPolicy automatedBuyOrderPolicy,
    IOptions<TradingSignalOptions>? tradingSignalOptions = null)
{
    private readonly TradingSignalOptions tradingSignals =
        tradingSignalOptions?.Value ?? new TradingSignalOptions();

    private sealed class ShadowAskLevel(decimal price, long remainingQuantity)
    {
        public decimal Price { get; } = price;

        public long RemainingQuantity { get; set; } = remainingQuantity;
    }

    public async Task<AiMarketSnapshot?> BuildAsync(int participantId, bool isFinalDecisionOfDay = false)
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market?.CurrentCycleId is not { } currentCycleId)
        {
            return null;
        }

        var evidencePoint = await (
                from cycle in dbContext.MarketCycles.AsNoTracking()
                join day in dbContext.TradingDays.AsNoTracking()
                    on cycle.TradingDayId equals day.Id
                where cycle.Id == currentCycleId
                    && cycle.MarketRunId == market.CurrentRunId
                    && cycle.TradingDayId == market.CurrentTradingDayId
                    && day.Id == market.CurrentTradingDayId
                select new TradingEvidencePoint(
                    cycle.MarketRunId,
                    cycle.CycleNumber,
                    day.DayNumber,
                    cycle.TradingCycleNumber))
            .FirstOrDefaultAsync();
        var participant = await dbContext.Participants.FirstOrDefaultAsync(candidate => candidate.Id == participantId);
        if (evidencePoint is null || participant is null)
        {
            return null;
        }

        var clock = await tradingClockService.GetStateAsync(market);
        if (clock is null
            || clock.TradingDayNumber != evidencePoint.TradingDayNumber
            || clock.TradingCycleNumber != evidencePoint.TradingCycleNumber)
        {
            return null;
        }

        var currentCycleNumber = evidencePoint.CycleNumber;
        var isFundMember = await dbContext.CollectiveFundParticipants
            .AnyAsync(member => member.ParticipantId == participantId);

        var prices = await PriceSnapshotQueries.LatestPriceByCompanyAtOrBeforeCycleAsync(
            dbContext,
            market.CurrentRunId,
            currentCycleNumber);
        var cycleNumbersById = await dbContext.MarketCycles
            .Where(cycle => cycle.MarketRunId == evidencePoint.MarketRunId
                && cycle.CycleNumber <= evidencePoint.CycleNumber)
            .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

        var historyCycleIds = await dbContext.MarketCycles
            .Where(cycle => cycle.MarketRunId == evidencePoint.MarketRunId
                && cycle.CycleNumber <= currentCycleNumber)
            .OrderByDescending(cycle => cycle.CycleNumber)
            .Take(aiOptions.Value.HistoryCycles)
            .Select(cycle => cycle.Id)
            .ToListAsync();

        var state = await BuildMarketStateAsync(currentCycleNumber, clock, isFinalDecisionOfDay);
        var settings = BuildSettings();
        var participantSnapshot = await BuildParticipantAsync(participant, prices);
        var companies = await BuildCompaniesAsync(
            prices,
            cycleNumbersById,
            participant,
            participantSnapshot,
            evidencePoint.MarketRunId,
            currentCycleNumber,
            evidencePoint.TradingDayNumber,
            historyCycleIds);
        var industries = await BuildIndustriesAsync();
        // Capitalization and sentiment history are the largest, most redundant sections because each company's and
        // industry's current value is already carried elsewhere; both are downsampled into fixed 6-cycle averaged
        // periods so a long window reads as a handful of representative points instead of one per cycle.
        // Capitalization is additionally limited to companies the participant can act on this cycle (current holdings
        // and companies with a live buyEnvelope), since it is by far the largest section.
        var relevantCompanyIds = participantSnapshot.Holdings.Select(holding => holding.CompanyId)
            .Concat(companies.Where(company => company.BuyEnvelope is not null).Select(company => company.CompanyId))
            .ToHashSet();
        var capitalizationHistory =
            await BuildCapitalizationHistoryAsync(historyCycleIds, relevantCompanyIds, cycleNumbersById);
        var sentimentHistory = await BuildSentimentHistoryAsync(historyCycleIds, cycleNumbersById);
        var feedback = await BuildRecentApplicationFeedbackAsync(participantId);
        IReadOnlyList<AiBigInvestmentOpportunity> bigInvestmentOpportunities =
            isFundMember || !bigInvestmentOptions.Value.Enabled
            ? []
            : companies
                .Select(company => BigInvestmentService.BuildOpportunity(
                    participant,
                    company.CompanyId,
                    company.Name,
                    company.CurrentPrice,
                    company.IssuedShares,
                    chanceRates.Value.RandomMagnitudeBands))
                .OfType<BigInvestmentOpportunity>()
                .Select(opportunity => new AiBigInvestmentOpportunity(
                    opportunity.CompanyId,
                    opportunity.CurrentPrice,
                    opportunity.MinimumShares,
                    opportunity.MaximumShares))
                .ToList();

        return new AiMarketSnapshot(
            participantId,
            isFundMember,
            state,
            settings,
            participantSnapshot,
            companies,
            industries,
            capitalizationHistory,
            sentimentHistory,
            feedback,
            bigInvestmentOpportunities);
    }

    private async Task<AiMarketState> BuildMarketStateAsync(
        int currentCycleNumber,
        TradingClockState? clock,
        bool isFinalDecisionOfDay)
    {
        var crisis = await dbContext.Crises
            .Where(candidate => currentCycleNumber > candidate.TriggeredInCycleNumber
                && currentCycleNumber <= candidate.TriggeredInCycleNumber + candidate.DurationCycles)
            .OrderByDescending(candidate => candidate.TriggeredInCycleNumber)
            .FirstOrDefaultAsync();

        var activeCrisis = crisis is null
            ? null
            : new AiActiveCrisis(
                crisis.Title,
                crisis.Scope.ToString(),
                crisis.TriggeredInCycleNumber + crisis.DurationCycles - currentCycleNumber);

        return new AiMarketState(
            currentCycleNumber,
            clock?.TradingDayNumber ?? 0,
            clock?.TradingCycleNumber ?? 0,
            clock?.RemainingTradingCycles ?? 0,
            clock?.TradingSessionState.ToString() ?? "Unknown",
            isFinalDecisionOfDay,
            activeCrisis);
    }

    private AiMarketSettings BuildSettings()
    {
        var tradeFee = tradeFeeOptions.Value;
        var margin = marginOptions.Value;
        return new AiMarketSettings(
            tradeFee.Enabled ? tradeFee.FeeRate : null,
            settlementOptions.Value.SettlementLagTradingDays,
            margin.Enabled,
            margin.InitialMarginRate,
            margin.MaintenanceMarginRate,
            aiOptions.Value.MaxOrdersPerDecision);
    }

    private async Task<IReadOnlyList<AiCompanySnapshot>> BuildCompaniesAsync(
        IReadOnlyDictionary<int, decimal> prices,
        IReadOnlyDictionary<int, int> cycleNumbersById,
        Participant participant,
        AiParticipantSnapshot participantSnapshot,
        int marketRunId,
        int currentCycleNumber,
        int currentTradingDayNumber,
        IReadOnlyCollection<int> signalCycleIds)
    {
        var companies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null)
            .ToListAsync();
        var companyIds = companies.Select(company => company.Id).ToList();
        var evidence = await TradingEvidenceReader.LoadAsync(
            dbContext,
            companyIds,
            new TradingEvidencePoint(
                marketRunId,
                currentCycleNumber,
                currentTradingDayNumber));

        var bands = await dbContext.PriceBandStates
            .Where(band => companyIds.Contains(band.CompanyId))
            .ToDictionaryAsync(band => band.CompanyId);
        var industryIds = companies.Select(company => company.IndustryId).Distinct().ToList();
        var sentimentByIndustry = await dbContext.Industries.AsNoTracking()
            .Where(industry => industryIds.Contains(industry.Id))
            .ToDictionaryAsync(industry => industry.Id, industry => industry.SentimentValue);

        var orderFlowByCompany = (await dbContext.Orders.AsNoTracking()
                .Where(order => companyIds.Contains(order.CompanyId)
                    && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
                    && order.ParticipantId != null
                    && order.Quantity > order.FilledQuantity)
                .GroupBy(order => order.CompanyId)
                .Select(group => new
                {
                    CompanyId = group.Key,
                    Buys = group.Sum(order => order.Type == OrderType.Buy
                        ? (long)(order.Quantity - order.FilledQuantity)
                        : 0L),
                    Sells = group.Sum(order => order.Type == OrderType.Sell
                        ? (long)(order.Quantity - order.FilledQuantity)
                        : 0L),
                })
                .ToListAsync())
            .ToDictionary(
                row => row.CompanyId,
                row =>
                {
                    var total = row.Buys + row.Sells;
                    return total == 0L
                        ? 0m
                        : Math.Clamp((decimal)(row.Buys - row.Sells) / total, -1m, 1m);
                });
        var signalPriceRows = await dbContext.PriceSnapshots.AsNoTracking()
            .Where(snapshot => companyIds.Contains(snapshot.CompanyId)
                && signalCycleIds.Contains(snapshot.CreatedInCycleId))
            .Select(snapshot => new
            {
                snapshot.Id,
                snapshot.CompanyId,
                snapshot.CreatedInCycleId,
                snapshot.Price,
            })
            .ToListAsync();
        var priceChangeByCompany = signalPriceRows
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var ordered = group
                        .OrderByDescending(snapshot => cycleNumbersById.GetValueOrDefault(snapshot.CreatedInCycleId))
                        .ThenByDescending(snapshot => snapshot.Id)
                        .ToList();
                    var latest = ordered[0];
                    var previous = ordered.FirstOrDefault(snapshot =>
                        snapshot.CreatedInCycleId != latest.CreatedInCycleId);
                    return previous is { Price: > 0m }
                        ? (latest.Price - previous.Price) / previous.Price
                        : 0m;
                });

        var sellOrders = await dbContext.Orders
            .Where(order => companyIds.Contains(order.CompanyId)
                && order.ParticipantId != participant.Id
                && order.Type == OrderType.Sell
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .Select(order => new
            {
                order.CompanyId,
                order.LimitPrice,
                RemainingQuantity = order.Quantity - order.FilledQuantity,
            })
            .ToListAsync();
        var buyOrders = await dbContext.Orders
            .Where(order => companyIds.Contains(order.CompanyId)
                && order.Type == OrderType.Buy
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .Select(order => new
            {
                order.Id,
                order.CompanyId,
                order.LimitPrice,
                order.CreatedAt,
                RemainingQuantity = order.Quantity - order.FilledQuantity,
            })
            .ToListAsync();

        var holdingsValue = participantSnapshot.Holdings.Sum(holding => holding.Quantity * holding.CurrentPrice);
        var exposure = automatedBuyOrderPolicy.AssessExposure(
            participant.RiskProfile,
            participantSnapshot.NetWorth,
            holdingsValue);
        var halt = volatilityHaltOptions.Value;
        return companies.Select(company =>
        {
            var price = prices.GetValueOrDefault(company.Id);
            bands.TryGetValue(company.Id, out var band);
            var bounds = OrderPriceBounds.Resolve(
                band, price,
                halt.LowerBandPercent, halt.UpperBandPercent,
                halt.AllowedOrderLowerPercent, halt.AllowedOrderUpperPercent);

            var executableSellLevels = sellOrders
                .Where(order => order.CompanyId == company.Id
                    && order.RemainingQuantity > 0
                    && bounds is not null
                    && bounds.IsWithinActiveBand(order.LimitPrice))
                .GroupBy(order => order.LimitPrice)
                .OrderBy(level => level.Key)
                .Select(level => new ShadowAskLevel(
                    level.Key,
                    level.Sum(order => (long)order.RemainingQuantity)))
                .ToList();
            decimal? maximumPrioritySafeBuyPrice = null;
            foreach (var buy in buyOrders
                         .Where(order => order.CompanyId == company.Id
                             && order.RemainingQuantity > 0
                             && bounds is not null
                             && bounds.IsWithinActiveBand(order.LimitPrice))
                         .OrderByDescending(order => order.LimitPrice)
                         .ThenBy(order => order.CreatedAt)
                         .ThenBy(order => order.Id))
            {
                var consumedQuantity = ConsumeShadowAskLevels(
                    executableSellLevels,
                    buy.LimitPrice,
                    buy.RemainingQuantity);
                if (consumedQuantity > 0)
                {
                    maximumPrioritySafeBuyPrice = maximumPrioritySafeBuyPrice is decimal currentLimit
                        ? Math.Min(currentLimit, buy.LimitPrice)
                        : buy.LimitPrice;
                }
            }

            var bestSellPrice = executableSellLevels.Count == 0
                ? null
                : (decimal?)executableSellLevels[0].Price;
            var bestSellQuantity = executableSellLevels.Count == 0
                ? 0
                : (int)Math.Min(executableSellLevels[0].RemainingQuantity, int.MaxValue);

            var passivePriorityPrice = maximumPrioritySafeBuyPrice is decimal priorityCeiling
                && bounds is not null
                && bounds.IsWithinActiveBand(priorityCeiling)
                && (bestSellPrice is null
                    || (bestSellPrice > priorityCeiling
                        && exposure?.Position != AutomatedExposurePosition.Below))
                    ? priorityCeiling
                    : (decimal?)null;
            var envelopePrice = passivePriorityPrice ?? bestSellPrice ?? price;
            var envelopeExecutableQuantity = passivePriorityPrice is not null ? 0 : bestSellQuantity;
            var envelope = band?.State is null or LuldState.Normal
                && (maximumPrioritySafeBuyPrice is null || envelopePrice <= maximumPrioritySafeBuyPrice)
                ? automatedBuyOrderPolicy.BuildBuyEnvelope(new AutomatedBuyOrderInput(
                    participant.RiskProfile,
                    participantSnapshot.NetWorth,
                    holdingsValue,
                    participant.ReservedBalance,
                    participantSnapshot.Available,
                    participantSnapshot.BuyingPower,
                    participantSnapshot.MarginLiability,
                    envelopePrice,
                    company.IssuedSharesCount,
                    envelopeExecutableQuantity))
                : null;

            var auditRow = evidence.EffectiveAudits.GetValueOrDefault(company.Id);
            IReadOnlyList<AiRatingSnapshot> ratings = auditRow is not null
                ? new[]
                {
                    new AiRatingSnapshot(
                        auditRow.Rating.Rating.ToString(),
                        auditRow.Rating.ImpactPercent,
                        auditRow.CreatedCycleNumber),
                }
                : [];
            var audit = auditRow?.DecisionEvidence;
            var financials = evidence.LatestFinancials.GetValueOrDefault(company.Id);
            var components = TradingSignalCalculator.Calculate(
                new CompanyQuote(
                    company.Id,
                    price,
                    priceChangeByCompany.GetValueOrDefault(company.Id),
                    orderFlowByCompany.GetValueOrDefault(company.Id),
                    SectorSentiment: sentimentByIndustry.GetValueOrDefault(company.IndustryId),
                    Bounds: bounds,
                    IssuedShares: company.IssuedSharesCount,
                    Audit: audit,
                    Financials: financials),
                participant.RiskProfile,
                tradingSignals);

            return new AiCompanySnapshot(
                company.Id,
                company.Name,
                company.IndustryId,
                price,
                band is { State: var status } && status != LuldState.Normal ? status.ToString() : null,
                bounds?.AllowedMinimumPrice,
                bounds?.AllowedMaximumPrice,
                bounds?.ActiveLowerPrice,
                bounds?.ActiveUpperPrice,
                company.IssuedSharesCount,
                bestSellPrice,
                bestSellQuantity,
                maximumPrioritySafeBuyPrice,
                envelope is null
                    ? null
                    : new AiBuyEnvelopeSnapshot(
                        envelopePrice,
                        envelope.MaximumBudget,
                        envelope.MinimumQuantity,
                        envelope.MaximumQuantity,
                        envelope.IsPassive),
                ratings,
                ToAiFinancialSnapshot(financials),
                auditRow is null
                    ? null
                    : BuildAiAuditSnapshot(auditRow.Rating.Rating, auditRow.Evidence),
                new AiDirectionalSignalSnapshot(
                    components.Momentum,
                    components.OrderFlow,
                    components.Industry,
                    components.Audit,
                    components.Fundamental,
                    components.Final));
        }).ToList();
    }

    private static AiCompanyFinancialSnapshot? ToAiFinancialSnapshot(LatestFinancialEvidence? financials) =>
        financials is null
            ? null
            : new AiCompanyFinancialSnapshot(
                financials.SnapshotId,
                financials.TradingDayNumber,
                financials.Moment.ToString(),
                financials.Current,
                financials.Deltas,
                financials.ProfitabilityScore,
                financials.ProfitabilityLevel.ToString(),
                financials.StabilityScore,
                financials.FinancialVolatilityLevel.ToString(),
                financials.ClosureRiskScore,
                financials.ClosureRiskLevel.ToString(),
                financials.ManagementOutlook.ToString(),
                financials.ManagementConfidenceScore,
                financials.LatestDividendOutcome?.ToString(),
                financials.LatestDividendDeclaredAmount,
                financials.LatestDividendFundedAmount);

    private static AiAuditEvidenceSnapshot BuildAiAuditSnapshot(
        CompanyRiskRating rating,
        CompanyAuditEvidence evidence) =>
        new(
            rating.ToString(),
            evidence.TotalScore,
            evidence.EvaluationStartTradingDayNumber,
            evidence.EvaluationEndTradingDayNumber,
            evidence.EffectiveTradingDayNumber,
            evidence.AdjustedReturnScore,
            evidence.CycleJumpScore,
            evidence.FreeShareEmissionScore,
            evidence.DenominationScore,
            evidence.DividendOutcomeScore,
            evidence.DividendCoverageScore,
            evidence.IndustryScore,
            evidence.ProfitabilityFactorScore,
            evidence.StabilityFactorScore,
            evidence.ClosureRiskFactorScore,
            evidence.ManagementOutlookFactorScore,
            evidence.StartPrice,
            evidence.EndPrice,
            evidence.AdjustedReturnPercent,
            evidence.MaximumAdjustedCycleMovePercent,
            evidence.OpeningIssuedShares,
            evidence.EmittedShares,
            evidence.FreeShareDilutionPercent,
            evidence.StockSplitCount,
            evidence.ReverseSplitCount,
            evidence.IssuerCash,
            evidence.ModeledMaximumDividend,
            evidence.DividendCoverageRatio,
            evidence.OpeningIndustrySentiment,
            evidence.ClosingIndustrySentiment,
            evidence.IndustryTrend.ToString());

    private static long ConsumeShadowAskLevels(
        List<ShadowAskLevel> levels,
        decimal buyLimit,
        long buyQuantity)
    {
        var initialQuantity = buyQuantity;
        while (buyQuantity > 0 && levels.Count > 0 && levels[0].Price <= buyLimit)
        {
            var level = levels[0];
            var consumed = Math.Min(buyQuantity, level.RemainingQuantity);
            level.RemainingQuantity -= consumed;
            buyQuantity -= consumed;
            if (level.RemainingQuantity == 0)
            {
                levels.RemoveAt(0);
            }
        }

        return initialQuantity - buyQuantity;
    }

    private async Task<IReadOnlyList<AiIndustrySnapshot>> BuildIndustriesAsync()
        => await dbContext.Industries
            .Select(industry => new AiIndustrySnapshot(industry.Id, industry.Name, industry.SentimentValue))
            .ToListAsync();

    private async Task<AiParticipantSnapshot> BuildParticipantAsync(
        Participant participant,
        IReadOnlyDictionary<int, decimal> prices)
    {
        var holdings = await dbContext.Holdings
            .Where(holding => holding.ParticipantId == participant.Id && holding.Quantity > 0)
            .ToListAsync();

        var holdingSnapshots = holdings.Select(holding =>
        {
            var price = prices.GetValueOrDefault(holding.CompanyId);
            return new AiHoldingSnapshot(
                holding.CompanyId,
                holding.Quantity,
                holding.SettledQuantity,
                holding.AverageCost,
                price);
        }).ToList();
        var holdingsValue = holdingSnapshots.Sum(holding => holding.Quantity * holding.CurrentPrice);

        var openOrders = await dbContext.Orders
            .Where(order => order.ParticipantId == participant.Id
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .Select(order => new AiOpenOrder(
                order.Id,
                order.CompanyId,
                order.Type.ToString(),
                order.Quantity,
                order.Quantity - order.FilledQuantity,
                order.LimitPrice,
                order.Status.ToString(),
                order.RelatedMarginCallId == null && order.RelatedLoanId == null,
                order.RelatedMarginCallId != null
                    ? "MarginCall"
                    : order.RelatedLoanId != null
                        ? "LoanDistress"
                        : null))
            .ToListAsync();

        var loanLiability = (await LoanService.OpenLoanLiabilityByParticipantAsync(dbContext))
            .GetValueOrDefault(participant.Id);

        var marginLiability = (await MarginService.LiabilityByParticipantAsync(dbContext))
            .GetValueOrDefault(participant.Id);
        var buyingPower = await marginService.GetBuyingPowerAsync(participant.Id, prices);
        var netWorth = participant.CurrentBalance + holdingsValue - loanLiability - marginLiability;
        var exposure = automatedBuyOrderPolicy.AssessExposure(participant.RiskProfile, netWorth, holdingsValue);

        return new AiParticipantSnapshot(
            participant.Id,
            participant.Temperament.ToString(),
            participant.RiskProfile.ToString(),
            participant.CurrentBalance,
            participant.SettledCashBalance,
            participant.CurrentBalance - participant.SettledCashBalance,
            participant.ReservedBalance,
            participant.AvailableBalance,
            buyingPower,
            loanLiability,
            marginLiability,
            netWorth,
            holdingSnapshots,
            openOrders,
            exposure is null
                ? null
                : new AiExposureSnapshot(
                    exposure.CurrentExposurePercent,
                    exposure.Target.MinimumExposurePercent,
                    exposure.Target.MaximumExposurePercent,
                    exposure.Position.ToString()));
    }

    private async Task<IReadOnlyList<AiApplicationFeedback>> BuildRecentApplicationFeedbackAsync(int participantId)
    {
        var currentRunId = await dbContext.Markets.Select(market => market.CurrentRunId).SingleOrDefaultAsync();
        var calls = await dbContext.AiTraderCalls
            .Where(call => call.ParticipantId == participantId
                && (call.MarketRunId == currentRunId || call.MarketRunId == null)
                && (call.ApplicationResultJson != null || call.Error != null))
            .OrderByDescending(call => call.Id)
            .Take(3)
            .Select(call => new
            {
                call.Id,
                call.SnapshotCycleNumber,
                call.Status,
                call.ApplicationResultJson,
                call.Error,
            })
            .ToListAsync();

        return calls.Select(call => new AiApplicationFeedback(
            call.Id,
            call.SnapshotCycleNumber,
            call.Status.ToString(),
            ExtractRejections(call.ApplicationResultJson, call.Error)))
            .ToList();
    }

    private static IReadOnlyList<AiApplicationRejectionFeedback> ExtractRejections(
        string? applicationResultJson,
        string? error)
    {
        var rejections = new List<AiApplicationRejectionFeedback>();
        if (!string.IsNullOrWhiteSpace(error))
        {
            rejections.Add(new("provider_error", error));
        }

        if (string.IsNullOrWhiteSpace(applicationResultJson))
        {
            return rejections;
        }

        try
        {
            using var document = JsonDocument.Parse(applicationResultJson);
            foreach (var propertyName in new[] { "cancellations", "orders" })
            {
                if (!document.RootElement.TryGetProperty(propertyName, out var results)
                    || results.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var result in results.EnumerateArray())
                {
                    if (result.TryGetProperty("applied", out var applied)
                        && !applied.GetBoolean()
                        && result.TryGetProperty("rejectionReason", out var reason)
                        && reason.GetString() is { Length: > 0 } text)
                    {
                        rejections.Add(ReadConstraintFeedback(
                            result,
                            text,
                            propertyName == "orders" ? "order_rejected" : "cancellation_rejected"));
                    }
                }
            }

            if (document.RootElement.TryGetProperty("bigInvestment", out var investment)
                && investment.ValueKind == JsonValueKind.Object
                && investment.TryGetProperty("applied", out var investmentApplied)
                && !investmentApplied.GetBoolean()
                && investment.TryGetProperty("rejectionReason", out var investmentReason)
                && investmentReason.GetString() is { Length: > 0 } investmentText)
            {
                rejections.Add(new("investment_rejected", investmentText));
            }
        }
        catch (JsonException)
        {
            return rejections;
        }

        return rejections
            .DistinctBy(rejection => (rejection.Code, rejection.Reason))
            .Take(5)
            .ToList();
    }

    private static AiApplicationRejectionFeedback ReadConstraintFeedback(
        JsonElement result,
        string reason,
        string fallbackCode)
    {
        if (!result.TryGetProperty("constraintFeedback", out var feedback)
            || feedback.ValueKind != JsonValueKind.Object)
        {
            return new(fallbackCode, reason);
        }

        var code = feedback.TryGetProperty("code", out var codeElement)
            ? codeElement.GetString() ?? fallbackCode
            : fallbackCode;
        return new AiApplicationRejectionFeedback(
            code,
            reason,
            OptionalInt(feedback, "minimumQuantity"),
            OptionalInt(feedback, "maximumQuantity"),
            OptionalDecimal(feedback, "minimumPrice"),
            OptionalDecimal(feedback, "maximumPrice"),
            OptionalDecimal(feedback, "maximumBudget"));
    }

    private static int? OptionalInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;

    private static decimal? OptionalDecimal(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetDecimal()
            : null;

    // Consecutive cycles are averaged into fixed-width periods so a long window compresses to a few representative
    // points; averaging also smooths single-cycle spikes better than sampling one cycle per period would.
    private const int HistoryBucketCycles = 6;

    // Snaps a cycle to the most-recent cycle of its fixed-width bucket, counting back from the newest cycle so the
    // latest bucket always aligns to it and both history charts share one period grid.
    private static int BucketCycle(int newestCycleNumber, int cycleNumber)
        => newestCycleNumber - ((newestCycleNumber - cycleNumber) / HistoryBucketCycles) * HistoryBucketCycles;

    private async Task<IReadOnlyList<AiCapitalizationHistoryPoint>> BuildCapitalizationHistoryAsync(
        IReadOnlyList<int> historyCycleIds,
        IReadOnlyCollection<int> companyIds,
        IReadOnlyDictionary<int, int> cycleNumbersById)
    {
        if (companyIds.Count == 0)
        {
            return [];
        }

        var snapshots = await dbContext.PriceSnapshots
            .Where(snapshot => historyCycleIds.Contains(snapshot.CreatedInCycleId)
                && companyIds.Contains(snapshot.CompanyId))
            .Select(snapshot => new { snapshot.Id, snapshot.CompanyId, snapshot.CreatedInCycleId, snapshot.Capitalization })
            .ToListAsync();

        var latestPerCycle = snapshots
            .GroupBy(snapshot => new { snapshot.CompanyId, snapshot.CreatedInCycleId })
            .Select(group => group.OrderByDescending(snapshot => snapshot.Id).First())
            .Select(snapshot => new
            {
                snapshot.CompanyId,
                CycleNumber = cycleNumbersById.GetValueOrDefault(snapshot.CreatedInCycleId),
                snapshot.Capitalization,
            })
            .Where(point => point.Capitalization is not null)
            .ToList();

        if (latestPerCycle.Count == 0)
        {
            return [];
        }

        // Buckets are counted back from the most recent cycle so the latest period always aligns to the current cycle.
        var newestCycleNumber = latestPerCycle.Max(point => point.CycleNumber);

        return latestPerCycle
            .GroupBy(point => new
            {
                point.CompanyId,
                BucketCycle = BucketCycle(newestCycleNumber, point.CycleNumber),
            })
            .Select(group => new AiCapitalizationHistoryPoint(
                group.Key.CompanyId,
                group.Key.BucketCycle,
                Math.Round(group.Average(point => point.Capitalization!.Value), 2)))
            .OrderBy(point => point.CompanyId)
            .ThenBy(point => point.CycleNumber)
            .ToList();
    }

    private async Task<IReadOnlyList<AiSentimentHistoryPoint>> BuildSentimentHistoryAsync(
        IReadOnlyList<int> historyCycleIds,
        IReadOnlyDictionary<int, int> cycleNumbersById)
    {
        var snapshots = await dbContext.SectorSentimentSnapshots
            .Where(snapshot => historyCycleIds.Contains(snapshot.CreatedInCycleId))
            .Select(snapshot => new { snapshot.Id, snapshot.IndustryId, snapshot.CreatedInCycleId, snapshot.SentimentValue })
            .ToListAsync();

        var latestPerCycle = snapshots
            .GroupBy(snapshot => new { snapshot.IndustryId, snapshot.CreatedInCycleId })
            .Select(group => group.OrderByDescending(snapshot => snapshot.Id).First())
            .Select(snapshot => new
            {
                snapshot.IndustryId,
                CycleNumber = cycleNumbersById.GetValueOrDefault(snapshot.CreatedInCycleId),
                snapshot.SentimentValue,
            })
            .ToList();

        if (latestPerCycle.Count == 0)
        {
            return [];
        }

        var newestCycleNumber = latestPerCycle.Max(point => point.CycleNumber);

        return latestPerCycle
            .GroupBy(point => new
            {
                point.IndustryId,
                BucketCycle = BucketCycle(newestCycleNumber, point.CycleNumber),
            })
            .Select(group => new AiSentimentHistoryPoint(
                group.Key.IndustryId,
                group.Key.BucketCycle,
                (int)Math.Round(group.Average(point => point.SentimentValue), MidpointRounding.AwayFromZero)))
            .OrderBy(point => point.IndustryId)
            .ThenBy(point => point.CycleNumber)
            .ToList();
    }
}
