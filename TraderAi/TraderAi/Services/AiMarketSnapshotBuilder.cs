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
    AiOrderBookSnapshot OrderBook,
    IReadOnlyList<AiCapitalizationHistoryPoint> CapitalizationHistory,
    IReadOnlyList<AiSentimentHistoryPoint> SentimentHistory,
    IReadOnlyList<AiApplicationFeedback> RecentApplicationFeedback,
    IReadOnlyList<BigInvestmentOpportunity> BigInvestmentOpportunities);

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
    decimal CurrentPrice,
    decimal CurrentValue,
    decimal UnrealizedResult);

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
    string IndustryName,
    decimal CurrentPrice,
    decimal Capitalization,
    string TradingStatus,
    decimal? AllowedMinimumPrice,
    decimal? AllowedMaximumPrice,
    decimal? ActiveLowerPrice,
    decimal? ActiveUpperPrice,
    int IssuedShares,
    decimal? BestExecutableSellPrice,
    int BestExecutableSellQuantity,
    decimal? MaximumPrioritySafeBuyPrice,
    AiBuyEnvelopeSnapshot? BuyEnvelope,
    IReadOnlyList<AiRatingSnapshot> RecentRatings);

public sealed record AiBuyEnvelopeSnapshot(
    decimal OrderPrice,
    decimal MaximumBudget,
    int MinimumQuantity,
    int MaximumQuantity,
    bool IsPassive,
    string StateBasis);

public sealed record AiRatingSnapshot(string Rating, decimal? ImpactPercent, int CycleNumber);

public sealed record AiIndustrySnapshot(int Id, string Name, int SentimentValue);

public sealed record AiOrderBookSnapshot(IReadOnlyList<AiBookEntry> Buys, IReadOnlyList<AiBookEntry> Sells);

public sealed record AiBookEntry(int CompanyId, decimal LimitPrice, int RemainingQuantity);

public sealed record AiCapitalizationHistoryPoint(int CompanyId, int CycleNumber, decimal? Capitalization);

public sealed record AiSentimentHistoryPoint(int IndustryId, int CycleNumber, int SentimentValue);

