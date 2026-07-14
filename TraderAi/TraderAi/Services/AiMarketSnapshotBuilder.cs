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
    IReadOnlyList<AiSentimentHistoryPoint> SentimentHistory);

public sealed record AiMarketState(int CycleNumber, int TradingDayNumber, string Session, AiActiveCrisis? ActiveCrisis);

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
    IReadOnlyList<AiOpenOrder> OpenOrders);

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
    string Status);

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
    IReadOnlyList<AiRatingSnapshot> RecentRatings);

public sealed record AiRatingSnapshot(string Rating, decimal? ImpactPercent, int CycleNumber);

public sealed record AiIndustrySnapshot(int Id, string Name, int SentimentValue);

public sealed record AiOrderBookSnapshot(IReadOnlyList<AiBookEntry> Buys, IReadOnlyList<AiBookEntry> Sells);

public sealed record AiBookEntry(int CompanyId, decimal LimitPrice, int RemainingQuantity);

public sealed record AiCapitalizationHistoryPoint(int CompanyId, int CycleNumber, decimal? Capitalization);

public sealed record AiSentimentHistoryPoint(int IndustryId, int CycleNumber, int SentimentValue);

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
    IOptions<VolatilityHaltOptions> volatilityHaltOptions)
{
    public async Task<AiMarketSnapshot?> BuildAsync(int participantId)
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

        var state = await BuildMarketStateAsync(currentCycleNumber, clock);
        var settings = BuildSettings();
        var companies = await BuildCompaniesAsync(prices, cycleNumbersById);
        var industries = await BuildIndustriesAsync();
        var orderBook = await BuildOrderBookAsync();
        var participantSnapshot = await BuildParticipantAsync(participant, prices);
        var capitalizationHistory = await BuildCapitalizationHistoryAsync(historyCycleIds, cycleNumbersById);
        var sentimentHistory = await BuildSentimentHistoryAsync(historyCycleIds, cycleNumbersById);

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
            sentimentHistory);
    }

    private async Task<AiMarketState> BuildMarketStateAsync(int currentCycleNumber, TradingClockState? clock)
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
            clock?.TradingSessionState.ToString() ?? "Unknown",
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
        IReadOnlyDictionary<int, int> cycleNumbersById)
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

        var halt = volatilityHaltOptions.Value;
        return companies.Select(company =>
        {
            var price = prices.GetValueOrDefault(company.Id);
            bands.TryGetValue(company.Id, out var band);
            var bounds = OrderPriceBounds.Resolve(
                band, price,
                halt.LowerBandPercent, halt.UpperBandPercent,
                halt.AllowedOrderLowerPercent, halt.AllowedOrderUpperPercent);

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
                ratings);
        }).ToList();
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
                order.Status.ToString()))
            .ToListAsync();

        var loanLiability = await dbContext.Loans
            .Where(loan => loan.ParticipantId == participant.Id && loan.Status == LoanStatus.Open)
            .SumAsync(loan => loan.RemainingPrincipal + loan.PastDueInterest + loan.AccruedFees);

        var marginLiability = (await MarginService.LiabilityByParticipantAsync(dbContext))
            .GetValueOrDefault(participant.Id);
        var buyingPower = await marginService.GetBuyingPowerAsync(participant.Id, prices);

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
            participant.CurrentBalance + holdingsValue - loanLiability - marginLiability,
            holdingSnapshots,
            openOrders);
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
