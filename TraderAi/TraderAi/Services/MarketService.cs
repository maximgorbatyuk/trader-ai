using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record PlaceOrderResult(bool Success, Order? Order, string? Error)
{
    public static PlaceOrderResult Ok(Order order) => new(true, order, null);

    public static PlaceOrderResult Fail(string error) => new(false, null, error);
}

public sealed record AdvanceCycleResult(bool Success, int? CompletedCycleNumber, int FillCount, string? Error)
{
    public static AdvanceCycleResult Ok(int completedCycleNumber, int fillCount) =>
        new(true, completedCycleNumber, fillCount, null);

    public static AdvanceCycleResult Fail(string error) => new(false, null, 0, error);
}

public sealed record RunDecisionsResult(bool Success, int OrdersPlaced, string? Error)
{
    public static RunDecisionsResult Ok(int ordersPlaced) => new(true, ordersPlaced, null);

    public static RunDecisionsResult Fail(string error) => new(false, 0, error);
}

public sealed record CycleTickResult(bool Ran, int OrdersPlaced, int FillCount, int? CompletedCycleNumber)
{
    public static CycleTickResult Skipped() => new(false, 0, 0, null);

    public static CycleTickResult Executed(int ordersPlaced, int fillCount, int? completedCycleNumber) =>
        new(true, ordersPlaced, fillCount, completedCycleNumber);
}

public sealed record CreatePlayerResult(bool Success, Participant? Player, string? Error)
{
    public static CreatePlayerResult Ok(Participant player) => new(true, player, null);

    public static CreatePlayerResult Fail(string error) => new(false, null, error);
}

public sealed record CancelOrderResult(bool Success, Order? Order, string? Error)
{
    public static CancelOrderResult Ok(Order order) => new(true, order, null);

    public static CancelOrderResult Fail(string error) => new(false, null, error);
}

public sealed record PlayerFundResult(bool Success, string? Error)
{
    public static PlayerFundResult Ok() => new(true, null);

    public static PlayerFundResult Fail(string error) => new(false, error);
}

// What a single advertisement would cost the fund right now: the price (a fraction of fund worth), the fraction
// itself, the 20-cycle net-worth growth that set it (as a percent), the fund worth it is charged against, and
// the fund's current popularity.
public sealed record FundAdvertiseQuote(decimal Price, decimal Fraction, decimal GrowthPct, decimal FundWorth, int PopularityIndex);

public sealed record FundAdvertiseQuoteResult(bool Success, FundAdvertiseQuote? Quote, string? Error)
{
    public static FundAdvertiseQuoteResult Ok(FundAdvertiseQuote quote) => new(true, quote, null);

    public static FundAdvertiseQuoteResult Fail(string error) => new(false, null, error);
}