public sealed record AiApplicationFeedback(
    long CallId,
    int SnapshotCycleNumber,
    string Status,
    IReadOnlyList<string> RejectionReasons);

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
    AutomatedBuyOrderPolicy automatedBuyOrderPolicy)
{
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

        var currentCycle = await dbContext.MarketCycles.FirstOrDefaultAsync(cycle => cycle.Id == currentCycleId);
        var participant = await dbContext.Participants.FirstOrDefaultAsync(candidate => candidate.Id == participantId);
        if (currentCycle is null || participant is null)
        {
            return null;
        }

        var currentCycleNumber = currentCycle.CycleNumber;
        var clock = await tradingClockService.GetStateAsync(market);
        var isFundMember = await dbContext.CollectiveFundParticipants
            .AnyAsync(member => member.ParticipantId == participantId);

        var prices = await PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);
        var cycleNumbersById = await dbContext.MarketCycles
            .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

        var historyCycleIds = await dbContext.MarketCycles
            .Where(cycle => cycle.CycleNumber <= currentCycleNumber)
            .OrderByDescending(cycle => cycle.CycleNumber)
            .Take(aiOptions.Value.HistoryCycles)
            .Select(cycle => cycle.Id)
            .ToListAsync();

        var state = await BuildMarketStateAsync(currentCycleNumber, clock, isFinalDecisionOfDay);
        var settings = BuildSettings();
        var participantSnapshot = await BuildParticipantAsync(participant, prices);
        var companies = await BuildCompaniesAsync(prices, cycleNumbersById, participant, participantSnapshot);
        var industries = await BuildIndustriesAsync();
        var orderBook = await BuildOrderBookAsync();
        // Capitalization and sentiment history are the largest, most redundant snapshot sections because each
        // company's and industry's current value is already carried elsewhere. Shortening their lookback trims the
        // provider payload with little decision cost: capitalization keeps half the window and sentiment about 70%.
        var capitalizationCycleIds = historyCycleIds.Take(Math.Max(1, historyCycleIds.Count / 2)).ToList();
        var sentimentCycleIds = historyCycleIds.Take(Math.Max(1, historyCycleIds.Count * 7 / 10)).ToList();
        var capitalizationHistory = await BuildCapitalizationHistoryAsync(capitalizationCycleIds, cycleNumbersById);
        var sentimentHistory = await BuildSentimentHistoryAsync(sentimentCycleIds, cycleNumbersById);
        var feedback = await BuildRecentApplicationFeedbackAsync(participantId);
        IReadOnlyList<BigInvestmentOpportunity> bigInvestmentOpportunities =
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
                .ToList();

        return new AiMarketSnapshot(
            participantId,
            isFundMember,
            state,
            settings,
            participantSnapshot,
            companies,
            industries,
            orderBook,
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
        AiParticipantSnapshot participantSnapshot)
    {
        var companies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null)
            .ToListAsync();
        var companyIds = companies.Select(company => company.Id).ToList();

        var bands = await dbContext.PriceBandStates
            .Where(band => companyIds.Contains(band.CompanyId))
            .ToDictionaryAsync(band => band.CompanyId);

        var industryNames = await dbContext.Industries
            .ToDictionaryAsync(industry => industry.Id, industry => industry.Name);

        var recentRatings = (await dbContext.CompanyRatings
                .Where(rating => companyIds.Contains(rating.CompanyId))
                .OrderByDescending(rating => rating.Id)
                .ToListAsync())
            .GroupBy(rating => rating.CompanyId)
            .ToDictionary(group => group.Key, group => group.Take(3).ToList());

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

        var holdingsValue = participantSnapshot.Holdings.Sum(holding => holding.CurrentValue);
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

            var ratings = (recentRatings.GetValueOrDefault(company.Id) ?? [])
                .Select(rating => new AiRatingSnapshot(
                    rating.Rating.ToString(),
                    rating.ImpactPercent,
                    cycleNumbersById.GetValueOrDefault(rating.CreatedInCycleId)))
                .ToList();

            return new AiCompanySnapshot(
                company.Id,
                company.Name,
                company.IndustryId,
                industryNames.GetValueOrDefault(company.IndustryId, string.Empty),
                price,
                price * company.IssuedSharesCount,
                band?.State.ToString() ?? nameof(LuldState.Normal),
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
                        envelope.IsPassive,
                        "CurrentOpenOrdersBeforeCancellations"),
                ratings);
        }).ToList();
    }

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

    private async Task<AiOrderBookSnapshot> BuildOrderBookAsync()
    {
        var openOrders = await dbContext.Orders
            .Where(order => order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
            .Select(order => new
            {
                order.CompanyId,
                order.Type,
                order.LimitPrice,
                Remaining = order.Quantity - order.FilledQuantity,
            })
            .ToListAsync();

        var buys = openOrders
            .Where(order => order.Type == OrderType.Buy)
            .Select(order => new AiBookEntry(order.CompanyId, order.LimitPrice, order.Remaining))
            .ToList();
        var sells = openOrders
            .Where(order => order.Type == OrderType.Sell)
            .Select(order => new AiBookEntry(order.CompanyId, order.LimitPrice, order.Remaining))
            .ToList();

        return new AiOrderBookSnapshot(buys, sells);
    }

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
                price,
                holding.Quantity * price,
                (price - holding.AverageCost) * holding.Quantity);
        }).ToList();
        var holdingsValue = holdingSnapshots.Sum(holding => holding.CurrentValue);

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
        var calls = await dbContext.AiTraderCalls
            .Where(call => call.ParticipantId == participantId
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
            ExtractRejectionReasons(call.ApplicationResultJson, call.Error)))
            .ToList();
    }

    private static IReadOnlyList<string> ExtractRejectionReasons(string? applicationResultJson, string? error)
    {
        var reasons = new List<string>();
        if (!string.IsNullOrWhiteSpace(error))
        {
            reasons.Add(error);
        }

        if (string.IsNullOrWhiteSpace(applicationResultJson))
        {
            return reasons;
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
                        reasons.Add(text);
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
                reasons.Add(investmentText);
            }
        }
        catch (JsonException)
        {
            return reasons;
        }

        return reasons.Distinct().Take(5).ToList();
    }

    private async Task<IReadOnlyList<AiCapitalizationHistoryPoint>> BuildCapitalizationHistoryAsync(
        IReadOnlyList<int> historyCycleIds,
        IReadOnlyDictionary<int, int> cycleNumbersById)
    {
        var snapshots = await dbContext.PriceSnapshots
            .Where(snapshot => historyCycleIds.Contains(snapshot.CreatedInCycleId))
            .Select(snapshot => new { snapshot.Id, snapshot.CompanyId, snapshot.CreatedInCycleId, snapshot.Capitalization })
            .ToListAsync();

        return snapshots
            .GroupBy(snapshot => new { snapshot.CompanyId, snapshot.CreatedInCycleId })
            .Select(group => group.OrderByDescending(snapshot => snapshot.Id).First())
            .Select(snapshot => new AiCapitalizationHistoryPoint(
                snapshot.CompanyId,
                cycleNumbersById.GetValueOrDefault(snapshot.CreatedInCycleId),
                snapshot.Capitalization))
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

        return snapshots
            .GroupBy(snapshot => new { snapshot.IndustryId, snapshot.CreatedInCycleId })
            .Select(group => group.OrderByDescending(snapshot => snapshot.Id).First())
            .Select(snapshot => new AiSentimentHistoryPoint(
                snapshot.IndustryId,
                cycleNumbersById.GetValueOrDefault(snapshot.CreatedInCycleId),
                snapshot.SentimentValue))
            .OrderBy(point => point.IndustryId)
            .ThenBy(point => point.CycleNumber)
            .ToList();
    }
}
