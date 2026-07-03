using Microsoft.EntityFrameworkCore;
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
    StockSplitService? stockSplitService = null)
{
    private static readonly IReadOnlyDictionary<int, int> NoHoldings = new Dictionary<int, int>();
    private static readonly IReadOnlySet<int> NoOpenOrders = new HashSet<int>();

    // How far back the long-range price move is measured for the engine's extreme-move reactions.
    private const int LongRangeWindowCycles = 10;

    // Order ageing: a resting order is force-cancelled once this old, and from RepriceFromAge it is
    // chased toward the market by RepriceStep with a probability that climbs the longer it stays
    // unfilled (see RepriceChance), so a stubborn order is cut more aggressively before the cap.
    private const int OrderMaxAgeCycles = 15;
    private const int OrderRepriceFromAge = 3;
    private const decimal RepriceStep = 0.10m;

    // After this many consecutive cycles unable to afford any share, a holder liquidates to raise cash.
    private const int CashStarvedLimitCycles = 5;

    // A collective fund keeps roughly this share of its total worth liquid so it can return members' deposits,
    // spending only the rest when it trades.
    private const decimal CollectiveFundCashBufferFraction = 0.10m;

    // A trader may reserve for buys beyond its available cash, borrowing up to this share of its total worth
    // (cash plus holdings). The debt surfaces as a negative balance, which incoming cash then pays down first.
    private const decimal DebtLimitFraction = 0.20m;

    // Dividends are paid at a random interval drawn in this range. Each paying company draws a rate in
    // [MinDividendRate, MaxDividendRate] of its capitalisation for the whole payout pool, split evenly across
    // its issued shares — so per share it works out to rate × price and a stock split cuts it proportionally.
    private const int MinDividendIntervalCycles = 10;
    private const int MaxDividendIntervalCycles = 25;
    private const decimal MinDividendRate = 0.0001m;
    private const decimal MaxDividendRate = 0.005m;

    // A company pays this window only if a per-company roll passes; the chance is high while its capitalisation
    // is stable and low once it has moved sharply, so payouts thin out during volatile stretches.
    private const decimal StableCapitalizationChance = 0.75m;
    private const decimal VolatileCapitalizationChance = 0.25m;
    private const decimal CapitalizationStabilityThreshold = 0.05m;

    // Anti-inflation ceiling on the total dividend cash one company injects per payout. Above it the effective
    // yield compresses toward zero as the company grows, so dividend injection stops tracking an ever-rising
    // market cap — the feedback loop that otherwise inflates prices without bound. Tunable.
    private const decimal MaxDividendCashPerCompanyPerPayout = 1_000_000m;

    // Forced-liquidation sells undercut the market by 1–5% so the order actually crosses.
    private const decimal MinSellOffset = 0.01m;
    private const decimal MaxSellOffset = 0.05m;

    // A joining human player starts with a whole-dollar balance drawn from this range.
    private const int PlayerMinBalance = 10_000;
    private const int PlayerMaxBalance = 200_000;

    public Task<Market?> GetMarketAsync() => dbContext.Markets.FirstOrDefaultAsync();

    public Task<PlaceOrderResult> PlaceOrderAsync(
        int participantId,
        int companyId,
        OrderType type,
        int quantity,
        decimal limitPrice) =>
        WithLockAsync(() => PlaceOrderCoreAsync(participantId, companyId, type, quantity, limitPrice));

    public Task<AdvanceCycleResult> AdvanceCycleAsync() => WithLockAsync(AdvanceCycleCoreAsync);

    public Task<RunDecisionsResult> GenerateDecisionsAsync() => WithLockAsync(GenerateDecisionsCoreAsync);

    // Single automatic step used by the background loop: decide then match under one lock so a manual
    // trigger cannot slip between the two halves. Skips unless the market is explicitly running.
    public Task<CycleTickResult> RunCycleTickAsync() => WithLockAsync(RunCycleTickCoreAsync);

    // Manual equivalent of one loop tick, used while the loop is stopped: same decide-then-match step
    // but without the running gate, so a single cycle can be stepped by hand.
    public Task<CycleTickResult> StepCycleAsync() => WithLockAsync(StepCycleCoreAsync);

    public Task<Market> SeedDemoMarketAsync() => WithLockAsync(SeedDemoMarketCoreAsync);

    public Task<Market> ResetDemoMarketAsync() => WithLockAsync(ResetDemoMarketCoreAsync);

    public Task<CreatePlayerResult> CreatePlayerAsync(string? name) =>
        WithLockAsync(() => CreatePlayerCoreAsync(name));

    public Task<CancelOrderResult> CancelPlayerOrderAsync(int orderId) =>
        WithLockAsync(() => CancelPlayerOrderCoreAsync(orderId));

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

        return await DecideAndAdvanceCoreAsync();
    }

    private async Task<CycleTickResult> StepCycleCoreAsync()
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market?.CurrentCycleId is null)
        {
            return CycleTickResult.Skipped();
        }

        return await DecideAndAdvanceCoreAsync();
    }

    private async Task<CycleTickResult> DecideAndAdvanceCoreAsync()
    {
        await MaintainOrdersCoreAsync();
        var decisions = await GenerateDecisionsCoreAsync();
        var advance = await AdvanceCycleCoreAsync();

        return CycleTickResult.Executed(decisions.OrdersPlaced, advance.FillCount, advance.CompletedCycleNumber);
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

        var openOrders = await dbContext.Orders
            .Where(order => (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
                && order.ParticipantId != null)
            .ToListAsync();

        var participantsById = await dbContext.Participants.ToDictionaryAsync(participant => participant.Id);

        foreach (var order in openOrders)
        {
            var participant = participantsById[order.ParticipantId!.Value];

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
            else if (random.NextDouble() < RepriceChance(age))
            {
                Reprice(order, participant, currentCycleId);
            }
        }

        // Persist cancellations first so freed shares and cash are visible to the liquidation pass.
        await dbContext.SaveChangesAsync();

        // Splits re-denominate prices and share counts before any worth-reading service runs, so bankruptcy,
        // funds, and exits all see the post-split state; saved at once since those services query the database.
        if (stockSplitService is not null)
        {
            await stockSplitService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow);
            await dbContext.SaveChangesAsync();
        }

        await LiquidateStarvedHoldersAsync(participantsById);

        // Runs before this cycle's matching so a wealthy trader's collapse, and a bankrupt trader's cheaper
        // re-listing, both reach the order book in time to cross the same cycle.
        if (bankruptcyService is not null)
        {
            await bankruptcyService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow);
        }

        // Runs in the same pre-match window so a fund's forced sales, and a new member's freed-up cash, reach
        // the order book in time to cross this cycle.
        if (collectiveFundService is not null)
        {
            await collectiveFundService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow);
        }

        // Saved before exit processing so a fund that closed this cycle has persisted its payouts, membership
        // deletes, and loss flags; the exit rolls then read a settled database.
        await dbContext.SaveChangesAsync();

        // Departures and their replacements land before this cycle's decision pass, so no ghost bids survive
        // matching and a replacement can trade the same tick.
        if (marketExitService is not null)
        {
            await marketExitService.ProcessForCycleAsync(currentCycleId, currentCycleNumber, DateTime.UtcNow);
            await dbContext.SaveChangesAsync();
        }
    }

    private void CancelOrder(Order order, Participant participant, int currentCycleId)
    {
        if (order.Type == OrderType.Buy)
        {
            var release = order.ReservedCashAmount;
            if (release > 0m)
            {
                participant.ReservedBalance -= release;
                order.ReservedCashAmount = 0m;
                dbContext.MoneyTransactions.Add(new MoneyTransaction
                {
                    ParticipantId = participant.Id,
                    Type = MoneyTransactionType.Release,
                    Amount = release,
                    RelatedOrderId = order.Id,
                    CreatedInCycleId = currentCycleId,
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }
        // A sell reserves no cash and holds no links; cancelling it simply stops the order counting
        // toward the seller's outstanding sells, freeing that quantity to be listed again.
        order.Status = OrderStatus.Cancelled;
        order.UpdatedAt = DateTime.UtcNow;
    }

    private void Reprice(Order order, Participant participant, int currentCycleId)
    {
        var now = DateTime.UtcNow;

        if (order.Type == OrderType.Sell)
        {
            order.LimitPrice = Round(order.LimitPrice * (1m - RepriceStep));
            order.UpdatedAt = now;
            return;
        }

        // A higher bid needs more cash reserved on the unfilled quantity; if the trader cannot cover the
        // top-up the order is left as-is and simply expires at the age cap.
        var newLimit = Round(order.LimitPrice * (1m + RepriceStep));
        var extraReservation = (newLimit - order.LimitPrice) * order.RemainingQuantity;
        if (extraReservation > participant.AvailableBalance)
        {
            return;
        }

        participant.ReservedBalance += extraReservation;
        order.ReservedCashAmount += extraReservation;
        order.LimitPrice = newLimit;
        order.UpdatedAt = now;

        dbContext.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = participant.Id,
            Type = MoneyTransactionType.Reserve,
            Amount = extraReservation,
            RelatedOrderId = order.Id,
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

    // Draw discipline for a scripted Random: companies are walked in ascending Id order, and each one with a
    // price draws exactly one NextDouble for its pay-or-skip roll; a company that pays then draws one more for
    // its rate. Skipped companies draw nothing further.
    private async Task PayDividendsAsync(int currentCycleId, DateTime now)
    {
        var latestPriceByCompany = await LatestPriceByCompanyAsync();
        if (latestPriceByCompany.Count == 0)
        {
            return;
        }

        var pricedCompanyIds = latestPriceByCompany.Keys.ToList();
        var companies = await dbContext.Companies
            .Where(company => pricedCompanyIds.Contains(company.Id))
            .OrderBy(company => company.Id)
            .ToListAsync();

        // Each priced company rolls whether to pay this window and, if it pays, declares its own rate; every
        // company's capitalisation baseline is refreshed either way so the next window measures from here.
        var rateByCompany = new Dictionary<int, decimal>();
        foreach (var company in companies)
        {
            var capitalization = latestPriceByCompany[company.Id] * company.IssuedSharesCount;
            if (RollPaysDividend(company.LastDividendCapitalization, capitalization))
            {
                rateByCompany[company.Id] = RandomDividendRate();
            }

            company.LastDividendCapitalization = capitalization;
        }

        if (rateByCompany.Count == 0)
        {
            return;
        }

        var payingCompanyIds = rateByCompany.Keys.ToList();
        var holdings = await dbContext.Holdings
            .Where(holding => holding.Quantity > 0 && payingCompanyIds.Contains(holding.CompanyId))
            .Select(holding => new { holding.ParticipantId, holding.CompanyId, holding.Quantity })
            .ToListAsync();

        // A company's pool is rate × capitalisation shared evenly over its issued shares, i.e. rate × price per
        // share; compress the rate so the total paid to holders never tops the ceiling, capping new-cash
        // injection instead of letting it track an ever-rising market cap.
        var effectiveRateByCompany = holdings
            .GroupBy(holding => holding.CompanyId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var rate = rateByCompany[group.Key];
                    var uncapped = latestPriceByCompany[group.Key] * rate * group.Sum(holding => holding.Quantity);
                    return uncapped > MaxDividendCashPerCompanyPerPayout
                        ? rate * (MaxDividendCashPerCompanyPerPayout / uncapped)
                        : rate;
                });

        var participantsById = await dbContext.Participants.ToDictionaryAsync(participant => participant.Id);

        foreach (var ownerGroup in holdings.GroupBy(holding => holding.ParticipantId))
        {
            if (!participantsById.TryGetValue(ownerGroup.Key, out var owner))
            {
                continue;
            }

            var payout = ownerGroup.Sum(holding =>
                latestPriceByCompany[holding.CompanyId] * effectiveRateByCompany[holding.CompanyId] * holding.Quantity);
            payout = Round(payout);
            if (payout <= 0m)
            {
                continue;
            }

            owner.CurrentBalance += payout;
            dbContext.MoneyTransactions.Add(new MoneyTransaction
            {
                ParticipantId = owner.Id,
                Type = MoneyTransactionType.Dividend,
                Amount = payout,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
            });

            // A fund passes part of its own dividend straight through to its members, split by their deposit.
            if (owner.Type == ParticipantType.CollectiveFund && collectiveFundService is not null)
            {
                await collectiveFundService.DistributeDividendToMembersAsync(owner, payout, currentCycleId, now);
            }
        }
    }

    private async Task<IReadOnlyDictionary<int, decimal>> LatestPriceByCompanyAsync()
    {
        var snapshots = await dbContext.PriceSnapshots.ToListAsync();
        return snapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.Id).First().Price);
    }

    // How much a participant may reserve on top of its available cash: a share of total worth (cash plus
    // holdings at the latest price), never negative so a trader already underwater cannot borrow further.
    private async Task<decimal> DebtAllowanceAsync(Participant participant)
    {
        var latestPriceByCompany = await LatestPriceByCompanyAsync();
        var holdingsValue = (await dbContext.Holdings
                .Where(holding => holding.ParticipantId == participant.Id && holding.Quantity > 0)
                .Select(holding => new { holding.CompanyId, holding.Quantity })
                .ToListAsync())
            .Sum(holding => holding.Quantity * latestPriceByCompany.GetValueOrDefault(holding.CompanyId));

        return Math.Max(0m, DebtLimitFraction * (participant.CurrentBalance + holdingsValue));
    }

    private static int RandomDividendInterval(Random rng) =>
        rng.Next(MinDividendIntervalCycles, MaxDividendIntervalCycles + 1);

    private decimal RandomDividendRate() =>
        MinDividendRate + ((decimal)random.NextDouble() * (MaxDividendRate - MinDividendRate));

    // A company pays with the high chance while its capitalisation has held within the stability band since the
    // last window (and on its first window, when there is no baseline yet), and the low chance once it moved
    // past the band — a proxy for skipping dividends during turbulent stretches. Always draws once.
    private bool RollPaysDividend(decimal? baselineCapitalization, decimal capitalization)
    {
        var stable = baselineCapitalization is not decimal baseline
            || baseline <= 0m
            || Math.Abs(capitalization - baseline) / baseline <= CapitalizationStabilityThreshold;
        var chance = stable ? StableCapitalizationChance : VolatileCapitalizationChance;
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
        decimal limitPrice)
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

        var participant = await dbContext.Participants.FirstOrDefaultAsync(candidate => candidate.Id == participantId);
        if (participant is null)
        {
            return PlaceOrderResult.Fail("Participant not found.");
        }

        if (!participant.IsActive)
        {
            return PlaceOrderResult.Fail("Participant is not active.");
        }

        if (!await dbContext.Companies.AnyAsync(company => company.Id == companyId))
        {
            return PlaceOrderResult.Fail("Company not found.");
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
            if (participant.AvailableBalance < reserved
                && participant.AvailableBalance + await DebtAllowanceAsync(participant) < reserved)
            {
                return PlaceOrderResult.Fail("Insufficient available cash to reserve for the buy order, even on the debt allowance.");
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

        await dbContext.SaveChangesAsync();
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

        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        if (currentCycle.Status == CycleStatus.Planned)
        {
            currentCycle.Status = CycleStatus.Running;
            currentCycle.StartedAt ??= now;
        }

        var fillCount = await matchingEngine.RunAsync(currentCycle);

        currentCycle.Status = CycleStatus.Completed;
        currentCycle.CompletedAt = now;

        await PayDividendsIfDueAsync(market, currentCycle, now);

        // Automated news is published on its cycle schedule and shares this transaction's save below.
        if (newsService is not null)
        {
            await newsService.MaybeAddAutomatedNewsForCycleAsync(currentCycle, now);
        }

        // A crisis may also strike this cycle, driving its hit sectors down and cancelling their stale bids.
        if (crisisService is not null)
        {
            await crisisService.MaybeTriggerForCycleAsync(market, currentCycle, now);
        }

        // A science investigation runs on its own clock and may lift a few sectors the same cycle a crisis hits.
        if (scienceService is not null)
        {
            await scienceService.MaybeTriggerForCycleAsync(market, currentCycle, now);
        }

        var nextCycle = new MarketCycle
        {
            CycleNumber = currentCycle.CycleNumber + 1,
            Status = CycleStatus.Running,
            StartedAt = now,
        };
        dbContext.MarketCycles.Add(nextCycle);
        await dbContext.SaveChangesAsync();

        // Runs once this cycle's fills and shocks are persisted so each trader's holdings reflect final prices;
        // the added rows flush with the market update below, inside the advance transaction.
        await WriteWorthSnapshotsAsync(currentCycle.Id, now);

        market.CurrentCycleId = nextCycle.Id;
        market.UpdatedAt = now;
        await dbContext.SaveChangesAsync();

        await transaction.CommitAsync();

        return AdvanceCycleResult.Ok(currentCycle.CycleNumber, fillCount);
    }

    // Records each trader's cash and holdings value for the just-completed cycle so per-cycle change figures
    // and the total-worth chart can be derived later. Every participant is captured, not just the human player.
    private async Task WriteWorthSnapshotsAsync(int completedCycleId, DateTime now)
    {
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
        if (order is null || order.ParticipantId != player.Id)
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

        CancelOrder(order, player, currentCycleId);
        await dbContext.SaveChangesAsync();
        return CancelOrderResult.Ok(order);
    }

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
        var baselineCycleNumber = cycleNumbersById.GetValueOrDefault(market.CurrentCycleId.Value) - LongRangeWindowCycles;

        var snapshots = await dbContext.PriceSnapshots.ToListAsync();
        var quotes = snapshots
            .GroupBy(snapshot => snapshot.CompanyId)
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
                    longRangeChangePct);
            })
            .ToList();

        if (quotes.Count == 0)
        {
            return RunDecisionsResult.Ok(0);
        }

        var traders = await dbContext.Participants
            .Where(participant => participant.IsActive
                && (participant.Type == ParticipantType.Individual
                    || participant.Type == ParticipantType.AIAgent
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
        var fundStatusByParticipantId = await dbContext.CollectiveFunds
            .ToDictionaryAsync(fund => fund.ParticipantId, fund => fund.Status);
        var memberParticipantIds = (await dbContext.CollectiveFundParticipants
                .Select(member => member.ParticipantId)
                .ToListAsync())
            .ToHashSet();

        var ordersPlaced = 0;

        foreach (var trader in traders)
        {
            // A fund that is winding down places no new orders; its forced sales are service-driven.
            if (trader.Type == ParticipantType.CollectiveFund
                && fundStatusByParticipantId.GetValueOrDefault(trader.Id) != CollectiveFundStatus.Active)
            {
                continue;
            }

            var context = new DecisionContext(
                trader,
                AvailableCashForDecisions(trader, memberParticipantIds, holdingsByOwner, priceByCompany),
                quotes,
                holdingsByOwner.GetValueOrDefault(trader.Id, NoHoldings),
                openOrdersByParticipant.GetValueOrDefault(trader.Id, NoOpenOrders));

            foreach (var intent in decisionEngine.Decide(context))
            {
                var result = await PlaceOrderCoreAsync(
                    trader.Id,
                    intent.CompanyId,
                    intent.Type,
                    intent.Quantity,
                    intent.LimitPrice);

                if (result.Success)
                {
                    ordersPlaced++;
                }
            }
        }

        return RunDecisionsResult.Ok(ordersPlaced);
    }

    // A fund member hands its buying to the fund — cash is zeroed so the engine can only sell its own shares.
    // Everyone else may size into a debt allowance on top of available cash; a fund still reserves a tenth of
    // its worth liquid for deposit returns before that borrowing headroom applies.
    private static decimal AvailableCashForDecisions(
        Participant trader,
        IReadOnlySet<int> memberParticipantIds,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> holdingsByOwner,
        IReadOnlyDictionary<int, decimal> priceByCompany)
    {
        if (memberParticipantIds.Contains(trader.Id))
        {
            return 0m;
        }

        var holdingsValue = holdingsByOwner.TryGetValue(trader.Id, out var holdings)
            ? holdings.Sum(holding => holding.Value * priceByCompany.GetValueOrDefault(holding.Key))
            : 0m;
        var debtAllowance = Math.Max(0m, DebtLimitFraction * (trader.CurrentBalance + holdingsValue));

        if (trader.Type != ParticipantType.CollectiveFund)
        {
            return trader.AvailableBalance + debtAllowance;
        }

        var totalWorth = trader.AvailableBalance + holdingsValue;
        return Math.Max(0m, trader.AvailableBalance - (CollectiveFundCashBufferFraction * totalWorth)) + debtAllowance;
    }

    private async Task<Market> ResetDemoMarketCoreAsync()
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        await dbContext.CrisisIndustries.ExecuteDeleteAsync();
        await dbContext.Crises.ExecuteDeleteAsync();
        await dbContext.ScienceInvestigationIndustries.ExecuteDeleteAsync();
        await dbContext.ScienceInvestigations.ExecuteDeleteAsync();
        await dbContext.Bankruptcies.ExecuteDeleteAsync();
        await dbContext.MarketExits.ExecuteDeleteAsync();
        await dbContext.CollectiveFundParticipants.ExecuteDeleteAsync();
        await dbContext.CollectiveFunds.ExecuteDeleteAsync();
        await dbContext.NewsPostIndustries.ExecuteDeleteAsync();
        await dbContext.NewsPosts.ExecuteDeleteAsync();
        await dbContext.OrderFills.ExecuteDeleteAsync();
        await dbContext.MoneyTransactions.ExecuteDeleteAsync();
        await dbContext.PriceSnapshots.ExecuteDeleteAsync();
        await dbContext.Holdings.ExecuteDeleteAsync();
        await dbContext.ShareTransactions.ExecuteDeleteAsync();
        await dbContext.Orders.ExecuteDeleteAsync();
        await dbContext.MarketCycles.ExecuteDeleteAsync();
        await dbContext.ParticipantWorthSnapshots.ExecuteDeleteAsync();
        await dbContext.Participants.ExecuteDeleteAsync();
        await dbContext.Companies.ExecuteDeleteAsync();
        await dbContext.Industries.ExecuteDeleteAsync();
        await dbContext.Markets.ExecuteDeleteAsync();

        dbContext.ChangeTracker.Clear();
        await dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM sqlite_sequence WHERE name IN (" +
            "'Companies', 'MarketCycles', 'Markets', 'Orders', 'Participants', " +
            "'ShareTransactions', 'MoneyTransactions', 'OrderFills', 'PriceSnapshots', 'Holdings', " +
            "'Industries', 'NewsPosts', 'NewsPostIndustries', 'Crises', 'CrisisIndustries', " +
            "'ScienceInvestigations', 'ScienceInvestigationIndustries', 'Bankruptcies', 'MarketExits', " +
            "'CollectiveFunds', 'CollectiveFundParticipants', 'ParticipantWorthSnapshots')");

        var market = await SeedDemoMarketCoreAsync();
        await transaction.CommitAsync();

        return market;
    }

    private async Task<Market> SeedDemoMarketCoreAsync()
    {
        // Tunable size of the generated demo market; bump these to grow the simulation.
        const int companyCount = 100;
        const int participantCount = 300;
        const int minShares = 100;
        const int maxShares = 1000;
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

        var firstCycle = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
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
                ReservedBalance = 0m,
                IsActive = true,
            });
        }

        var industries = DemoIndustries.Names
            .Select(name => new Industry { Name = name })
            .ToList();
        dbContext.Industries.AddRange(industries);
        await dbContext.SaveChangesAsync();
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

        // Drawn last so adding the dividend schedule does not shift the generated demo data above.
        market.NextDividendCycleNumber = firstCycle.CycleNumber + RandomDividendInterval(random);
        market.CurrentCycleId = firstCycle.Id;
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
}