public sealed class MarketService(
    AppDbContext dbContext,
    MatchingEngine matchingEngine,
    IDecisionEngine decisionEngine,
    MarketCycleLock cycleLock,
    Random random,
    NewsService? newsService = null,
    CrisisService? crisisService = null,
    ScienceInvestigationService? scienceService = null,
    BankruptcyService? bankruptcyService = null,
    CollectiveFundService? collectiveFundService = null,
    MarketExitService? marketExitService = null,
    StockSplitService? stockSplitService = null,
    AuditorService? auditorService = null,
    ShareEmissionService? shareEmissionService = null,
    CompanyLifecycleService? companyLifecycleService = null,
    LoanService? loanService = null,
    VolatilityHaltService? volatilityHaltService = null,
    ConcentrationCapService? concentrationCapService = null,
    IOptions<ArchiveOptions>? archiveOptions = null,
    IOptions<RandomChanceRatesOptions>? chanceRates = null,
    IOptions<LoanOptions>? loanOptions = null,
    IOptions<IndustrySentimentOptions>? industrySentimentOptions = null,
    IndustrySentimentService? industrySentimentService = null,
    BehaviorAuditService? behaviorAuditService = null,
    TradingClockService? tradingClockService = null,
    SettlementService? settlementService = null,
    MarginService? marginService = null,
    IOptions<VolatilityHaltOptions>? volatilityHaltOptions = null,
    IOptions<CollectiveFundOptions>? collectiveFundOptions = null,
    PrimaryIssuanceService? primaryIssuanceService = null,
    AutomatedBuyOrderPolicy? automatedBuyOrderPolicy = null,
    IOptions<AiTradingOptions>? aiTradingOptions = null)
{
    private static readonly IReadOnlyDictionary<int, int> NoHoldings = new Dictionary<int, int>();
    private static readonly IReadOnlySet<int> NoOpenOrders = new HashSet<int>();

    private sealed class ExecutableAskLevel(decimal price, long remainingQuantity)
    {
        public decimal Price { get; } = price;

        public long RemainingQuantity { get; set; } = remainingQuantity;
    }

    // Company cash-window chance/rate values fall back to the built-in defaults for reduced-argument tests.
    private readonly RandomChanceRatesOptions chanceRateValues = chanceRates?.Value ?? new RandomChanceRatesOptions();

    // Loan settings, defaulted when not injected so the buffer/cap math still works in reduced-argument tests.
    private readonly LoanOptions loanOptionValues = loanOptions?.Value ?? new LoanOptions();

    private readonly IndustrySentimentOptions industrySentimentOptionValues =
        industrySentimentOptions?.Value ?? new IndustrySentimentOptions();

    private readonly CollectiveFundOptions collectiveFundOptionValues =
        collectiveFundOptions?.Value ?? new CollectiveFundOptions();

    // Band/allowed-range percentages default to the built-in values so reduced-argument test constructors keep
    // resolving order price bounds without wiring the volatility-halt options.
    private readonly VolatilityHaltOptions volatilityHaltOptionValues =
        volatilityHaltOptions?.Value ?? new VolatilityHaltOptions();

    private readonly AutomatedBuyOrderPolicy automatedBuyPolicy =
        automatedBuyOrderPolicy ?? new AutomatedBuyOrderPolicy(Options.Create(new AutomatedTradingOptions()));

    private readonly int maxAiCancellationIds = Math.Max(
        1,
        aiTradingOptions?.Value.MaxOrdersPerDecision ?? new AiTradingOptions().MaxOrdersPerDecision);

    // How far back the long-range price move is measured for the engine's extreme-move reactions.
    private const int LongRangeWindowCycles = 10;

    // Order ageing: a resting automated order is force-cancelled once this old. Non-AI automated orders may
    // be chased toward the market before the cap, while AI orders keep the exact price they selected.
    private const int OrderMaxAgeCycles = 15;
    private const int OrderRepriceFromAge = 1;
    private const decimal RepriceStep = 0.10m;

    // After this many consecutive cycles unable to afford any share, a holder liquidates to raise cash.
    private const int CashStarvedLimitCycles = 5;

    // A fund advertisement is priced as a fraction of fund worth that falls the faster the fund has grown: a fund
    // flat or down over the window pays the dear fraction, one up by the growth cap or more pays the cheap
    // fraction, linear and clamped between. The growth is measured over this many worth snapshots.
    private const int AdvertiseWindowCycles = 20;
    private const decimal AdvertiseGrowthCap = 0.20m;
    private const decimal AdvertiseDearFraction = 0.10m;
    private const decimal AdvertiseCheapFraction = 0.001m;

    // A trader may reserve for buys beyond its available cash, borrowing up to this share of its total worth
    // (cash plus holdings). The debt is carried as open Loan liability, kept at or under this fraction of worth.
    // Dividends are paid at a random interval drawn in this range. Each paying company draws a rate from the
    // configured dividend band of its capitalisation for the whole payout pool, split evenly across its issued
    // shares — so per share it works out to rate × price and a stock split cuts it proportionally.
    private const int MinDividendIntervalCycles = 10;
    private const int MaxDividendIntervalCycles = 25;

    // A company pays this window only if a per-company roll passes; the chance is high while its capitalisation
    // is stable and low once it has moved sharply, so payouts thin out during volatile stretches.
    private const decimal CapitalizationStabilityThreshold = 0.05m;

    // The shared ceiling prevents company income and dividends from scaling without bound with market cap.
    private const decimal MaxCompanyCashPerWindow = 1_000_000m;

    // Forced-liquidation sells undercut the market by 1–5% so the order actually crosses.
    private const decimal MinSellOffset = 0.01m;
    private const decimal MaxSellOffset = 0.05m;

    // A joining human player starts with a whole-dollar balance drawn from this range.
    private const int PlayerMinBalance = 100_000;
    private const int PlayerMaxBalance = 400_000;

    public Task<Market?> GetMarketAsync() => dbContext.Markets.FirstOrDefaultAsync();

    public Task<PlaceOrderResult> PlaceOrderAsync(
        int participantId,
        int companyId,
        OrderType type,
        int quantity,
        decimal limitPrice) =>
        WithLockAsync(() => PlaceOrderCoreAsync(participantId, companyId, type, quantity, limitPrice));

    // Revalidation and explicit cancellations share the market lock so stale reservations are released before
    // replacement orders are assessed. Exact AI prices and quantities are either accepted unchanged through the
    // ordinary order path or rejected independently against the shared automated-buy policy.
    public Task<AiDecisionApplicationResult> ApplyAiDecisionAsync(
        int participantId,
        int configurationRevision,
        AiTradeDecision decision) =>
        WithLockAsync(() => ApplyAiDecisionCoreAsync(participantId, configurationRevision, decision));

    private async Task<AiDecisionApplicationResult> ApplyAiDecisionCoreAsync(
        int participantId,
        int configurationRevision,
        AiTradeDecision decision)
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        var configuration = await dbContext.AiTraderConfigurations
            .FirstOrDefaultAsync(candidate => candidate.ParticipantId == participantId);
        var participant = await dbContext.Participants
            .FirstOrDefaultAsync(candidate => candidate.Id == participantId);

        var stillCurrent = market?.CurrentCycleId is not null
            && configuration is not null
            && configuration.Revision == configurationRevision
            && participant is { IsActive: true, IsBankrupt: false, Type: ParticipantType.AIAgent };
        if (!stillCurrent)
        {
            return new AiDecisionApplicationResult(false, [], []);
        }

        var priceByCompany = await PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);
        var boundsByCompany = await ResolveOrderPriceBoundsByCompanyAsync(priceByCompany);
        var holdings = await dbContext.Holdings
            .Where(holding => holding.ParticipantId == participantId && holding.Quantity > 0)
            .Select(holding => new { holding.CompanyId, holding.Quantity })
            .ToListAsync();
        var holdingsValue = holdings.Sum(holding =>
            holding.Quantity * priceByCompany.GetValueOrDefault(holding.CompanyId));
        var loanLiability = (await LoanService.OpenLoanLiabilityByParticipantAsync(dbContext))
            .GetValueOrDefault(participantId);
        var marginLiability = (await MarginService.LiabilityByParticipantAsync(dbContext))
            .GetValueOrDefault(participantId);
        var context = new OrderBookContext
        {
            Market = market!,
            CurrentCycleId = market!.CurrentCycleId!.Value,
            Participant = participant!,
            PriceByCompany = priceByCompany,
            BoundsByCompany = boundsByCompany,
            HoldingsValue = holdingsValue,
            LoanLiability = loanLiability,
            MarginLiability = marginLiability,
        };

        var cancellationResults = new AiCancellationApplicationResult?[decision.CancelOrderIds.Length];
        var uniqueIds = new List<int>(Math.Min(decision.CancelOrderIds.Length, maxAiCancellationIds));
        var seenIds = new HashSet<int>();
        for (var index = 0; index < decision.CancelOrderIds.Length; index++)
        {
            var orderId = decision.CancelOrderIds[index];
            if (!seenIds.Add(orderId))
            {
                cancellationResults[index] = new AiCancellationApplicationResult(
                    orderId, false, "Duplicate cancellation ID was rejected.");
                continue;
            }

            if (uniqueIds.Count >= maxAiCancellationIds)
            {
                cancellationResults[index] = new AiCancellationApplicationResult(
                    orderId, false, $"Cancellation limit is {maxAiCancellationIds} unique order IDs per decision.");
                continue;
            }

            uniqueIds.Add(orderId);
        }

        var ordersById = await dbContext.Orders
            .Where(order => uniqueIds.Contains(order.Id))
            .ToDictionaryAsync(order => order.Id);
        var appliedCancellation = false;
        for (var index = 0; index < decision.CancelOrderIds.Length; index++)
        {
            if (cancellationResults[index] is not null)
            {
                continue;
            }

            var orderId = decision.CancelOrderIds[index];
            ordersById.TryGetValue(orderId, out var order);
            if (order?.ParticipantId != participantId)
            {
                cancellationResults[index] = new AiCancellationApplicationResult(
                    orderId, false, "Order does not belong to this AI trader.");
                continue;
            }

            if (order.Status != OrderStatus.Open && order.Status != OrderStatus.PartiallyFilled)
            {
                cancellationResults[index] = new AiCancellationApplicationResult(
                    orderId, false, "Only an open or partially filled order can be cancelled.");
                continue;
            }

            if (order.RelatedMarginCallId is not null)
            {
                cancellationResults[index] = new AiCancellationApplicationResult(
                    orderId, false, "Margin-call orders are managed by the risk service and cannot be cancelled.");
                continue;
            }

            if (order.RelatedLoanId is not null)
            {
                cancellationResults[index] = new AiCancellationApplicationResult(
                    orderId, false, "Loan-distress orders are managed by the risk service and cannot be cancelled.");
                continue;
            }

            OrderCancellation.Cancel(dbContext, order, participant!, context.CurrentCycleId);
            cancellationResults[index] = new AiCancellationApplicationResult(orderId, true, null);
            appliedCancellation = true;
        }

        if (appliedCancellation)
        {
            await dbContext.SaveChangesAsync();
        }

        var (executableAskLevelsByCompany, priorBuyPriorityLimitByCompany) =
            await BuildAiExecutableAskContextAsync(context);
        var results = new List<AiOrderApplicationResult>(decision.Orders.Length);
        for (var index = 0; index < decision.Orders.Length; index++)
        {
            var order = decision.Orders[index];
            var validationError = order.Side == OrderType.Buy
                ? await ValidateAiBuyOrderAsync(
                    context,
                    executableAskLevelsByCompany,
                    priorBuyPriorityLimitByCompany,
                    order)
                : null;
            var placement = validationError is null
                ? await PlaceOrderCoreAsync(
                    participantId,
                    order.CompanyId,
                    order.Side,
                    order.Quantity,
                    order.LimitPrice,
                    context.PriceByCompany,
                    deferSave: false,
                    context.BoundsByCompany)
                : PlaceOrderResult.Fail(validationError);

            if (placement.Success && order.Side == OrderType.Buy)
            {
                var consumedQuantity = ConsumeExecutableAskLevels(
                    executableAskLevelsByCompany,
                    order.CompanyId,
                    order.LimitPrice,
                    order.Quantity);
                if (consumedQuantity > 0)
                {
                    UpdatePriorBuyPriorityLimit(
                        priorBuyPriorityLimitByCompany,
                        order.CompanyId,
                        order.LimitPrice);
                }
            }

            results.Add(new AiOrderApplicationResult(
                index,
                order.Side,
                order.CompanyId,
                order.Quantity,
                order.LimitPrice,
                order.Reason,
                placement.Success,
                placement.Order?.Id,
                placement.Error));
        }

        return new AiDecisionApplicationResult(
            true,
            cancellationResults.Select(result => result!).ToArray(),
            results.ToArray());
    }

    private async Task<string?> ValidateAiBuyOrderAsync(
        OrderBookContext context,
        IReadOnlyDictionary<int, List<ExecutableAskLevel>> executableAskLevelsByCompany,
        IReadOnlyDictionary<int, decimal> priorBuyPriorityLimitByCompany,
        AiTradeOrderDecision order)
    {
        var company = await dbContext.Companies
            .FirstOrDefaultAsync(candidate => candidate.Id == order.CompanyId);
        if (company is null)
        {
            return "Company not found.";
        }

        if (company.ClosedInCycleId is not null)
        {
            return "This company is delisted.";
        }

        if (!context.BoundsByCompany.TryGetValue(order.CompanyId, out var bounds))
        {
            return "No reference price is available for this company yet.";
        }

        if (!bounds.IsWithinAllowedRange(order.LimitPrice))
        {
            return $"Limit price must be between ${bounds.AllowedMinimumPrice:F2} and ${bounds.AllowedMaximumPrice:F2}.";
        }

        if (!bounds.IsWithinActiveBand(order.LimitPrice))
        {
            return $"AI buy limit price must stay inside the active band ${bounds.ActiveLowerPrice:F2}–${bounds.ActiveUpperPrice:F2}.";
        }

        var netWorth = context.Participant.CurrentBalance
            + context.HoldingsValue
            - context.LoanLiability
            - context.MarginLiability;
        var availableCash = context.Participant.AvailableBalance;
        var orderNotional = order.LimitPrice * order.Quantity;

        if (context.Participant.RiskProfile != RiskProfile.High
            && orderNotional > Math.Max(0m, availableCash))
        {
            return "Margin is only available to High risk automated traders.";
        }

        var buyingPower = marginService is null
            ? Math.Max(0m, availableCash)
            : (await marginService.GetReadOnlyMetricsAsync(context.Participant, context.HoldingsValue)).BuyingPower;

        var executableSells = executableAskLevelsByCompany.GetValueOrDefault(order.CompanyId) ?? [];
        var bestSellPrice = executableSells.Count == 0 ? null : (decimal?)executableSells[0].Price;
        if (priorBuyPriorityLimitByCompany.TryGetValue(order.CompanyId, out var priorPriorityLimit)
            && order.LimitPrice > priorPriorityLimit)
        {
            return $"Buy limit price would violate earlier demand priority above ${priorPriorityLimit:F2}.";
        }

        var exposure = automatedBuyPolicy.AssessExposure(
            context.Participant.RiskProfile,
            netWorth,
            context.HoldingsValue);
        if (exposure?.Position == AutomatedExposurePosition.Below
            && bestSellPrice is decimal bestAsk
            && order.LimitPrice < bestAsk)
        {
            return $"Below-target buy must cross the best executable sell at ${bestAsk:F2}.";
        }

        var executableQuantity = executableSells
            .Where(candidate => candidate.Price <= order.LimitPrice)
            .Sum(candidate => candidate.RemainingQuantity);
        var envelope = automatedBuyPolicy.BuildBuyEnvelope(new AutomatedBuyOrderInput(
            context.Participant.RiskProfile,
            netWorth,
            context.HoldingsValue,
            context.Participant.ReservedBalance,
            availableCash,
            buyingPower,
            context.MarginLiability,
            order.LimitPrice,
            company.IssuedSharesCount,
            (int)Math.Min(executableQuantity, int.MaxValue)));
        if (envelope is null)
        {
            return "Buy order exceeds the automated exposure, reserved-order, cash, or margin limits.";
        }

        if (order.Quantity < envelope.MinimumQuantity)
        {
            return $"Buy quantity must be at least {envelope.MinimumQuantity} for the current envelope.";
        }

        if (order.Quantity > envelope.MaximumQuantity)
        {
            return $"Buy quantity must be at most {envelope.MaximumQuantity} for the current envelope.";
        }

        return null;
    }

    private async Task<(
        IReadOnlyDictionary<int, List<ExecutableAskLevel>> LevelsByCompany,
        Dictionary<int, decimal> PriorBuyPriorityLimitByCompany)> BuildAiExecutableAskContextAsync(
        OrderBookContext context)
    {
        var openSells = await dbContext.Orders.AsNoTracking()
            .Where(order => order.Type == OrderType.Sell
                && order.ParticipantId != context.Participant.Id
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .Select(order => new
            {
                order.CompanyId,
                order.LimitPrice,
                RemainingQuantity = order.Quantity - order.FilledQuantity,
            })
            .ToListAsync();

        var levelsByCompany = openSells
            .Where(order => order.RemainingQuantity > 0
                && context.BoundsByCompany.TryGetValue(order.CompanyId, out var bounds)
                && bounds.IsWithinActiveBand(order.LimitPrice))
            .GroupBy(order => order.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(order => order.LimitPrice)
                    .OrderBy(level => level.Key)
                    .Select(level => new ExecutableAskLevel(
                        level.Key,
                        level.Sum(order => (long)order.RemainingQuantity)))
                    .ToList());

        var openBuys = await dbContext.Orders.AsNoTracking()
            .Where(order => order.Type == OrderType.Buy
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

        var priorityLimitByCompany = new Dictionary<int, decimal>();
        foreach (var buy in openBuys
                     .Where(order => order.RemainingQuantity > 0
                         && context.BoundsByCompany.TryGetValue(order.CompanyId, out var bounds)
                         && bounds.IsWithinActiveBand(order.LimitPrice))
                     .OrderByDescending(order => order.LimitPrice)
                     .ThenBy(order => order.CreatedAt)
                     .ThenBy(order => order.Id))
        {
            var consumedQuantity = ConsumeExecutableAskLevels(
                levelsByCompany,
                buy.CompanyId,
                buy.LimitPrice,
                buy.RemainingQuantity);
            if (consumedQuantity > 0)
            {
                UpdatePriorBuyPriorityLimit(priorityLimitByCompany, buy.CompanyId, buy.LimitPrice);
            }
        }

        return (levelsByCompany, priorityLimitByCompany);
    }

    public Task<AdvanceCycleResult> AdvanceCycleAsync() =>
        WithLockAsync(() => InTransactionAsync(AdvanceCycleEntryCoreAsync));

    public Task<RunDecisionsResult> GenerateDecisionsAsync() => WithLockAsync(GenerateDecisionsCoreAsync);

    // Single automatic step used by the background loop: decide then match under one lock. Skips unless the
    // market is explicitly running.
    public Task<CycleTickResult> RunCycleTickAsync() => WithLockAsync(RunCycleTickCoreAsync);

    public Task<Market> SeedDemoMarketAsync() => WithLockAsync(SeedDemoMarketCoreAsync);

    public Task<Market> ResetDemoMarketAsync() => WithLockAsync(ResetDemoMarketCoreAsync);

    public Task<CreatePlayerResult> CreatePlayerAsync(string? name) =>
        WithLockAsync(() => CreatePlayerCoreAsync(name));

    public Task<CancelOrderResult> CancelPlayerOrderAsync(int orderId) =>
        WithLockAsync(() => CancelPlayerOrderCoreAsync(orderId));

    public Task<PlayerFundResult> OpenPlayerFundAsync(decimal seedAmount, string? name) =>
        WithLockAsync(() => OpenPlayerFundCoreAsync(seedAmount, name));

    public Task<PlayerFundResult> DepositToPlayerFundAsync(decimal amount) =>
        WithLockAsync(() => DepositToPlayerFundCoreAsync(amount));

    public Task<PlayerFundResult> WithdrawFromPlayerFundAsync(decimal amount) =>
        WithLockAsync(() => WithdrawFromPlayerFundCoreAsync(amount));

    public Task<PlayerFundResult> ClosePlayerFundAsync() =>
        WithLockAsync(ClosePlayerFundCoreAsync);

    public Task<FundAdvertiseQuoteResult> GetFundAdvertiseQuoteAsync(int fundParticipantId) =>
        WithLockAsync(() => GetFundAdvertiseQuoteCoreAsync(fundParticipantId));

    public Task<PlayerFundResult> AdvertiseFundAsync(int fundParticipantId) =>
        WithLockAsync(() => AdvertiseFundCoreAsync(fundParticipantId));

    // Manual loan repayment goes through the cycle lock so it cannot race a running tick's writes.
    public Task<RepayLoanResult> RepayLoanAsync(int loanId, decimal? amount) => WithLockAsync(async () =>
    {
        if (loanService is null)
        {
            return RepayLoanResult.Fail("Loans are not enabled.");
        }

        var market = await dbContext.Markets.FirstOrDefaultAsync();
        var currentCycleId = market?.CurrentCycleId ?? 0;
        return await loanService.RepayLoanAsync(loanId, amount, currentCycleId, DateTime.UtcNow);
    });

    public Task<Market?> SetStatusAsync(MarketStatus status) => WithLockAsync(async () =>
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market is null)
        {
            return null;
        }

        market.Status = status;
        market.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
        return market;
    });

    // Goes through the cycle lock so a profile edit cannot race a running tick's SQLite writes.
    public Task<Participant?> UpdateParticipantProfileAsync(int participantId, Temperament temperament, RiskProfile riskProfile) =>
        WithLockAsync(async () =>
        {
            var participant = await dbContext.Participants.FirstOrDefaultAsync(candidate => candidate.Id == participantId);
            if (participant is null)
            {
                return null;
            }

            participant.Temperament = temperament;
            participant.RiskProfile = riskProfile;
            await dbContext.SaveChangesAsync();
            return participant;
        });

    private async Task<CycleTickResult> RunCycleTickCoreAsync()
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market is null || market.Status != MarketStatus.Running || market.CurrentCycleId is null)
        {
            return CycleTickResult.Skipped();
        }

        if (tradingClockService is not null && !await tradingClockService.IsTradingAsync(market))
        {
            return await InTransactionAsync(() => AdvanceBreakCoreAsync(market));
        }

        return await DecideAndAdvanceCoreAsync();
    }

    private async Task<CycleTickResult> AdvanceBreakCoreAsync(Market market)
    {
        var completedCycleNumber = market.CurrentCycleId is int cycleId
            ? await dbContext.MarketCycles
                .Where(cycle => cycle.Id == cycleId)
                .Select(cycle => (int?)cycle.CycleNumber)
                .FirstOrDefaultAsync()
            : null;
        await tradingClockService!.AdvanceBreakAsync(market, DateTime.UtcNow);
        return CycleTickResult.Executed(0, 0, completedCycleNumber);
    }

    private async Task<AdvanceCycleResult> AdvanceCycleEntryCoreAsync()
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market?.CurrentCycleId is int cycleId
            && tradingClockService is not null
            && !await tradingClockService.IsTradingAsync(market))
        {
            var completedCycleNumber = await dbContext.MarketCycles
                .Where(cycle => cycle.Id == cycleId)
                .Select(cycle => cycle.CycleNumber)
                .FirstAsync();
            await tradingClockService.AdvanceBreakAsync(market, DateTime.UtcNow);
            return AdvanceCycleResult.Ok(completedCycleNumber, 0);
        }

        await SettleOpeningCycleCoreAsync();
        await ProcessOpeningDayMarginAsync();
        return await AdvanceCycleCoreAsync();
    }

    private async Task<CycleTickResult> DecideAndAdvanceCoreAsync()
    {
        return await InTransactionAsync(async () =>
        {
            await SettleOpeningCycleCoreAsync();
            await ProcessOpeningDayMarginAsync();
            await MaintainOrdersCoreAsync();
            var decisions = await GenerateDecisionsCoreAsync();
            var advance = await AdvanceCycleCoreAsync();

            return CycleTickResult.Executed(decisions.OrdersPlaced, advance.FillCount, advance.CompletedCycleNumber);
        });
    }

    private async Task SettleOpeningCycleCoreAsync()
    {
        if (settlementService is null)
        {
            return;
        }

        var current = await (
                from market in dbContext.Markets
                join cycle in dbContext.MarketCycles on market.CurrentCycleId equals cycle.Id
                join day in dbContext.TradingDays on cycle.TradingDayId equals day.Id
                select new { Cycle = cycle, day.DayNumber })
            .FirstOrDefaultAsync();
        if (current is null || current.Cycle.TradingCycleNumber != 1)
        {
            return;
        }

        await settlementService.SettleDueAsync(current.DayNumber, current.Cycle.Id, DateTime.UtcNow);
    }

    private async Task ProcessOpeningDayMarginAsync()
    {
        if (marginService is null)
        {
            return;
        }

        var current = await (
                from market in dbContext.Markets
                join cycle in dbContext.MarketCycles on market.CurrentCycleId equals cycle.Id
                select cycle)
            .FirstOrDefaultAsync();
        if (current is null || current.TradingDayId <= 0 || current.TradingCycleNumber != 1)
        {
            return;
        }

        await marginService.ProcessForTradingDayAsync(current.TradingDayId, current.Id, DateTime.UtcNow);
    }

    // Ages the resting participant order book before new decisions: expired orders are cancelled, stale
    // ones may be re-priced toward the market, and holders starved of cash for too long liquidate. The
    // company float (null participant) is never touched so the initial supply does not expire.
    private async Task MaintainOrdersCoreAsync()
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market?.CurrentCycleId is not int currentCycleId)
        {
            return;
        }

        var cycleNumbersById = await dbContext.MarketCycles
            .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);
        var currentCycleNumber = cycleNumbersById.GetValueOrDefault(currentCycleId);

        // Resolved once here so bankruptcy and auditors both read the same window; a crisis triggered a prior
        // cycle is what governs this cycle's harsher behaviour and the timeline its events attach to.
        var activeCrisis = crisisService is not null
            ? await crisisService.GetActiveCrisisAsync(currentCycleNumber)
            : null;

        // Sector confidence is revised before new decisions so this cycle's market behavior reads one settled mood state.
        if (industrySentimentService is not null)
        {
            await industrySentimentService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, activeCrisis);
        }

        var openOrders = await dbContext.Orders
            .Where(order => (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
                && order.ParticipantId != null)
            .ToListAsync();

        var participantsById = await dbContext.Participants.ToDictionaryAsync(participant => participant.Id);
        var bandByCompany = await dbContext.PriceBandStates.ToDictionaryAsync(state => state.CompanyId);
        var luldAffectedCompanyIds = bandByCompany.Values
            .Where(state => state.State != LuldState.Normal)
            .Select(state => state.CompanyId)
            .ToHashSet();
        var latestPriceByCompany = await LatestPriceByCompanyAsync();

        foreach (var order in openOrders)
        {
            if (luldAffectedCompanyIds.Contains(order.CompanyId))
            {
                continue;
            }

            var participant = participantsById[order.ParticipantId!.Value];
            var bounds = BuildOrderPriceBounds(
                bandByCompany.GetValueOrDefault(order.CompanyId),
                latestPriceByCompany.GetValueOrDefault(order.CompanyId));

            // Universal validity check: any participant order — the player's, a forced service's — resting beyond
            // the current allowed range is cancelled and its reservation released; a forced service relists inside
            // the band on its own next pass.
            if (bounds is not null && !bounds.IsWithinAllowedRange(order.LimitPrice))
            {
                CancelOrder(order, participant, currentCycleId);
                continue;
            }

            if (order.RelatedLoanId is not null || order.RelatedMarginCallId is not null)
            {
                continue;
            }

            // The bankruptcy service owns the lifecycle of a bankrupt trader's forced-sale orders, so generic
            // ageing must not cancel or reprice them out from under it.
            if (participant.IsBankrupt)
            {
                continue;
            }

            // The player is a human; the market never ages, reprices, or expires their orders — they cancel manually.
            if (participant.Type == ParticipantType.Player)
            {
                continue;
            }

            var age = currentCycleNumber - cycleNumbersById.GetValueOrDefault(order.CreatedInCycleId);

            if (age >= OrderMaxAgeCycles)
            {
                CancelOrder(order, participant, currentCycleId);
            }
            else if (participant.Type != ParticipantType.AIAgent
                && bounds is not null
                && random.NextDouble() < RepriceChance(age))
            {
                Reprice(order, participant, bounds, currentCycleId);
            }
        }

        // Persist cancellations first so freed shares and cash are visible to the liquidation pass.
        await dbContext.SaveChangesAsync();

        // A volatility halt runs first, reading only prior-cycle closes — before this cycle's splits, emissions,
        // lifecycle cut, or auditor downgrade add a snapshot, so a same-cycle deliberate price cut cannot trip
        // the down-halt. A company that moved past its band over the recent window is frozen and participant orders
        // are cancelled, while issuer float rests until matching and decisions can resume.
        if (volatilityHaltService is not null)
        {
            await volatilityHaltService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow);
            await dbContext.SaveChangesAsync();
        }

        // Splits re-denominate prices and share counts before any worth-reading service runs, so bankruptcy,
        // funds, and exits all see the post-split state; saved at once since those services query the database.
        if (stockSplitService is not null)
        {
            await stockSplitService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow);
            await dbContext.SaveChangesAsync();
        }

        // Another supply-side corporate action right after splits: very large companies may issue free shares,
        // diluting price before the worth-reading services and this cycle's matching run.
        if (shareEmissionService is not null)
        {
            await shareEmissionService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow);
            await dbContext.SaveChangesAsync();
        }

        // Priced issuance runs after free grants so scarcity is measured from the post-emission supply, while
        // the new issuer order still reaches lifecycle checks, decisions, and matching in this cycle.
        if (primaryIssuanceService is not null)
        {
            await primaryIssuanceService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow);
            await dbContext.SaveChangesAsync();
        }

        // Closes at most one failing company and may list a new one. Runs in the same pre-match window so a
        // delisted company's cancelled orders and wiped holdings, and a new listing's float, all reach the
        // worth-reading services and this cycle's matching.
        if (companyLifecycleService is not null)
        {
            await companyLifecycleService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow, activeCrisis);
            await dbContext.SaveChangesAsync();
        }

        await LiquidateStarvedHoldersAsync(participantsById);

        // Charges this cycle's loan payments (and lists distress sells) before bankruptcy so its debt-percent
        // and this cycle's matching both read post-payment loan state.
        if (loanService is not null)
        {
            await loanService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow);
            await dbContext.SaveChangesAsync();
        }

        // Runs before this cycle's matching so a wealthy trader's collapse, and a bankrupt trader's cheaper
        // re-listing, both reach the order book in time to cross the same cycle.
        if (bankruptcyService is not null)
        {
            await bankruptcyService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow, activeCrisis);
        }

        // Runs in the same pre-match window so a fund's forced sales, and a new member's freed-up cash, reach
        // the order book in time to cross this cycle.
        if (collectiveFundService is not null)
        {
            await collectiveFundService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow, activeCrisis);
        }

        // Saved before exit processing so a fund that closed this cycle has persisted its payouts, membership
        // deletes, and loss flags; the exit rolls then read a settled database.
        await dbContext.SaveChangesAsync();

        // Departures and their replacements land before this cycle's decision pass, so no ghost bids survive
        // matching and a replacement can trade the same tick.
        if (marketExitService is not null)
        {
            await marketExitService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow, activeCrisis);
            await dbContext.SaveChangesAsync();
        }

        // Runs right before the auditor, after bankruptcy/funds/exits have read pre-cut prices: any company that
        // has grown into an outsized share of total market capitalisation has its price cut, taking effect for
        // this cycle's decisions and matching.
        if (concentrationCapService is not null)
        {
            await concentrationCapService.ProcessForCycleAsync(currentCycleId, DateTime.UtcNow);
            await dbContext.SaveChangesAsync();
        }

        // Auditors run last so bankruptcy, funds, and exits read pre-audit prices; this cycle's decisions and
        // matching then react to the fresh ratings, any price correction, and the bids that were pulled.
        if (auditorService is not null)
        {
            await auditorService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow, activeCrisis);
            await dbContext.SaveChangesAsync();
        }
    }

    private void CancelOrder(Order order, Participant participant, int currentCycleId)
        => OrderCancellation.Cancel(dbContext, order, participant, currentCycleId);

    // A stale non-AI order is chased one RepriceStep toward the market and clamped into the active band. AI orders
    // bypass this path because their submitted limit remains authoritative for the order's lifetime.
    private void Reprice(Order order, Participant participant, OrderPriceBounds bounds, int currentCycleId)
    {
        var now = DateTime.UtcNow;

        var stepDown = order.Type == OrderType.Sell
            ? order.LimitPrice >= bounds.ActiveLowerPrice
            : order.LimitPrice > bounds.ActiveUpperPrice;
        var stepped = Round(order.LimitPrice * (stepDown ? 1m - RepriceStep : 1m + RepriceStep));
        var newLimit = bounds.ClampToActiveBand(stepped);
        if (newLimit == order.LimitPrice)
        {
            return;
        }

        if (order.Type == OrderType.Sell)
        {
            order.LimitPrice = newLimit;
            order.UpdatedAt = now;
            return;
        }

        // A buy's reserved cash tracks its limit on the unfilled quantity: top up when the bid rises, release when
        // it falls back toward the band. A rise the trader cannot fund leaves the order as-is to expire at the cap;
        // a release always proceeds.
        var delta = (newLimit - order.LimitPrice) * order.RemainingQuantity;
        if (delta > 0m && delta > participant.AvailableBalance)
        {
            return;
        }

        participant.ReservedBalance += delta;
        order.ReservedCashAmount += delta;
        order.LimitPrice = newLimit;
        order.UpdatedAt = now;

        dbContext.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = participant.Id,
            Type = delta >= 0m ? MoneyTransactionType.Reserve : MoneyTransactionType.Release,
            Amount = Math.Abs(delta),
            RelatedOrderId = order.Id,
            Description = "Reserved cash adjusted on buy order reprice",
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });
    }

    private async Task LiquidateStarvedHoldersAsync(IReadOnlyDictionary<int, Participant> participantsById)
    {
        var latestPriceByCompany = await LatestPriceByCompanyAsync();
        if (latestPriceByCompany.Count == 0)
        {
            return;
        }

        var cheapestShare = latestPriceByCompany.Values.Min();

        // Each seller's shares already committed to open sell orders, per company, so they are not
        // listed twice.
        var listedByOwnerCompany = (await dbContext.Orders
                .Where(order => order.Type == OrderType.Sell
                    && order.ParticipantId != null
                    && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
                .Select(order => new { ParticipantId = order.ParticipantId!.Value, order.CompanyId, Remaining = order.Quantity - order.FilledQuantity })
                .ToListAsync())
            .GroupBy(order => (order.ParticipantId, order.CompanyId))
            .ToDictionary(group => group.Key, group => group.Sum(order => order.Remaining));

        // Uncommitted holdings (quantity minus what is already listed), grouped by owner then company.
        var availableHoldings = (await dbContext.Holdings
                .Where(holding => holding.Quantity > 0)
                .Select(holding => new { holding.ParticipantId, holding.CompanyId, holding.Quantity })
                .ToListAsync())
            .GroupBy(holding => holding.ParticipantId)
            .ToDictionary(
                ownerGroup => ownerGroup.Key,
                ownerGroup => ownerGroup
                    .Select(holding => new
                    {
                        holding.CompanyId,
                        Available = holding.Quantity - listedByOwnerCompany.GetValueOrDefault((holding.ParticipantId, holding.CompanyId)),
                    })
                    .Where(holding => holding.Available > 0)
                    .ToDictionary(holding => holding.CompanyId, holding => holding.Available));

        var traders = participantsById.Values
            .Where(participant => participant.IsActive
                && (participant.Type == ParticipantType.Individual || participant.Type == ParticipantType.AIAgent));

        foreach (var trader in traders)
        {
            if (trader.AvailableBalance >= cheapestShare)
            {
                trader.CashStarvedCycles = 0;
                trader.CannotBuyCycles = 0;
                continue;
            }

            trader.CashStarvedCycles++;
            trader.CannotBuyCycles++;
            if (trader.CashStarvedCycles < CashStarvedLimitCycles)
            {
                continue;
            }

            trader.CashStarvedCycles = 0;

            if (!availableHoldings.TryGetValue(trader.Id, out var holdings) || holdings.Count == 0)
            {
                continue;
            }

            // Raise cash from the priciest holding by listing half of it.
            var target = holdings
                .OrderByDescending(holding => latestPriceByCompany.GetValueOrDefault(holding.Key))
                .First();
            var quantity = target.Value / 2;
            if (quantity < 1)
            {
                continue;
            }

            var sellLimit = Round(latestPriceByCompany[target.Key] * (1m - RandomSellOffset()));
            if (sellLimit <= 0m)
            {
                continue;
            }

            await PlaceOrderCoreAsync(trader.Id, target.Key, OrderType.Sell, quantity, sellLimit);
        }
    }

    // Pays a dividend to every share owner once the schedule comes due, then sets the next due cycle. A
    // non-positive schedule means "not yet scheduled" (a fresh or migrated market), so the first advance
    // only arms it rather than paying immediately.
    private async Task PayDividendsIfDueAsync(Market market, MarketCycle currentCycle, DateTime now)
    {
        if (market.NextDividendCycleNumber <= 0)
        {
            market.NextDividendCycleNumber = currentCycle.CycleNumber + RandomDividendInterval(random);
            return;
        }

        if (currentCycle.CycleNumber < market.NextDividendCycleNumber)
        {
            return;
        }

        market.NextDividendCycleNumber = currentCycle.CycleNumber + RandomDividendInterval(random);
        await PayDividendsAsync(currentCycle.Id, now);
    }

    // Draw discipline for a scripted Random: every priced company draws its dividend roll/rate in ascending Id
    // order before any company draws its independent income roll/rate in the same order.
    private async Task PayDividendsAsync(int currentCycleId, DateTime now)
    {
        var latestPriceByCompany = await LatestPriceByCompanyAsync();
        if (latestPriceByCompany.Count == 0)
        {
            return;
        }

        var pricedCompanyIds = latestPriceByCompany.Keys.ToList();
        var companies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null && pricedCompanyIds.Contains(company.Id))
            .OrderBy(company => company.Id)
            .ToListAsync();

        var capitalizationByCompany = companies.ToDictionary(
            company => company.Id,
            company => latestPriceByCompany[company.Id] * company.IssuedSharesCount);
        var dividendRateByCompany = new Dictionary<int, decimal>();
        foreach (var company in companies)
        {
            var capitalization = capitalizationByCompany[company.Id];
            if (RollCorporateCashEvent(company.LastDividendCapitalization, capitalization))
            {
                dividendRateByCompany[company.Id] = RandomCorporateCashRate();
            }
        }

        foreach (var company in companies)
        {
            var capitalization = capitalizationByCompany[company.Id];
            if (RollCorporateCashEvent(company.LastDividendCapitalization, capitalization))
            {
                var income = Math.Min(
                    Round(capitalization * RandomCorporateCashRate()),
                    MaxCompanyCashPerWindow);
                if (income > 0m)
                {
                    company.CashBalance += income;
                    dbContext.CorporateCashTransactions.Add(new CorporateCashTransaction
                    {
                        CompanyId = company.Id,
                        Type = CorporateCashTransactionType.OperatingIncome,
                        Amount = income,
                        CreatedInCycleId = currentCycleId,
                        CreatedAt = now,
                    });
                }
            }

            company.LastDividendCapitalization = capitalization;
        }

        if (dividendRateByCompany.Count == 0)
        {
            return;
        }

        var payingCompanyIds = dividendRateByCompany.Keys.ToList();
        var holdings = await dbContext.Holdings
            .Where(holding => holding.Quantity > 0 && payingCompanyIds.Contains(holding.CompanyId))
            .Select(holding => new { holding.ParticipantId, holding.CompanyId, holding.Quantity })
            .ToListAsync();
        var participantsById = await dbContext.Participants.ToDictionaryAsync(participant => participant.Id);
        var companyById = companies.ToDictionary(company => company.Id);
        var allocations = new List<(int ParticipantId, int CompanyId, decimal Amount)>();

        // Allocate each funded company pool in whole cents before grouping by owner. Flooring every claim and
        // distributing residual cents by largest remainder makes company debits exactly equal shareholder credits.
        foreach (var companyGroup in holdings
            .Where(holding => participantsById.ContainsKey(holding.ParticipantId))
            .GroupBy(holding => holding.CompanyId)
            .OrderBy(group => group.Key))
        {
            var company = companyById[companyGroup.Key];
            var claims = companyGroup
                .Select(holding => new
                {
                    holding.ParticipantId,
                    RawAmount = latestPriceByCompany[company.Id] * dividendRateByCompany[company.Id] * holding.Quantity,
                })
                .Where(claim => claim.RawAmount > 0m)
                .OrderBy(claim => claim.ParticipantId)
                .ToList();
            var uncappedPool = claims.Sum(claim => claim.RawAmount);
            var declaredPool = Math.Min(Round(uncappedPool), MaxCompanyCashPerWindow);
            if (declaredPool <= 0m)
            {
                continue;
            }

            var fundedPool = Math.Min(declaredPool, company.CashBalance);
            if (fundedPool < declaredPool)
            {
                var skipped = fundedPool <= 0m;
                dbContext.NewsPosts.Add(new NewsPost
                {
                    Title = skipped
                        ? $"{company.Name} dividend skipped"
                        : $"{company.Name} dividend reduced",
                    Content = skipped
                        ? $"{company.Name} could not fund its declared dividend from issuer cash, so no payout was made."
                        : $"{company.Name} reduced its dividend from ${declaredPool:N2} to ${fundedPool:N2} to stay within available issuer cash.",
                    PublishedInCycleId = currentCycleId,
                    PublishedAt = now,
                    Scope = NewsImpactScope.None,
                    Category = NewsCategory.General,
                    TargetCompanyId = company.Id,
                });
            }

            if (fundedPool <= 0m)
            {
                continue;
            }

            var fundedClaims = claims
                .Select(claim =>
                {
                    var exactAmount = claim.RawAmount * fundedPool / uncappedPool;
                    var floorAmount = Math.Floor(exactAmount * 100m) / 100m;
                    return new
                    {
                        claim.ParticipantId,
                        ExactAmount = exactAmount,
                        FloorAmount = floorAmount,
                    };
                })
                .ToList();
            var residualCents = decimal.ToInt32((fundedPool - fundedClaims.Sum(claim => claim.FloorAmount)) * 100m);
            var residualRecipients = fundedClaims
                .OrderByDescending(claim => claim.ExactAmount - claim.FloorAmount)
                .ThenBy(claim => claim.ParticipantId)
                .Take(residualCents)
                .Select(claim => claim.ParticipantId)
                .ToHashSet();

            foreach (var claim in fundedClaims)
            {
                var amount = claim.FloorAmount + (residualRecipients.Contains(claim.ParticipantId) ? 0.01m : 0m);
                if (amount > 0m)
                {
                    allocations.Add((claim.ParticipantId, company.Id, amount));
                }
            }

            company.CashBalance -= fundedPool;
            dbContext.CorporateCashTransactions.Add(new CorporateCashTransaction
            {
                CompanyId = company.Id,
                Type = CorporateCashTransactionType.DividendDeclared,
                Amount = fundedPool,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
            });
        }

        foreach (var ownerGroup in allocations.GroupBy(allocation => allocation.ParticipantId))
        {
            if (!participantsById.TryGetValue(ownerGroup.Key, out var owner))
            {
                continue;
            }

            var payout = ownerGroup.Sum(allocation => allocation.Amount);

            owner.CurrentBalance += payout;
            owner.SettledCashBalance += payout;
            var transaction = new MoneyTransaction
            {
                ParticipantId = owner.Id,
                Type = MoneyTransactionType.Dividend,
                Amount = payout,
                Description = "Dividend from share holdings",
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
            };
            dbContext.MoneyTransactions.Add(transaction);

            foreach (var line in ownerGroup)
            {
                dbContext.DividendPayouts.Add(new DividendPayout
                {
                    MoneyTransaction = transaction,
                    CompanyId = line.CompanyId,
                    Amount = line.Amount,
                    CreatedInCycleId = currentCycleId,
                    CreatedAt = now,
                });
            }

            // A fund passes part of its own dividend straight through to its members, split by their deposit.
            if (owner.Type == ParticipantType.CollectiveFund && collectiveFundService is not null)
            {
                await collectiveFundService.DistributeDividendToMembersAsync(owner, payout, currentCycleId, now);
            }
        }
    }

    private async Task<IReadOnlyDictionary<int, decimal>> LatestPriceByCompanyAsync() =>
        await PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);

    private OrderPriceBounds? BuildOrderPriceBounds(PriceBandState? band, decimal latestPrice) =>
        OrderPriceBounds.Resolve(
            band,
            latestPrice,
            volatilityHaltOptionValues.LowerBandPercent,
            volatilityHaltOptionValues.UpperBandPercent,
            volatilityHaltOptionValues.AllowedOrderLowerPercent,
            volatilityHaltOptionValues.AllowedOrderUpperPercent);

    // Single-company resolution for the interactive order path; the newest snapshot is the fallback reference
    // when a company has no persisted band yet.
    private async Task<OrderPriceBounds?> ResolveOrderPriceBoundsAsync(
        int companyId,
        IReadOnlyDictionary<int, decimal>? priceByCompany)
    {
        var band = await dbContext.PriceBandStates.FirstOrDefaultAsync(state => state.CompanyId == companyId);
        var latestPrice = priceByCompany is not null && priceByCompany.TryGetValue(companyId, out var known)
            ? known
            : await dbContext.PriceSnapshots
                .Where(snapshot => snapshot.CompanyId == companyId)
                .OrderByDescending(snapshot => snapshot.Id)
                .Select(snapshot => snapshot.Price)
                .FirstOrDefaultAsync();
        return BuildOrderPriceBounds(band, latestPrice);
    }

    // One band lookup per pass covers the whole automated decision batch, so no per-trader or per-order price-band
    // query runs during it.
    private async Task<Dictionary<int, OrderPriceBounds>> ResolveOrderPriceBoundsByCompanyAsync(
        IReadOnlyDictionary<int, decimal> priceByCompany)
    {
        var bandByCompany = await dbContext.PriceBandStates.ToDictionaryAsync(state => state.CompanyId);
        var bounds = new Dictionary<int, OrderPriceBounds>();
        foreach (var (companyId, price) in priceByCompany)
        {
            if (BuildOrderPriceBounds(bandByCompany.GetValueOrDefault(companyId), price) is { } resolved)
            {
                bounds[companyId] = resolved;
            }
        }

        return bounds;
    }

    private static int RandomDividendInterval(Random rng) =>
        rng.Next(MinDividendIntervalCycles, MaxDividendIntervalCycles + 1);

    private decimal RandomCorporateCashRate()
    {
        var bands = chanceRateValues.RandomMagnitudeBands;
        return bands.DividendRateMin + ((decimal)random.NextDouble() * (bands.DividendRateMax - bands.DividendRateMin));
    }

    // Each independent cash event is more likely while capitalisation stays within the stability band. The
    // shared previous-window baseline is intentionally unchanged until both event decisions are complete.
    private bool RollCorporateCashEvent(decimal? baselineCapitalization, decimal capitalization)
    {
        var stable = baselineCapitalization is not decimal baseline
            || baseline <= 0m
            || Math.Abs(capitalization - baseline) / baseline <= CapitalizationStabilityThreshold;
        var triggers = chanceRateValues.EventTriggerChances;
        var chance = stable ? triggers.DividendStableCapitalization : triggers.DividendVolatileCapitalization;
        return (decimal)random.NextDouble() < chance;
    }

    // Reprice probability climbs as the order ages: modest while fresh, then near-certain near the cap.
    private static double RepriceChance(int age) => age switch
    {
        >= 13 => 1.0,
        >= 7 => 0.7,
        >= OrderRepriceFromAge => 0.5,
        _ => 0.0,
    };

    private decimal RandomSellOffset() =>
        MinSellOffset + ((decimal)random.NextDouble() * (MaxSellOffset - MinSellOffset));

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private async Task<PlaceOrderResult> PlaceOrderCoreAsync(
        int participantId,
        int companyId,
        OrderType type,
        int quantity,
        decimal limitPrice,
        IReadOnlyDictionary<int, decimal>? priceByCompany = null,
        bool deferSave = false,
        IReadOnlyDictionary<int, OrderPriceBounds>? boundsByCompany = null)
    {
        if (quantity <= 0)
        {
            return PlaceOrderResult.Fail("Quantity must be greater than zero.");
        }

        if (limitPrice <= 0)
        {
            return PlaceOrderResult.Fail("Limit price must be greater than zero.");
        }

        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market?.CurrentCycleId is not int cycleId)
        {
            return PlaceOrderResult.Fail("Market is not running.");
        }

        if (tradingClockService is not null && !await tradingClockService.IsTradingAsync(market))
        {
            return PlaceOrderResult.Fail("New orders are unavailable during the trading break.");
        }

        var participant = await dbContext.Participants.FirstOrDefaultAsync(candidate => candidate.Id == participantId);
        if (participant is null)
        {
            return PlaceOrderResult.Fail("Participant not found.");
        }

        if (!participant.IsActive)
        {
            return PlaceOrderResult.Fail("Participant is not active.");
        }

        var company = await dbContext.Companies.FirstOrDefaultAsync(candidate => candidate.Id == companyId);
        if (company is null)
        {
            return PlaceOrderResult.Fail("Company not found.");
        }

        if (company.ClosedInCycleId is not null)
        {
            return PlaceOrderResult.Fail("This company is delisted.");
        }

        var oppositeType = type == OrderType.Buy ? OrderType.Sell : OrderType.Buy;
        var hasOppositeOrder = await dbContext.Orders.AnyAsync(existing =>
            existing.ParticipantId == participantId
            && existing.CompanyId == companyId
            && existing.Type == oppositeType
            && (existing.Status == OrderStatus.Open || existing.Status == OrderStatus.PartiallyFilled));
        if (hasOppositeOrder)
        {
            var side = oppositeType == OrderType.Sell ? "sell" : "buy";
            return PlaceOrderResult.Fail($"Cancel your open {side} order before placing a {type.ToString().ToLowerInvariant()}.");
        }

        if (!deferSave)
        {
            var luldState = await dbContext.PriceBandStates
                .Where(state => state.CompanyId == companyId)
                .Select(state => (LuldState?)state.State)
                .FirstOrDefaultAsync();
            if (luldState is not null and not LuldState.Normal)
            {
                return PlaceOrderResult.Fail($"Order entry is disabled while the security is in {luldState}.");
            }
        }

        // Every participant order — the player's, an automated trader's deferred write — must rest inside the
        // allowed range around the reference; matching still only crosses inside the narrower active band.
        var bounds = boundsByCompany is not null && boundsByCompany.TryGetValue(companyId, out var passedBounds)
            ? passedBounds
            : await ResolveOrderPriceBoundsAsync(companyId, priceByCompany);
        if (bounds is null)
        {
            return PlaceOrderResult.Fail("No reference price is available for this company yet.");
        }

        if (!bounds.IsWithinAllowedRange(limitPrice))
        {
            return PlaceOrderResult.Fail(
                $"Limit price must be between ${bounds.AllowedMinimumPrice:F2} and ${bounds.AllowedMaximumPrice:F2}. "
                + $"The current executable band is ${bounds.ActiveLowerPrice:F2}–${bounds.ActiveUpperPrice:F2}.");
        }

        if (marginService is not null && type == OrderType.Buy)
        {
            var buyingPower = await marginService.GetBuyingPowerAsync(participantId, priceByCompany);
            if (limitPrice * quantity > buyingPower)
            {
                return PlaceOrderResult.Fail("Insufficient margin buying power for the buy order.");
            }
        }

        var now = DateTime.UtcNow;
        var order = new Order
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Type = type,
            Status = OrderStatus.Open,
            Quantity = quantity,
            FilledQuantity = 0,
            LimitPrice = limitPrice,
            ReservedCashAmount = 0,
            CreatedInCycleId = cycleId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (type == OrderType.Buy)
        {
            var reserved = limitPrice * quantity;

            if (marginService is null && participant.AvailableBalance < reserved)
            {
                return PlaceOrderResult.Fail("Insufficient available cash to reserve for the buy order.");
            }

            participant.ReservedBalance += reserved;
            order.ReservedCashAmount = reserved;

            dbContext.Orders.Add(order);
            dbContext.MoneyTransactions.Add(new MoneyTransaction
            {
                ParticipantId = participantId,
                Type = MoneyTransactionType.Reserve,
                Amount = reserved,
                RelatedOrder = order,
                Description = "Cash reserved for new buy order",
                CreatedInCycleId = cycleId,
                CreatedAt = now,
            });
        }
        else
        {
            var owned = await dbContext.Holdings
                .Where(holding => holding.ParticipantId == participantId && holding.CompanyId == companyId)
                .Select(holding => holding.Quantity)
                .FirstOrDefaultAsync();

            // Shares already committed to this seller's other open sell orders for the company cannot be
            // offered again, so a new sell can only list what remains uncommitted.
            var alreadyListed = (await dbContext.Orders
                    .Where(existing => existing.ParticipantId == participantId
                        && existing.CompanyId == companyId
                        && existing.Type == OrderType.Sell
                        && (existing.Status == OrderStatus.Open || existing.Status == OrderStatus.PartiallyFilled))
                    .Select(existing => existing.Quantity - existing.FilledQuantity)
                    .ToListAsync())
                .Sum();

            if (owned - alreadyListed < quantity)
            {
                return PlaceOrderResult.Fail("Not enough available shares to sell.");
            }

            dbContext.Orders.Add(order);
        }

        // The decision pass stages many orders behind one save; a single-order caller saves immediately.
        if (!deferSave)
        {
            await dbContext.SaveChangesAsync();
        }

        return PlaceOrderResult.Ok(order);
    }

    private async Task<AdvanceCycleResult> AdvanceCycleCoreAsync()
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market?.CurrentCycleId is not int currentCycleId)
        {
            return AdvanceCycleResult.Fail("Market is not running.");
        }

        var currentCycle = await dbContext.MarketCycles.FirstOrDefaultAsync(cycle => cycle.Id == currentCycleId);
        if (currentCycle is null)
        {
            return AdvanceCycleResult.Fail("Current cycle not found.");
        }

        var now = DateTime.UtcNow;

        if (currentCycle.Status == CycleStatus.Planned)
        {
            currentCycle.Status = CycleStatus.Running;
            currentCycle.StartedAt ??= now;
        }

        var fillCount = await matchingEngine.RunAsync(currentCycle);

        currentCycle.Status = CycleStatus.Completed;
        currentCycle.CompletedAt = now;

        await PayDividendsIfDueAsync(market, currentCycle, now);

        // Read before this cycle's crisis roll so it reflects any crisis already running; while one is active,
        // price-lifting news and science land half as often.
        var duringCrisis = crisisService is not null
            && await crisisService.GetActiveCrisisAsync(currentCycle.CycleNumber) is not null;

        // Automated news is published on its cycle schedule and shares this transaction's save below.
        if (newsService is not null)
        {
            await newsService.MaybeAddAutomatedNewsForCycleAsync(currentCycle, now, duringCrisis);
        }

        // A crisis is rolled once per trading day, on its opening cycle; when it strikes it drives the hit
        // sectors down and cancels their stale bids.
        if (crisisService is not null && currentCycle.TradingCycleNumber == 1)
        {
            await crisisService.MaybeTriggerForCycleAsync(market, currentCycle, now);
        }

        // A science investigation runs on its own clock and may lift a few sectors the same cycle a crisis hits.
        if (scienceService is not null)
        {
            await scienceService.MaybeTriggerForCycleAsync(market, currentCycle, now, duringCrisis);
        }

        // Persist composed automated posts before querying the current-cycle apply pass, so the matching
        // outcome stays settled before headlines become the price signal for the next decision cycle.
        if (newsService is not null)
        {
            await dbContext.SaveChangesAsync();
            await newsService.ApplyPendingImpactsForCycleAsync(currentCycle, now);
        }

        MarketCycle? nextCycle;
        if (tradingClockService is not null && currentCycle.TradingDayId > 0)
        {
            nextCycle = await tradingClockService.CompleteTradingCycleAsync(market, currentCycle, now);
        }
        else
        {
            nextCycle = new MarketCycle
            {
                CycleNumber = currentCycle.CycleNumber + 1,
                Status = CycleStatus.Running,
                StartedAt = now,
            };
            dbContext.MarketCycles.Add(nextCycle);
            await dbContext.SaveChangesAsync();
        }

        // The trading day just closed (the clock returned no next cycle), so each fund pays its owner a share of
        // the day's collected fees before the day-end worth snapshot. The save flushes this cycle's dividend fees
        // so the sweep's fee total reads them back.
        if (nextCycle is null && currentCycle.TradingDayId > 0 && collectiveFundService is not null)
        {
            await dbContext.SaveChangesAsync();
            await collectiveFundService.PayManagerFeesForTradingDayAsync(currentCycle.TradingDayId, currentCycle.Id, now);
        }

        // Runs once this cycle's fills and shocks are persisted so each trader's holdings reflect final prices;
        // the added rows flush with the market update below, inside the advance transaction.
        await WriteWorthSnapshotsAsync(currentCycle.Id, now);

        // On its thirty-cycle cadence the behavioural audit reclassifies the player and its fund from recent
        // activity; its staged participant and news changes flush with the market update below.
        if (behaviorAuditService is not null)
        {
            await behaviorAuditService.ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber, now);
        }

        if (nextCycle is not null)
        {
            market.CurrentCycleId = nextCycle.Id;
        }
        market.UpdatedAt = now;
        await dbContext.SaveChangesAsync();

        // Move rows the running simulation no longer reads out of the live tables so the working set stays
        // small; runs in the advance transaction so a cycle either fully completes and archives or neither.
        await ArchiveAgedRowsAsync(currentCycle.CycleNumber);

        return AdvanceCycleResult.Ok(currentCycle.CycleNumber, fillCount);
    }

    // Bulk-moves price, money, worth, and sentiment snapshots older than the retention window from
    // their live tables to the matching archive tables. Nothing in the market references these rows once they
    // age past the window, so the move is a plain INSERT ... SELECT then DELETE keyed on the created-in cycle.
    private async Task ArchiveAgedRowsAsync(int currentCycleNumber)
    {
        var options = archiveOptions?.Value ?? new ArchiveOptions();
        if (!options.Enabled)
        {
            return;
        }

        var cutoffCycleNumber = currentCycleNumber - options.RetentionCycles;
        if (cutoffCycleNumber <= 0)
        {
            return;
        }

        // Cycle ids grow with cycle number, so the highest id at or before the cutoff number bounds the rows
        // to archive by their CreatedInCycleId without a join.
        var cutoffCycleId = await dbContext.MarketCycles
            .Where(cycle => cycle.CycleNumber <= cutoffCycleNumber)
            .MaxAsync(cycle => (int?)cycle.Id) ?? 0;
        if (cutoffCycleId <= 0)
        {
            return;
        }

        var currentPriceAnchorIds = await dbContext.PriceSnapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .Select(group => group.Max(snapshot => snapshot.Id))
            .ToListAsync();

        await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO PriceSnapshotArchives (Id, CompanyId, Price, Capitalization, SourceShareTransactionId, CreatedInCycleId, CreatedAt)
            SELECT Id, CompanyId, Price, Capitalization, SourceShareTransactionId, CreatedInCycleId, CreatedAt
            FROM PriceSnapshots
            WHERE CreatedInCycleId <= {cutoffCycleId}
              AND Id NOT IN (SELECT MAX(Id) FROM PriceSnapshots GROUP BY CompanyId)");
        await dbContext.PriceSnapshots
            .Where(row => row.CreatedInCycleId <= cutoffCycleId && !currentPriceAnchorIds.Contains(row.Id))
            .ExecuteDeleteAsync();

        await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO MoneyTransactionArchives (Id, ParticipantId, Type, Amount, RelatedOrderId, RelatedShareTransactionId, RelatedLoanId, FromWhomId, Description, CreatedInCycleId, CreatedAt)
            SELECT Id, ParticipantId, Type, Amount, RelatedOrderId, RelatedShareTransactionId, RelatedLoanId, FromWhomId, Description, CreatedInCycleId, CreatedAt
            FROM MoneyTransactions WHERE CreatedInCycleId <= {cutoffCycleId}");
        // The archive twin keeps no dividend line detail, so drop the breakdown with its parent rather than
        // leaving rows that reference an archived transaction.
        await dbContext.DividendPayouts.Where(row => row.CreatedInCycleId <= cutoffCycleId).ExecuteDeleteAsync();
        await dbContext.MoneyTransactions.Where(row => row.CreatedInCycleId <= cutoffCycleId).ExecuteDeleteAsync();

        await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ParticipantWorthSnapshotArchives (Id, ParticipantId, CreatedInCycleId, Balance, HoldingsValue, LoanLiability, MarginLiability, CreatedAt)
            SELECT Id, ParticipantId, CreatedInCycleId, Balance, HoldingsValue, LoanLiability, MarginLiability, CreatedAt
            FROM ParticipantWorthSnapshots WHERE CreatedInCycleId <= {cutoffCycleId}");
        await dbContext.ParticipantWorthSnapshots.Where(row => row.CreatedInCycleId <= cutoffCycleId).ExecuteDeleteAsync();

        await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO SectorSentimentSnapshotArchives (Id, IndustryId, SentimentValue, CreatedInCycleId, CreatedAt)
            SELECT Id, IndustryId, SentimentValue, CreatedInCycleId, CreatedAt
            FROM SectorSentimentSnapshots WHERE CreatedInCycleId <= {cutoffCycleId}");
        await dbContext.SectorSentimentSnapshots.Where(row => row.CreatedInCycleId <= cutoffCycleId).ExecuteDeleteAsync();
    }

    // Records each trader's cash and holdings value for the just-completed cycle so per-cycle change figures
    // and the total-worth chart can be derived later. Every participant is captured, not just the human player.
    private async Task WriteWorthSnapshotsAsync(int completedCycleId, DateTime now)
    {
        var industrySentiments = await dbContext.Industries
            .Select(industry => new { industry.Id, industry.SentimentValue })
            .ToListAsync();
        foreach (var industry in industrySentiments)
        {
            dbContext.SectorSentimentSnapshots.Add(new SectorSentimentSnapshot
            {
                IndustryId = industry.Id,
                SentimentValue = industry.SentimentValue,
                CreatedInCycleId = completedCycleId,
                CreatedAt = now,
            });
        }

        var participants = await dbContext.Participants.ToListAsync();
        if (participants.Count == 0)
        {
            return;
        }

        var latestPriceByCompany = await LatestPriceByCompanyAsync();

        var holdingsByOwner = (await dbContext.Holdings
                .Where(holding => holding.Quantity > 0)
                .Select(holding => new { holding.ParticipantId, holding.CompanyId, holding.Quantity })
                .ToListAsync())
            .GroupBy(holding => holding.ParticipantId)
            .ToDictionary(
                ownerGroup => ownerGroup.Key,
                ownerGroup => ownerGroup
                    .ToDictionary(holding => holding.CompanyId, holding => holding.Quantity));

        var loanLiabilityByParticipant = await LoanService.OpenLoanLiabilityByParticipantAsync(dbContext);
        var marginLiabilityByParticipant = await MarginService.LiabilityByParticipantAsync(dbContext);

        foreach (var participant in participants)
        {
            var holdingsValue = holdingsByOwner.TryGetValue(participant.Id, out var holdings)
                ? holdings.Sum(holding => holding.Value * latestPriceByCompany.GetValueOrDefault(holding.Key))
                : 0m;

            dbContext.ParticipantWorthSnapshots.Add(new ParticipantWorthSnapshot
            {
                ParticipantId = participant.Id,
                CreatedInCycleId = completedCycleId,
                Balance = participant.CurrentBalance,
                HoldingsValue = holdingsValue,
                LoanLiability = loanLiabilityByParticipant.GetValueOrDefault(participant.Id),
                MarginLiability = marginLiabilityByParticipant.GetValueOrDefault(participant.Id),
                CreatedAt = now,
            });
        }
    }

    private async Task<CreatePlayerResult> CreatePlayerCoreAsync(string? name)
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market is null)
        {
            return CreatePlayerResult.Fail("No market exists.");
        }

        if (await dbContext.Participants.AnyAsync(participant => participant.Type == ParticipantType.Player))
        {
            return CreatePlayerResult.Fail("A player already exists.");
        }

        var trimmed = name?.Trim();
        var balance = random.Next(PlayerMinBalance, PlayerMaxBalance + 1);

        var player = new Participant
        {
            Name = string.IsNullOrWhiteSpace(trimmed) ? "Player" : trimmed,
            Type = ParticipantType.Player,
            // A player does not use temperament or risk, but keeps the row well-formed like every other trader.
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = balance,
            CurrentBalance = balance,
            SettledCashBalance = balance,
            ReservedBalance = 0m,
            IsActive = true,
            // Recorded for consistency with churned-in traders, though the player never departs.
            JoinedInCycleId = market.CurrentCycleId ?? 0,
            MaxTotalWorth = balance,
        };

        dbContext.Participants.Add(player);
        await dbContext.SaveChangesAsync();
        return CreatePlayerResult.Ok(player);
    }

    private async Task<CancelOrderResult> CancelPlayerOrderCoreAsync(int orderId)
    {
        var player = await dbContext.Participants
            .FirstOrDefaultAsync(participant => participant.Type == ParticipantType.Player);
        if (player is null)
        {
            return CancelOrderResult.Fail("No player exists.");
        }

        var order = await dbContext.Orders
            .FirstOrDefaultAsync(candidate => candidate.Id == orderId);
        if (order is null)
        {
            return CancelOrderResult.Fail("Order does not belong to the player.");
        }

        // The player cancels its own orders and, since it hand-trades its managed fund, that fund's orders too.
        // Any other order is rejected. The reservation is released to the order's actual owner, not the player.
        var owner = await ResolveCancellableOwnerAsync(player, order);
        if (owner is null)
        {
            return CancelOrderResult.Fail("Order does not belong to the player.");
        }

        if (order.Status != OrderStatus.Open && order.Status != OrderStatus.PartiallyFilled)
        {
            return CancelOrderResult.Fail("Only an open order can be cancelled.");
        }

        // Release is stamped with the open cycle, matching the ageing cancel; fall back to the order's own cycle.
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        var currentCycleId = market?.CurrentCycleId ?? order.CreatedInCycleId;

        CancelOrder(order, owner, currentCycleId);
        await dbContext.SaveChangesAsync();
        return CancelOrderResult.Ok(order);
    }

    // The participant whose reservation a player-driven cancel releases: the player for its own order, or the
    // participant of the active fund the player manages for that fund's order. Null when the order is neither.
    private async Task<Participant?> ResolveCancellableOwnerAsync(Participant player, Order order)
    {
        if (order.ParticipantId == player.Id)
        {
            return player;
        }

        if (order.ParticipantId is not int ownerId)
        {
            return null;
        }

        var managesFund = await dbContext.CollectiveFunds
            .AnyAsync(fund => fund.IsPlayerManaged
                && fund.FoundedByParticipantId == player.Id
                && fund.Status != CollectiveFundStatus.Closed
                && fund.ParticipantId == ownerId);

        return managesFund
            ? await dbContext.Participants.FirstOrDefaultAsync(participant => participant.Id == ownerId)
            : null;
    }

    // The fund the human player runs by hand: a CollectiveFund founded by the player and flagged player-managed.
    // Distinct from an AI fund only in that the player trades it directly and it never auto-trades or auto-closes.
    private sealed record PlayerFundContext(Market Market, Participant Player, CollectiveFund Fund, Participant FundParticipant);

    private async Task<(PlayerFundContext? Context, string? Error)> ResolvePlayerFundAsync()
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market is null)
        {
            return (null, "No market exists.");
        }

        var player = await dbContext.Participants
            .FirstOrDefaultAsync(participant => participant.Type == ParticipantType.Player);
        if (player is null)
        {
            return (null, "No player exists.");
        }

        var fund = await dbContext.CollectiveFunds
            .FirstOrDefaultAsync(candidate => candidate.IsPlayerManaged
                && candidate.FoundedByParticipantId == player.Id
                && candidate.Status != CollectiveFundStatus.Closed);
        if (fund is null)
        {
            return (null, "The player does not manage a fund.");
        }

        var fundParticipant = await dbContext.Participants
            .FirstOrDefaultAsync(participant => participant.Id == fund.ParticipantId);
        if (fundParticipant is null)
        {
            return (null, "The fund is missing its participant.");
        }

        return (new PlayerFundContext(market, player, fund, fundParticipant), null);
    }

    private async Task<PlayerFundResult> OpenPlayerFundCoreAsync(decimal seedAmount, string? name)
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market is null)
        {
            return PlayerFundResult.Fail("No market exists.");
        }

        var player = await dbContext.Participants
            .FirstOrDefaultAsync(participant => participant.Type == ParticipantType.Player);
        if (player is null)
        {
            return PlayerFundResult.Fail("No player exists.");
        }

        // One managed fund per player, mirroring the single-player invariant.
        if (await dbContext.CollectiveFunds.AnyAsync(fund => fund.IsPlayerManaged
                && fund.FoundedByParticipantId == player.Id
                && fund.Status != CollectiveFundStatus.Closed))
        {
            return PlayerFundResult.Fail("The player already manages a fund.");
        }

        if (seedAmount <= 0m)
        {
            return PlayerFundResult.Fail("Seed amount must be positive.");
        }

        if (seedAmount > TransferableCash(player))
        {
            return PlayerFundResult.Fail("Seed amount exceeds the player's available balance.");
        }

        var currentCycleId = market.CurrentCycleId ?? 0;
        var now = DateTime.UtcNow;
        var trimmed = name?.Trim();

        var fundParticipant = new Participant
        {
            Name = string.IsNullOrWhiteSpace(trimmed) ? $"{player.Name}'s Fund" : trimmed,
            Type = ParticipantType.CollectiveFund,
            // A player-managed fund never trades on the engine, so temperament and risk are inert; snapshot the
            // player's for a well-formed row like every other participant.
            Temperament = player.Temperament,
            RiskProfile = player.RiskProfile,
            InitialBalance = 0m,
            CurrentBalance = 0m,
            SettledCashBalance = 0m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        dbContext.Participants.Add(fundParticipant);
        await dbContext.SaveChangesAsync();

        var fund = new CollectiveFund
        {
            ParticipantId = fundParticipant.Id,
            FoundedByParticipantId = player.Id,
            IsPlayerManaged = true,
            Status = CollectiveFundStatus.Active,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        };
        dbContext.CollectiveFunds.Add(fund);

        RecordPlayerFundTransfer(player, fundParticipant, seedAmount, currentCycleId, now);
        await dbContext.SaveChangesAsync();
        return PlayerFundResult.Ok();
    }

    private async Task<PlayerFundResult> DepositToPlayerFundCoreAsync(decimal amount)
    {
        var (context, error) = await ResolvePlayerFundAsync();
        if (context is null)
        {
            return PlayerFundResult.Fail(error!);
        }

        if (amount <= 0m)
        {
            return PlayerFundResult.Fail("Deposit amount must be positive.");
        }

        if (amount > TransferableCash(context.Player))
        {
            return PlayerFundResult.Fail("Deposit amount exceeds the player's available balance.");
        }

        RecordPlayerFundTransfer(context.Player, context.FundParticipant, amount, context.Market.CurrentCycleId ?? 0, DateTime.UtcNow);
        await dbContext.SaveChangesAsync();
        return PlayerFundResult.Ok();
    }

    private async Task<PlayerFundResult> WithdrawFromPlayerFundCoreAsync(decimal amount)
    {
        var (context, error) = await ResolvePlayerFundAsync();
        if (context is null)
        {
            return PlayerFundResult.Fail(error!);
        }

        if (amount <= 0m)
        {
            return PlayerFundResult.Fail("Withdrawal amount must be positive.");
        }

        // The player may pull out the fund's free cash and trading profits, but not the cash owed back to members
        // as returnable deposits, so a withdrawal can never strand a member's principal.
        var memberDepositsOwed = await dbContext.CollectiveFundParticipants
            .Where(member => member.CollectiveFundId == context.Fund.Id)
            .SumAsync(member => member.DepositAmount);
        var withdrawable = Math.Max(0m, TransferableCash(context.FundParticipant) - memberDepositsOwed);
        if (amount > withdrawable)
        {
            return PlayerFundResult.Fail("Withdrawal amount exceeds the fund's withdrawable cash.");
        }

        RecordPlayerFundTransfer(
            context.FundParticipant,
            context.Player,
            amount,
            context.Market.CurrentCycleId ?? 0,
            DateTime.UtcNow,
            MoneyTransactionType.CollectiveFundWithdrawal,
            MoneyTransactionType.CollectiveFundWithdrawalReceived);
        await dbContext.SaveChangesAsync();
        return PlayerFundResult.Ok();
    }

    // Instant close: the player absorbs the fund's positions and residual cash while members are made whole in
    // cash, then the fund is tombstoned like any wound-down fund. Blocked while the fund cannot cover its members'
    // deposits in cash, since a member's principal must never be stranded.
    private async Task<PlayerFundResult> ClosePlayerFundCoreAsync()
    {
        var (context, error) = await ResolvePlayerFundAsync();
        if (context is null)
        {
            return PlayerFundResult.Fail(error!);
        }

        var player = context.Player;
        var fund = context.Fund;
        var fundParticipant = context.FundParticipant;
        var currentCycleId = context.Market.CurrentCycleId ?? 0;
        var now = DateTime.UtcNow;

        var hasPendingSettlement = await dbContext.SettlementInstructions.AnyAsync(instruction =>
            instruction.Status == SettlementStatus.Pending
            && (instruction.BuyerId == fundParticipant.Id || instruction.SellerId == fundParticipant.Id));
        if (hasPendingSettlement)
        {
            return PlayerFundResult.Fail("The fund cannot close while a trade is awaiting settlement.");
        }

        var marginLiability = await dbContext.MarginAccounts
            .Where(account => account.ParticipantId == fundParticipant.Id)
            .SumAsync(account => account.DebitBalance + account.AccruedInterest);
        if (marginLiability > 0m)
        {
            return PlayerFundResult.Fail("The fund cannot close while a margin liability remains.");
        }
        var termLoanLiability = await dbContext.Loans
            .Where(loan => loan.ParticipantId == fundParticipant.Id && loan.Status == LoanStatus.Open)
            .SumAsync(loan => loan.RemainingPrincipal + loan.PastDueInterest + loan.AccruedFees);
        if (termLoanLiability > 0m)
        {
            return PlayerFundResult.Fail("The fund cannot close while an explicit term loan remains.");
        }
        if (fundParticipant.CurrentBalance != fundParticipant.SettledCashBalance)
        {
            return PlayerFundResult.Fail("The fund cannot close until all economic cash is settled.");
        }

        var members = await dbContext.CollectiveFundParticipants
            .Where(member => member.CollectiveFundId == fund.Id)
            .ToListAsync();
        var totalDeposits = members.Sum(member => member.DepositAmount);
        if (totalDeposits > fundParticipant.CurrentBalance)
        {
            return PlayerFundResult.Fail(
                "The fund cannot cover its members' deposits in cash. Deposit the shortfall or sell the fund's holdings first.");
        }

        // Cancelling releases any buy reservations back into the fund's cash; a sell just stops resting.
        var openOrders = await dbContext.Orders
            .Where(order => order.ParticipantId == fundParticipant.Id
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .ToListAsync();
        foreach (var order in openOrders)
        {
            CancelOrder(order, fundParticipant, currentCycleId);
        }

        // The player takes over every fund position at the fund's cost basis, blended into any it already holds.
        var fundHoldings = await dbContext.Holdings
            .Where(holding => holding.ParticipantId == fundParticipant.Id && holding.Quantity > 0)
            .ToListAsync();
        foreach (var fundHolding in fundHoldings)
        {
            var playerHolding = await dbContext.Holdings
                .FirstOrDefaultAsync(holding => holding.ParticipantId == player.Id && holding.CompanyId == fundHolding.CompanyId);
            if (playerHolding is null)
            {
                dbContext.Holdings.Add(new Holding
                {
                    ParticipantId = player.Id,
                    CompanyId = fundHolding.CompanyId,
                    Quantity = fundHolding.Quantity,
                    SettledQuantity = fundHolding.SettledQuantity,
                    AverageCost = fundHolding.AverageCost,
                });
            }
            else
            {
                var mergedQuantity = playerHolding.Quantity + fundHolding.Quantity;
                if (mergedQuantity > 0)
                {
                    playerHolding.AverageCost =
                        ((playerHolding.Quantity * playerHolding.AverageCost) + (fundHolding.Quantity * fundHolding.AverageCost)) / mergedQuantity;
                }
                playerHolding.Quantity = mergedQuantity;
                playerHolding.SettledQuantity += fundHolding.SettledQuantity;
            }

            fundHolding.Quantity = 0;
            fundHolding.SettledQuantity = 0;
        }

        // Members get their deposits back, then the player takes whatever cash is left.
        foreach (var member in members)
        {
            var memberParticipant = await dbContext.Participants
                .FirstOrDefaultAsync(participant => participant.Id == member.ParticipantId);
            var payout = memberParticipant is not null && member.DepositAmount > 0m ? member.DepositAmount : 0m;
            if (payout > 0m)
            {
                RecordPlayerFundTransfer(fundParticipant, memberParticipant!, payout, currentCycleId, now);
            }

            dbContext.CollectiveFundMembershipEvents.Add(new CollectiveFundMembershipEvent
            {
                CollectiveFundId = fund.Id,
                FundParticipantId = fundParticipant.Id,
                ParticipantId = member.ParticipantId,
                Type = CollectiveFundMembershipEventType.Left,
                Amount = payout,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
            });

            dbContext.CollectiveFundParticipants.Remove(member);
        }

        if (fundParticipant.CurrentBalance > 0m)
        {
            RecordPlayerFundTransfer(
                fundParticipant,
                player,
                fundParticipant.CurrentBalance,
                currentCycleId,
                now,
                MoneyTransactionType.CollectiveFundWithdrawal,
                MoneyTransactionType.CollectiveFundWithdrawalReceived);
        }

        // A winding-down fund's loans are discharged like any departing borrower's.
        var openLoans = await dbContext.Loans
            .Where(loan => loan.ParticipantId == fundParticipant.Id && loan.Status == LoanStatus.Open)
            .ToListAsync();
        foreach (var loan in openLoans)
        {
            LoanService.MarkClosed(loan, LoanCloseReason.ParticipantDeparted, currentCycleId, now);
        }

        // Rounding dust below the cent is dropped so the closed fund settles flat.
        fundParticipant.CurrentBalance = 0m;
        fundParticipant.SettledCashBalance = 0m;
        fundParticipant.ReservedBalance = 0m;
        fundParticipant.IsActive = false;
        fund.Status = CollectiveFundStatus.Closed;
        fund.ClosedAt = now;

        await dbContext.SaveChangesAsync();
        return PlayerFundResult.Ok();
    }

    private async Task<FundAdvertiseQuoteResult> GetFundAdvertiseQuoteCoreAsync(int fundParticipantId)
    {
        var (context, error) = await ResolvePlayerFundAsync();
        if (context is null)
        {
            return FundAdvertiseQuoteResult.Fail(error!);
        }

        if (context.FundParticipant.Id != fundParticipantId)
        {
            return FundAdvertiseQuoteResult.Fail("The fund is not player-managed.");
        }

        return FundAdvertiseQuoteResult.Ok(await ComputeAdvertiseQuoteAsync(context.Fund, context.FundParticipant));
    }

    // Pays for one advertisement out of the fund's own cash: a fund advertisement debit, a no-impact newswire so
    // traders see the fund is recruiting, one more popularity point, and a stamp of the cycle so decay knows the
    // fund is fresh. Popularity is the only channel by which an ad changes anyone's join odds.
    private async Task<PlayerFundResult> AdvertiseFundCoreAsync(int fundParticipantId)
    {
        var (context, error) = await ResolvePlayerFundAsync();
        if (context is null)
        {
            return PlayerFundResult.Fail(error!);
        }

        if (context.FundParticipant.Id != fundParticipantId)
        {
            return PlayerFundResult.Fail("The fund is not player-managed.");
        }

        var fund = context.Fund;
        var fundParticipant = context.FundParticipant;
        var quote = await ComputeAdvertiseQuoteAsync(fund, fundParticipant);
        if (quote.Price > fundParticipant.AvailableBalance)
        {
            return PlayerFundResult.Fail("The fund cannot afford the advertisement.");
        }

        var currentCycleId = context.Market.CurrentCycleId ?? 0;
        var currentCycleNumber = await dbContext.MarketCycles
            .Where(cycle => cycle.Id == currentCycleId)
            .Select(cycle => (int?)cycle.CycleNumber)
            .FirstOrDefaultAsync() ?? 0;
        var now = DateTime.UtcNow;

        if (quote.Price > 0m)
        {
            fundParticipant.CurrentBalance -= quote.Price;
            fundParticipant.SettledCashBalance -= quote.Price;
            dbContext.MoneyTransactions.Add(new MoneyTransaction
            {
                ParticipantId = fundParticipant.Id,
                Type = MoneyTransactionType.FundAdvertisement,
                Amount = quote.Price,
                Description = "Fund advertising campaign fee",
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
            });
        }

        dbContext.NewsPosts.Add(new NewsPost
        {
            Title = $"{fundParticipant.Name} is looking for new members",
            Content = $"{fundParticipant.Name} has taken out an advertisement inviting traders to pool their cash and join the fund.",
            PublishedInCycleId = currentCycleId,
            PublishedAt = now,
            Scope = NewsImpactScope.None,
            Category = NewsCategory.FundAdvertisement,
        });

        fund.PopularityIndex += 1;
        fund.LastAdvertisedInCycleNumber = currentCycleNumber;

        await dbContext.SaveChangesAsync();
        return PlayerFundResult.Ok();
    }

    // Prices one advertisement: a fraction of the fund's worth (cash plus holdings minus open-loan liability) that
    // slides from the dear fraction when the fund is flat or down to the cheap fraction once it is up by the growth
    // cap or more, linear and clamped. Reused by the quote endpoint and the paid advertisement.
    private async Task<FundAdvertiseQuote> ComputeAdvertiseQuoteAsync(CollectiveFund fund, Participant fundParticipant)
    {
        var latestPriceByCompany = await LatestPriceByCompanyAsync();
        var holdings = await dbContext.Holdings
            .Where(holding => holding.ParticipantId == fundParticipant.Id && holding.Quantity > 0)
            .Select(holding => new { holding.CompanyId, holding.Quantity })
            .ToListAsync();
        var holdingsValue = holdings.Sum(holding => holding.Quantity * latestPriceByCompany.GetValueOrDefault(holding.CompanyId));
        var loanLiability = await dbContext.Loans
            .Where(loan => loan.ParticipantId == fundParticipant.Id && loan.Status == LoanStatus.Open)
            .SumAsync(loan => loan.RemainingPrincipal + loan.PastDueInterest + loan.AccruedFees);
        var marginLiability = await dbContext.MarginAccounts
            .Where(account => account.ParticipantId == fundParticipant.Id)
            .SumAsync(account => account.DebitBalance + account.AccruedInterest);
        var fundWorth = fundParticipant.CurrentBalance + holdingsValue - loanLiability - marginLiability;

        var growth = await ComputeFundGrowthAsync(fundParticipant.Id);
        var clampedGrowth = Math.Clamp(growth, 0m, AdvertiseGrowthCap);
        var fraction = AdvertiseDearFraction + ((clampedGrowth / AdvertiseGrowthCap) * (AdvertiseCheapFraction - AdvertiseDearFraction));
        var price = Round(Math.Max(0m, fraction * fundWorth));

        return new FundAdvertiseQuote(price, fraction, Round(growth * 100m), fundWorth, fund.PopularityIndex);
    }

    // The fund's net-worth growth over the advertising window, read from its worth snapshots: latest against the
    // snapshot a window back, or against the earliest recorded snapshot when it has less than a full window of
    // history. Zero (the dearest price) when there is no usable baseline.
    private async Task<decimal> ComputeFundGrowthAsync(int fundParticipantId)
    {
        var recentWorths = (await dbContext.ParticipantWorthSnapshots
                .Where(snapshot => snapshot.ParticipantId == fundParticipantId)
                .OrderByDescending(snapshot => snapshot.Id)
                .Take(AdvertiseWindowCycles + 1)
                .Select(snapshot => snapshot.Balance + snapshot.HoldingsValue - snapshot.LoanLiability - snapshot.MarginLiability)
                .ToListAsync());
        if (recentWorths.Count == 0)
        {
            return 0m;
        }

        var latest = recentWorths[0];
        var baseline = recentWorths[^1];
        return baseline > 0m ? (latest - baseline) / baseline : 0m;
    }

    // Moves cash one way between the player and its fund and records both legs, reusing the fund cash-movement
    // type the AI join/leave flow already uses.
    private void RecordPlayerFundTransfer(
        Participant from,
        Participant to,
        decimal amount,
        int currentCycleId,
        DateTime now,
        MoneyTransactionType fromType = MoneyTransactionType.CollectiveFund,
        MoneyTransactionType toType = MoneyTransactionType.CollectiveFund)
    {
        from.CurrentBalance -= amount;
        from.SettledCashBalance -= amount;
        to.CurrentBalance += amount;
        to.SettledCashBalance += amount;
        dbContext.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = from.Id,
            Type = fromType,
            Amount = amount,
            Description = $"Transfer to {to.Name}",
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });
        dbContext.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = to.Id,
            Type = toType,
            Amount = amount,
            FromWhomId = from.Id,
            Description = $"Transfer from {from.Name}",
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });
    }

    private static decimal TransferableCash(Participant participant) =>
        Math.Max(0m, Math.Min(participant.AvailableBalance, participant.SettledCashBalance));

    private async Task<RunDecisionsResult> GenerateDecisionsCoreAsync()
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market?.CurrentCycleId is null)
        {
            return RunDecisionsResult.Fail("Market is not running.");
        }

        // Net buy demand counts only participant orders; the issuer's seed sell of every share would
        // otherwise swamp the signal and read as permanent selling pressure.
        var netDemandByCompany = (await dbContext.Orders
                .Where(order => (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
                    && order.ParticipantId != null)
                .Select(order => new { order.CompanyId, order.Type, Remaining = order.Quantity - order.FilledQuantity })
                .ToListAsync())
            .GroupBy(order => order.CompanyId)
            .ToDictionary(
                group => group.Key,
                // Sum in long: aggregate remaining buy demand across all participants for one company is
                // unbounded by share count and overflows a 32-bit accumulator on a hot company.
                group => group.Sum(order => order.Type == OrderType.Buy ? (long)order.Remaining : -(long)order.Remaining));

        var cycleNumbersById = await dbContext.MarketCycles
            .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);
        var currentCycleNumber = cycleNumbersById.GetValueOrDefault(market.CurrentCycleId.Value);
        var baselineCycleNumber = currentCycleNumber - LongRangeWindowCycles;

        // A live crisis makes conservative and low-risk traders hold back on buying.
        var crisisActive = crisisService is not null
            && await crisisService.GetActiveCrisisAsync(currentCycleNumber) is not null;

        // Delisted companies keep their price history for the detail page but must not be offered to the engine,
        // which would otherwise rest ghost bids on a stock that has no float to fill them.
        var closedCompanyIds = (await dbContext.Companies
                .Where(company => company.ClosedInCycleId != null)
                .Select(company => company.Id)
                .ToListAsync())
            .ToHashSet();

        // A company frozen by a volatility halt cannot trade this cycle, so like a delisted one it is kept out
        // of the quote universe — otherwise the engine would rest bids that no float can fill.
        var haltedCompanyIds = (await dbContext.PriceBandStates
                .Where(state => state.State != LuldState.Normal)
                .Select(state => state.CompanyId)
                .ToListAsync())
            .ToHashSet();

        var snapshots = await dbContext.PriceSnapshots.AsNoTracking().ToListAsync();
        // A trading halt removes order eligibility, not economic value, so portfolio and margin valuation keep
        // the latest price for every active company independently from the tradable quote map.
        var valuationPriceByCompany = await PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);
        foreach (var closedCompanyId in closedCompanyIds)
        {
            valuationPriceByCompany.Remove(closedCompanyId);
        }

        var quoteCompanyIds = snapshots
            .Select(snapshot => snapshot.CompanyId)
            .Where(companyId => !closedCompanyIds.Contains(companyId) && !haltedCompanyIds.Contains(companyId))
            .ToHashSet();
        var sentimentByCompany = await dbContext.Companies.AsNoTracking()
            .Where(company => quoteCompanyIds.Contains(company.Id))
            .Join(
                dbContext.Industries.AsNoTracking(),
                company => company.IndustryId,
                industry => industry.Id,
                (company, industry) => new { company.Id, industry.SentimentValue })
            .ToDictionaryAsync(pair => pair.Id, pair => pair.SentimentValue);
        var issuedSharesByCompany = await dbContext.Companies.AsNoTracking()
            .Where(company => quoteCompanyIds.Contains(company.Id))
            .ToDictionaryAsync(company => company.Id, company => company.IssuedSharesCount);
        var quotes = snapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .Where(group => !closedCompanyIds.Contains(group.Key) && !haltedCompanyIds.Contains(group.Key))
            .Select(group =>
            {
                var ordered = group.OrderByDescending(snapshot => snapshot.Id).ToList();
                var latest = ordered[0];
                var priorCycleClose = ordered.FirstOrDefault(snapshot => snapshot.CreatedInCycleId != latest.CreatedInCycleId);
                var changePct = priorCycleClose is { Price: > 0m }
                    ? (latest.Price - priorCycleClose.Price) / priorCycleClose.Price
                    : 0m;

                // Newest snapshot at or before the baseline cycle is the price "ten cycles ago"; absent
                // enough history the long-range move stays zero.
                var longRangeClose = ordered.FirstOrDefault(snapshot =>
                    cycleNumbersById.GetValueOrDefault(snapshot.CreatedInCycleId) <= baselineCycleNumber);
                var longRangeChangePct = longRangeClose is { Price: > 0m }
                    ? (latest.Price - longRangeClose.Price) / longRangeClose.Price
                    : 0m;

                return new CompanyQuote(
                    group.Key,
                    latest.Price,
                    changePct,
                    (int)Math.Clamp(netDemandByCompany.GetValueOrDefault(group.Key), int.MinValue, int.MaxValue),
                    longRangeChangePct,
                    sentimentByCompany.GetValueOrDefault(group.Key));
            })
            .ToList();

        if (quotes.Count == 0)
        {
            return RunDecisionsResult.Ok(0);
        }

        // Configured AI Agents are owned only by the hosted coordinator, so they never reach the rule-based
        // engine; the engine drives Individuals and ordinary Collective Funds.
        var traders = await dbContext.Participants
            .Where(participant => participant.IsActive
                && (participant.Type == ParticipantType.Individual
                    || participant.Type == ParticipantType.CollectiveFund))
            .OrderBy(participant => participant.Id)
            .ToListAsync();

        var holdingsByOwner = (await dbContext.Holdings
                .Where(holding => holding.Quantity > 0)
                .Select(holding => new { holding.ParticipantId, holding.CompanyId, holding.Quantity })
                .ToListAsync())
            .GroupBy(holding => holding.ParticipantId)
            .ToDictionary(
                ownerGroup => ownerGroup.Key,
                ownerGroup => (IReadOnlyDictionary<int, int>)ownerGroup
                    .ToDictionary(holding => holding.CompanyId, holding => holding.Quantity));

        var openOrdersByParticipant = (await dbContext.Orders
                .Where(order => (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
                    && order.ParticipantId != null)
                .Select(order => new { ParticipantId = order.ParticipantId!.Value, order.CompanyId })
                .ToListAsync())
            .GroupBy(order => order.ParticipantId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<int>)group.Select(order => order.CompanyId).ToHashSet());

        var priceByCompany = quotes.ToDictionary(quote => quote.CompanyId, quote => quote.Price);

        // Bounds are resolved once for the whole batch and carried on the quote, so the pure engine prices against
        // them and the deferred writes validate against the same map without any per-order band query.
        var boundsByCompany = await ResolveOrderPriceBoundsByCompanyAsync(priceByCompany);
        var openSellRows = await dbContext.Orders.AsNoTracking()
            .Where(order => (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
                && order.Type == OrderType.Sell
                && quoteCompanyIds.Contains(order.CompanyId))
            .Select(order => new
            {
                order.CompanyId,
                order.LimitPrice,
                RemainingQuantity = order.Quantity - order.FilledQuantity,
            })
            .ToListAsync();
        // Decisions are staged before matching, so Individuals share these transient price levels rather than
        // each treating the same ask shares as independently available. Funds retain their legacy quote view.
        var executableAskLevelsByCompany = openSellRows
            .Where(order => order.RemainingQuantity > 0
                && boundsByCompany.TryGetValue(order.CompanyId, out var bounds)
                && bounds.IsWithinActiveBand(order.LimitPrice))
            .GroupBy(order => order.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(order => order.LimitPrice)
                    .OrderBy(level => level.Key)
                    .Select(level => new ExecutableAskLevel(
                        level.Key,
                        level.Sum(order => (long)order.RemainingQuantity)))
                    .ToList());
        var openSellQuantityByCompany = openSellRows
            .Where(order => order.RemainingQuantity > 0)
            .GroupBy(order => order.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => (int)Math.Clamp(
                    group.Sum(order => (long)order.RemainingQuantity),
                    0L,
                    int.MaxValue));
        var openBuyRows = await dbContext.Orders.AsNoTracking()
            .Where(order => (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
                && order.Type == OrderType.Buy
                && quoteCompanyIds.Contains(order.CompanyId))
            .Select(order => new
            {
                order.Id,
                order.CompanyId,
                order.LimitPrice,
                order.CreatedAt,
                RemainingQuantity = order.Quantity - order.FilledQuantity,
            })
            .ToListAsync();
        // Shadow consumption keeps deferred decisions behind demand that MatchingEngine will process first.
        // The lowest consuming limit is the boundary a later bid must not jump.
        var priorBuyPriorityLimitByCompany = new Dictionary<int, decimal>();
        foreach (var companyBuys in openBuyRows
                     .Where(order => order.RemainingQuantity > 0
                         && boundsByCompany.TryGetValue(order.CompanyId, out var bounds)
                         && bounds.IsWithinActiveBand(order.LimitPrice))
                     .GroupBy(order => order.CompanyId))
        {
            foreach (var buy in companyBuys
                         .OrderByDescending(order => order.LimitPrice)
                         .ThenBy(order => order.CreatedAt)
                         .ThenBy(order => order.Id))
            {
                // Seller identity is intentionally absent from the aggregated levels, so a potential self-cross
                // reserves supply conservatively; MatchingEngine still owns cancellation and persisted fills.
                var consumedQuantity = ConsumeExecutableAskLevels(
                    executableAskLevelsByCompany,
                    buy.CompanyId,
                    buy.LimitPrice,
                    buy.RemainingQuantity);
                if (consumedQuantity > 0)
                {
                    UpdatePriorBuyPriorityLimit(
                        priorBuyPriorityLimitByCompany,
                        buy.CompanyId,
                        buy.LimitPrice);
                }
            }
        }

        quotes = quotes
            .Select(quote =>
            {
                var bestAsk = executableAskLevelsByCompany.TryGetValue(quote.CompanyId, out var levels)
                    && levels.Count > 0
                        ? levels[0]
                        : null;
                return quote with
                {
                    Bounds = boundsByCompany.GetValueOrDefault(quote.CompanyId),
                    IssuedShares = issuedSharesByCompany.GetValueOrDefault(quote.CompanyId),
                    BestExecutableSellPrice = bestAsk?.Price,
                    BestExecutableSellQuantity = bestAsk is not null
                        ? (int)Math.Clamp(bestAsk.RemainingQuantity, 0L, int.MaxValue)
                        : 0,
                    OpenSellQuantity = openSellQuantityByCompany.GetValueOrDefault(quote.CompanyId),
                };
            })
            .ToList();

        var loanLiabilityByParticipant = await LoanService.OpenLoanLiabilityByParticipantAsync(dbContext);
        var marginLiabilityByParticipant = await MarginService.LiabilityByParticipantAsync(dbContext);
        var fundStatusByParticipantId = await dbContext.CollectiveFunds
            .ToDictionaryAsync(fund => fund.ParticipantId, fund => fund.Status);

        // A player-managed fund only trades when the human places an order through it, so it is left out of the
        // automatic decision pass entirely.
        var playerManagedFundParticipantIds = (await dbContext.CollectiveFunds
                .Where(fund => fund.IsPlayerManaged)
                .Select(fund => fund.ParticipantId)
                .ToListAsync())
            .ToHashSet();
        var memberParticipantIds = (await dbContext.CollectiveFundParticipants
                .Select(member => member.ParticipantId)
                .ToListAsync())
            .ToHashSet();
        var preLeaveBufferFundParticipantIds = await PreLeaveBufferFundParticipantIdsAsync(market.CurrentTradingDayId);

        var ordersPlaced = 0;

        foreach (var trader in traders)
        {
            // The human runs a player-managed fund by hand, so the engine never trades it.
            if (playerManagedFundParticipantIds.Contains(trader.Id))
            {
                continue;
            }

            // A fund that is winding down places no new orders; its forced sales are service-driven.
            if (trader.Type == ParticipantType.CollectiveFund
                && fundStatusByParticipantId.GetValueOrDefault(trader.Id) != CollectiveFundStatus.Active)
            {
                continue;
            }

            var openLoanLiability = loanLiabilityByParticipant.GetValueOrDefault(trader.Id);
            var marginLiability = marginLiabilityByParticipant.GetValueOrDefault(trader.Id);
            var buyingPower = marginService is null
                ? trader.AvailableBalance
                : await marginService.GetBuyingPowerAsync(trader.Id, valuationPriceByCompany);
            var holdingsValue = holdingsByOwner.TryGetValue(trader.Id, out var holdings)
                ? holdings.Sum(holding => holding.Value * valuationPriceByCompany.GetValueOrDefault(holding.Key))
                : 0m;
            var decisionQuotes = trader.Type == ParticipantType.Individual
                ? quotes.Select(quote =>
                {
                    var bestAsk = executableAskLevelsByCompany.TryGetValue(quote.CompanyId, out var levels)
                        && levels.Count > 0
                            ? levels[0]
                            : null;
                    var buyBlockedForBatch = priorBuyPriorityLimitByCompany.TryGetValue(
                            quote.CompanyId,
                            out var priorBuyLimit)
                        && (bestAsk is null || bestAsk.Price > priorBuyLimit);
                    if (buyBlockedForBatch)
                    {
                        bestAsk = null;
                    }

                    return quote with
                    {
                        BestExecutableSellPrice = bestAsk?.Price,
                        BestExecutableSellQuantity = bestAsk is not null
                            ? (int)Math.Clamp(bestAsk.RemainingQuantity, 0L, int.MaxValue)
                            : 0,
                        IndividualBuyBlockedForBatch = buyBlockedForBatch,
                    };
                }).ToList()
                : quotes;
            var context = new DecisionContext(
                trader,
                AvailableCashForDecisions(
                    trader,
                    memberParticipantIds,
                    preLeaveBufferFundParticipantIds,
                    holdingsByOwner,
                    priceByCompany,
                    buyingPower),
                decisionQuotes,
                holdingsByOwner.GetValueOrDefault(trader.Id, NoHoldings),
                openOrdersByParticipant.GetValueOrDefault(trader.Id, NoOpenOrders),
                crisisActive,
                openLoanLiability,
                HoldingsValue: holdingsValue,
                NetWorth: trader.CurrentBalance + holdingsValue - openLoanLiability - marginLiability,
                AvailableBalance: trader.AvailableBalance,
                BuyingPower: buyingPower,
                MarginLiability: marginLiability,
                ReservedBuyNotional: trader.ReservedBalance,
                HasAutomatedTradingData: true);

            foreach (var intent in decisionEngine.Decide(context))
            {
                // The engine emits at most one order per trader and skips companies the trader already has an
                // open order in, so staged orders never race the per-order validation — one save covers the pass.
                var result = await PlaceOrderCoreAsync(
                    trader.Id,
                    intent.CompanyId,
                    intent.Type,
                    intent.Quantity,
                    intent.LimitPrice,
                    priceByCompany,
                    deferSave: true,
                    boundsByCompany: boundsByCompany);

                if (result.Success)
                {
                    ordersPlaced++;
                    if ((trader.Type == ParticipantType.Individual
                            || trader.Type == ParticipantType.CollectiveFund)
                        && intent.Type == OrderType.Buy
                        && boundsByCompany.TryGetValue(intent.CompanyId, out var bounds)
                        && bounds.IsWithinActiveBand(intent.LimitPrice))
                    {
                        var consumedQuantity = ConsumeExecutableAskLevels(
                            executableAskLevelsByCompany,
                            intent.CompanyId,
                            intent.LimitPrice,
                            result.Order!.Quantity);
                        if (consumedQuantity > 0)
                        {
                            UpdatePriorBuyPriorityLimit(
                                priorBuyPriorityLimitByCompany,
                                intent.CompanyId,
                                intent.LimitPrice);
                        }
                    }
                }
            }
        }

        await dbContext.SaveChangesAsync();
        return RunDecisionsResult.Ok(ordersPlaced);
    }

    private static void UpdatePriorBuyPriorityLimit(
        IDictionary<int, decimal> priorityLimitByCompany,
        int companyId,
        decimal buyLimit)
    {
        priorityLimitByCompany[companyId] = priorityLimitByCompany.TryGetValue(companyId, out var existingLimit)
            ? Math.Min(existingLimit, buyLimit)
            : buyLimit;
    }

    private static long ConsumeExecutableAskLevels(
        IReadOnlyDictionary<int, List<ExecutableAskLevel>> levelsByCompany,
        int companyId,
        decimal buyLimit,
        long buyQuantity)
    {
        if (buyQuantity <= 0 || !levelsByCompany.TryGetValue(companyId, out var levels))
        {
            return 0;
        }

        var requestedQuantity = buyQuantity;
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

        return requestedQuantity - buyQuantity;
    }

    // A fund member hands its buying to the fund — cash is zeroed so the engine can only sell its own shares.
    // Automated fund buys stay within settled cash above the payout reserve so margin cannot consume that liquidity.
    private decimal AvailableCashForDecisions(
        Participant trader,
        IReadOnlySet<int> memberParticipantIds,
        IReadOnlySet<int> preLeaveBufferFundParticipantIds,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> holdingsByOwner,
        IReadOnlyDictionary<int, decimal> priceByCompany,
        decimal buyingPower)
    {
        if (memberParticipantIds.Contains(trader.Id))
        {
            return 0m;
        }

        var holdingsValue = holdingsByOwner.TryGetValue(trader.Id, out var holdings)
            ? holdings.Sum(holding => holding.Value * priceByCompany.GetValueOrDefault(holding.Key))
            : 0m;
        if (trader.Type != ParticipantType.CollectiveFund)
        {
            return buyingPower;
        }

        var totalWorth = trader.AvailableBalance + holdingsValue;
        var bufferFraction = preLeaveBufferFundParticipantIds.Contains(trader.Id)
            ? collectiveFundOptionValues.PreLeaveCashBufferFraction
            : collectiveFundOptionValues.CashBufferFraction;
        var cashReserve = Math.Clamp(bufferFraction, 0m, 1m) * totalWorth;
        var settledCashAboveReserve = Math.Max(
            0m,
            Math.Min(trader.AvailableBalance, trader.SettledCashBalance) - cashReserve);
        return Math.Min(buyingPower, settledCashAboveReserve);
    }

    private async Task<IReadOnlySet<int>> PreLeaveBufferFundParticipantIdsAsync(int? currentTradingDayId)
    {
        if (currentTradingDayId is not int dayId)
        {
            return new HashSet<int>();
        }

        var currentTradingDayNumber = await dbContext.TradingDays
            .Where(day => day.Id == dayId)
            .Select(day => (int?)day.DayNumber)
            .FirstOrDefaultAsync();
        if (currentTradingDayNumber is null)
        {
            return new HashSet<int>();
        }

        var eligibilityCutoffDay = currentTradingDayNumber.Value
            + 1
            - Math.Max(0, collectiveFundOptionValues.MinimumMembershipTradingDays);
        return (await (
                from membership in dbContext.CollectiveFundParticipants
                join joinedCycle in dbContext.MarketCycles on membership.JoinedInCycleId equals joinedCycle.Id
                join joinedDay in dbContext.TradingDays on joinedCycle.TradingDayId equals joinedDay.Id
                join fund in dbContext.CollectiveFunds on membership.CollectiveFundId equals fund.Id
                where membership.IsLeaving || joinedDay.DayNumber <= eligibilityCutoffDay
                select fund.ParticipantId)
            .Distinct()
            .ToListAsync())
            .ToHashSet();
    }

    private async Task<Market> ResetDemoMarketCoreAsync()
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        await dbContext.MarginCalls.ExecuteDeleteAsync();
        await dbContext.MarginAccounts.ExecuteDeleteAsync();
        await dbContext.Loans.ExecuteDeleteAsync();
        await dbContext.TradingBreakCycles.ExecuteDeleteAsync();
        await dbContext.TradingDays.ExecuteDeleteAsync();
        await dbContext.Banks.ExecuteDeleteAsync();
        await dbContext.CrisisEvents.ExecuteDeleteAsync();
        await dbContext.CrisisIndustries.ExecuteDeleteAsync();
        await dbContext.Crises.ExecuteDeleteAsync();
        await dbContext.ScienceInvestigationIndustries.ExecuteDeleteAsync();
        await dbContext.ScienceInvestigations.ExecuteDeleteAsync();
        await dbContext.Bankruptcies.ExecuteDeleteAsync();
        await dbContext.MarketExits.ExecuteDeleteAsync();
        await dbContext.CollectiveFundMembershipEvents.ExecuteDeleteAsync();
        await dbContext.CollectiveFundParticipants.ExecuteDeleteAsync();
        await dbContext.CollectiveFunds.ExecuteDeleteAsync();
        await dbContext.NewsPostIndustries.ExecuteDeleteAsync();
        await dbContext.NewsPosts.ExecuteDeleteAsync();
        await dbContext.SettlementInstructions.ExecuteDeleteAsync();
        await dbContext.OrderFills.ExecuteDeleteAsync();
        await dbContext.DividendPayouts.ExecuteDeleteAsync();
        await dbContext.CorporateCashTransactions.ExecuteDeleteAsync();
        await dbContext.StockDenominationEvents.ExecuteDeleteAsync();
        await dbContext.PriceBandStates.ExecuteDeleteAsync();
        await dbContext.MoneyTransactions.ExecuteDeleteAsync();
        await dbContext.PriceSnapshots.ExecuteDeleteAsync();
        await dbContext.Holdings.ExecuteDeleteAsync();
        await dbContext.ShareTransactions.ExecuteDeleteAsync();
        await dbContext.Orders.ExecuteDeleteAsync();
        await dbContext.MarketCycles.ExecuteDeleteAsync();
        await dbContext.ParticipantWorthSnapshots.ExecuteDeleteAsync();
        await dbContext.PriceSnapshotArchives.ExecuteDeleteAsync();
        await dbContext.MoneyTransactionArchives.ExecuteDeleteAsync();
        await dbContext.ParticipantWorthSnapshotArchives.ExecuteDeleteAsync();
        await dbContext.SectorSentimentSnapshots.ExecuteDeleteAsync();
        await dbContext.SectorSentimentSnapshotArchives.ExecuteDeleteAsync();
        // Call history has no foreign key, so it is cleared first; the configuration would otherwise cascade with
        // its participant, but a full reset removes both AI tables explicitly and in dependency order.
        await dbContext.AiTraderCalls.ExecuteDeleteAsync();
        await dbContext.AiTraderConfigurations.ExecuteDeleteAsync();
        await dbContext.Participants.ExecuteDeleteAsync();
        await dbContext.Companies.ExecuteDeleteAsync();
        await dbContext.Industries.ExecuteDeleteAsync();
        await dbContext.Markets.ExecuteDeleteAsync();

        dbContext.ChangeTracker.Clear();
        await dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM sqlite_sequence WHERE name IN (" +
            "'Companies', 'MarketCycles', 'Markets', 'Orders', 'Participants', " +
            "'ShareTransactions', 'MoneyTransactions', 'DividendPayouts', 'CorporateCashTransactions', 'StockDenominationEvents', 'PriceBandStates', 'OrderFills', 'PriceSnapshots', 'Holdings', " +
            "'Industries', 'NewsPosts', 'NewsPostIndustries', 'Crises', 'CrisisIndustries', 'CrisisEvents', " +
            "'ScienceInvestigations', 'ScienceInvestigationIndustries', 'Bankruptcies', 'MarketExits', " +
            "'CollectiveFunds', 'CollectiveFundParticipants', 'CollectiveFundMembershipEvents', 'ParticipantWorthSnapshots', " +
            "'PriceSnapshotArchives', 'MoneyTransactionArchives', 'ParticipantWorthSnapshotArchives', " +
            "'SectorSentimentSnapshots', 'SectorSentimentSnapshotArchives', " +
            "'AiTraderCalls', 'AiTraderConfigurations', " +
            "'Banks', 'Loans', 'MarginAccounts', 'MarginCalls', 'TradingDays', 'TradingBreakCycles', 'SettlementInstructions')");

        var market = await SeedDemoMarketCoreAsync();
        await transaction.CommitAsync();

        return market;
    }

    private async Task<Market> SeedDemoMarketCoreAsync()
    {
        // Tunable size of the generated demo market; bump these to grow the simulation.
        const int companyCount = 100;
        const int participantCount = 300;
        const int minShares = 1000;
        const int maxShares = 10000;
        const int minPrice = 20;
        const int maxPrice = 300;

        // A small share of traders seed as "whales" with far deeper pockets so the market has a few large
        // players among many smaller ones, rather than a single uniform wealth band.
        const double whaleShare = 0.15;
        const long whaleMinBalance = 100_000;
        const long whaleMaxBalance = 2_000_000_000;
        const long regularMinBalance = 10_000;
        const long regularMaxBalance = 200_000;
        const int randomSeed = 20260619; // fixed seed keeps the generated demo data reproducible

        var random = new Random(randomSeed);
        var now = DateTime.UtcNow;

        var participantNames = DemoMarketNames.PickPeople(participantCount, random);
        var companyNames = DemoMarketNames.PickCompanies(companyCount, random);

        var firstCycle = new MarketCycle
        {
            CycleNumber = 1,
            TradingCycleNumber = 1,
            Status = CycleStatus.Running,
            StartedAt = now,
        };
        dbContext.MarketCycles.Add(firstCycle);

        var market = new Market
        {
            Name = "Demo Market",
            Status = MarketStatus.NotStarted,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.Markets.Add(market);

        var temperaments = new[] { Temperament.Aggressive, Temperament.Balanced, Temperament.Conservative };
        var riskProfiles = new[] { RiskProfile.High, RiskProfile.Medium, RiskProfile.Low };

        for (var index = 0; index < participantCount; index++)
        {
            var balance = random.NextDouble() < whaleShare
                ? random.NextInt64(whaleMinBalance, whaleMaxBalance + 1)
                : random.NextInt64(regularMinBalance, regularMaxBalance + 1);
            dbContext.Participants.Add(new Participant
            {
                Name = participantNames[index],
                // Seeded traders are all individuals for now; AI agents are introduced later.
                Type = ParticipantType.Individual,
                Temperament = temperaments[index % temperaments.Length],
                RiskProfile = riskProfiles[index % riskProfiles.Length],
                InitialBalance = balance,
                CurrentBalance = balance,
                SettledCashBalance = balance,
                ReservedBalance = 0m,
                IsActive = true,
            });
        }

        var industries = DemoIndustries.Names
            .Select(name => new Industry { Name = name })
            .ToList();
        dbContext.Industries.AddRange(industries);
        await dbContext.SaveChangesAsync();

        if (tradingClockService is not null)
        {
            var firstDay = new TradingDay
            {
                DayNumber = 1,
                State = TradingSessionState.Trading,
                OpenedInCycleId = firstCycle.Id,
            };
            dbContext.TradingDays.Add(firstDay);
            await dbContext.SaveChangesAsync();
            firstCycle.TradingDayId = firstDay.Id;
            market.CurrentTradingDayId = firstDay.Id;
            await dbContext.SaveChangesAsync();
        }
        var industryIds = industries.Select(industry => industry.Id).ToArray();

        var companies = new List<Company>(companyCount);
        var companyPrices = new decimal[companyCount];
        var companyShareCounts = new int[companyCount];

        for (var index = 0; index < companyCount; index++)
        {
            var price = random.Next(minPrice, maxPrice + 1);
            var shareCount = random.Next(minShares, maxShares + 1);
            companyPrices[index] = price;
            companyShareCounts[index] = shareCount;

            var company = new Company
            {
                Name = companyNames[index],
                // Round-robin keeps the reproducible RNG sequence untouched while spreading companies
                // across industries so most sectors have at least one listing for news to move.
                IndustryId = industryIds[index % industryIds.Length],
                IssuedSharesCount = shareCount,
                CashBalance = 0m,
                CreatedInCycleId = firstCycle.Id,
                CreatedAt = now,
                UpdatedAt = now,
            };
            companies.Add(company);
            dbContext.Companies.Add(company);
        }

        await dbContext.SaveChangesAsync();

        // The seed only adds new graphs, so change detection is turned off to keep the bulk
        // insert of per-share rows and their offers fast.
        dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            for (var index = 0; index < companyCount; index++)
            {
                var company = companies[index];
                var price = companyPrices[index];
                var shareCount = companyShareCounts[index];

                // Every issued share starts unowned as the company float, listed in a single
                // company-originated sell order so the whole supply is immediately available to buy.
                dbContext.Orders.Add(new Order
                {
                    ParticipantId = null,
                    CompanyId = company.Id,
                    Type = OrderType.Sell,
                    Status = OrderStatus.Open,
                    Quantity = shareCount,
                    FilledQuantity = 0,
                    LimitPrice = price,
                    ReservedCashAmount = 0m,
                    CreatedInCycleId = firstCycle.Id,
                    CreatedAt = now,
                    UpdatedAt = now,
                });

                dbContext.PriceSnapshots.Add(new PriceSnapshot
                {
                    CompanyId = company.Id,
                    Price = price,
                    Capitalization = price * shareCount,
                    CreatedInCycleId = firstCycle.Id,
                    CreatedAt = now,
                });
            }

            await dbContext.SaveChangesAsync();
        }
        finally
        {
            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
        }

        // Rating agencies that review companies once the market runs; added with no random draw so the generated
        // demo data above is unaffected.
        foreach (var (name, description) in DemoAuditorProfiles.Take(AuditorService.AuditorCountFor(companyCount)))
        {
            dbContext.Auditors.Add(new Auditor { Name = name, Description = description, CreatedAt = now });
        }

        // The single lending bank; the loan service also resolves-or-creates it defensively on first usage.
        dbContext.Banks.Add(new Bank
        {
            Name = loanOptionValues.BankName,
            InterestRatePerCycle = loanOptionValues.InterestRatePerCycle,
        });

        // Drawn last so adding the dividend schedule does not shift the generated demo data above.
        market.NextDividendCycleNumber = firstCycle.CycleNumber + RandomDividendInterval(random);
        // The appearance clock starts at the first cycle so the same safe period applies from launch.
        market.LastCompanyAppearanceCycleNumber = firstCycle.CycleNumber;
        market.CurrentCycleId = firstCycle.Id;

        if (industrySentimentOptionValues.Enabled)
        {
            foreach (var industry in industries.OrderBy(industry => industry.Id))
            {
                industry.SentimentValue = random.Next(
                    industrySentimentOptionValues.SentimentValueMin,
                    industrySentimentOptionValues.SentimentValueMax + 1);
                industry.SentimentVolatility = industrySentimentOptionValues.SentimentVolatilityMin +
                    ((decimal)random.NextDouble() *
                     (industrySentimentOptionValues.SentimentVolatilityMax - industrySentimentOptionValues.SentimentVolatilityMin));
                industry.SectorBeta = industrySentimentOptionValues.SectorBetaMin +
                    ((decimal)random.NextDouble() *
                     (industrySentimentOptionValues.SectorBetaMax - industrySentimentOptionValues.SectorBetaMin));
            }
        }

        await dbContext.SaveChangesAsync();

        return market;
    }

    private async Task<T> WithLockAsync<T>(Func<Task<T>> action)
    {
        await cycleLock.Semaphore.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            cycleLock.Semaphore.Release();
        }
    }

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> action)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            return await action();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        var result = await action();
        await transaction.CommitAsync();
        return result;
    }
}
