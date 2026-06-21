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

public sealed class MarketService(
    AppDbContext dbContext,
    MatchingEngine matchingEngine,
    IDecisionEngine decisionEngine,
    MarketCycleLock cycleLock,
    Random random,
    NewsService? newsService = null,
    CrisisService? crisisService = null)
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

    // Dividends are paid at a random interval drawn in this range and credited at a per-company rate
    // drawn in [MinDividendRate, MaxDividendRate] of the share's current price.
    private const int MinDividendIntervalCycles = 10;
    private const int MaxDividendIntervalCycles = 25;
    private const decimal MinDividendRate = 0.001m;
    private const decimal MaxDividendRate = 0.02m;

    // Forced-liquidation sells undercut the market by 1–5% so the order actually crosses.
    private const decimal MinSellOffset = 0.01m;
    private const decimal MaxSellOffset = 0.05m;

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
            .Include(order => order.OrderShares)
            .ToListAsync();

        var participantsById = await dbContext.Participants.ToDictionaryAsync(participant => participant.Id);

        foreach (var order in openOrders)
        {
            var participant = participantsById[order.ParticipantId!.Value];
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

        await LiquidateStarvedHoldersAsync(participantsById);

        await dbContext.SaveChangesAsync();
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
        else
        {
            // The unsold share links return to the owner so the shares can be listed again.
            dbContext.OrderShares.RemoveRange(order.OrderShares);
            order.OrderShares.Clear();
        }

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

        // Owned shares not already listed in an open order, grouped by owner then company.
        var offeredShareIds = (await dbContext.OrderShares.Select(orderShare => orderShare.ShareId).ToListAsync())
            .ToHashSet();
        var availableHoldings = (await dbContext.Shares
                .Where(share => share.OwnerId != null)
                .Select(share => new { OwnerId = share.OwnerId!.Value, share.CompanyId, share.Id })
                .ToListAsync())
            .Where(share => !offeredShareIds.Contains(share.Id))
            .GroupBy(share => share.OwnerId)
            .ToDictionary(
                ownerGroup => ownerGroup.Key,
                ownerGroup => ownerGroup
                    .GroupBy(share => share.CompanyId)
                    .ToDictionary(companyGroup => companyGroup.Key, companyGroup => companyGroup.Count()));

        var traders = participantsById.Values
            .Where(participant => participant.IsActive
                && (participant.Type == ParticipantType.Individual || participant.Type == ParticipantType.AIAgent));

        foreach (var trader in traders)
        {
            if (trader.AvailableBalance >= cheapestShare)
            {
                trader.CashStarvedCycles = 0;
                continue;
            }

            trader.CashStarvedCycles++;
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

    private async Task PayDividendsAsync(int currentCycleId, DateTime now)
    {
        var latestPriceByCompany = await LatestPriceByCompanyAsync();
        if (latestPriceByCompany.Count == 0)
        {
            return;
        }

        // Each company declares its own rate this round, so all its shares pay the same per-share amount.
        var rateByCompany = latestPriceByCompany.Keys.ToDictionary(companyId => companyId, _ => RandomDividendRate());

        var holdings = await dbContext.Shares
            .Where(share => share.OwnerId != null)
            .GroupBy(share => new { OwnerId = share.OwnerId!.Value, share.CompanyId })
            .Select(group => new { group.Key.OwnerId, group.Key.CompanyId, Count = group.Count() })
            .ToListAsync();

        var participantsById = await dbContext.Participants.ToDictionaryAsync(participant => participant.Id);

        foreach (var ownerGroup in holdings.GroupBy(holding => holding.OwnerId))
        {
            if (!participantsById.TryGetValue(ownerGroup.Key, out var owner))
            {
                continue;
            }

            var payout = ownerGroup.Sum(holding =>
                latestPriceByCompany[holding.CompanyId] * rateByCompany[holding.CompanyId] * holding.Count);
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
        }
    }

    private async Task<IReadOnlyDictionary<int, decimal>> LatestPriceByCompanyAsync()
    {
        var snapshots = await dbContext.PriceSnapshots.ToListAsync();
        return snapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.Id).First().Price);
    }

    private static int RandomDividendInterval(Random rng) =>
        rng.Next(MinDividendIntervalCycles, MaxDividendIntervalCycles + 1);

    private decimal RandomDividendRate() =>
        MinDividendRate + ((decimal)random.NextDouble() * (MaxDividendRate - MinDividendRate));

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
            if (participant.AvailableBalance < reserved)
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
                CreatedInCycleId = cycleId,
                CreatedAt = now,
            });
        }
        else
        {
            var ownedShareIds = await dbContext.Shares
                .Where(share => share.OwnerId == participantId && share.CompanyId == companyId)
                .Select(share => share.Id)
                .ToListAsync();

            var offeredShareIds = await dbContext.OrderShares
                .Where(orderShare => ownedShareIds.Contains(orderShare.ShareId))
                .Select(orderShare => orderShare.ShareId)
                .ToListAsync();

            var availableShareIds = ownedShareIds.Except(offeredShareIds).Take(quantity).ToList();
            if (availableShareIds.Count < quantity)
            {
                return PlaceOrderResult.Fail("Not enough available shares to sell.");
            }

            dbContext.Orders.Add(order);
            foreach (var shareId in availableShareIds)
            {
                dbContext.OrderShares.Add(new OrderShare { Order = order, ShareId = shareId });
            }
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

        var nextCycle = new MarketCycle
        {
            CycleNumber = currentCycle.CycleNumber + 1,
            Status = CycleStatus.Running,
            StartedAt = now,
        };
        dbContext.MarketCycles.Add(nextCycle);
        await dbContext.SaveChangesAsync();

        market.CurrentCycleId = nextCycle.Id;
        market.UpdatedAt = now;
        await dbContext.SaveChangesAsync();

        await transaction.CommitAsync();

        return AdvanceCycleResult.Ok(currentCycle.CycleNumber, fillCount);
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
                group => group.Sum(order => order.Type == OrderType.Buy ? order.Remaining : -order.Remaining));

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
                    netDemandByCompany.GetValueOrDefault(group.Key),
                    longRangeChangePct);
            })
            .ToList();

        if (quotes.Count == 0)
        {
            return RunDecisionsResult.Ok(0);
        }

        var traders = await dbContext.Participants
            .Where(participant => participant.IsActive
                && (participant.Type == ParticipantType.Individual || participant.Type == ParticipantType.AIAgent))
            .OrderBy(participant => participant.Id)
            .ToListAsync();

        var holdingsByOwner = (await dbContext.Shares
                .Where(share => share.OwnerId != null)
                .Select(share => new { OwnerId = share.OwnerId!.Value, share.CompanyId })
                .ToListAsync())
            .GroupBy(share => share.OwnerId)
            .ToDictionary(
                ownerGroup => ownerGroup.Key,
                ownerGroup => (IReadOnlyDictionary<int, int>)ownerGroup
                    .GroupBy(share => share.CompanyId)
                    .ToDictionary(companyGroup => companyGroup.Key, companyGroup => companyGroup.Count()));

        var openOrdersByParticipant = (await dbContext.Orders
                .Where(order => (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
                    && order.ParticipantId != null)
                .Select(order => new { ParticipantId = order.ParticipantId!.Value, order.CompanyId })
                .ToListAsync())
            .GroupBy(order => order.ParticipantId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<int>)group.Select(order => order.CompanyId).ToHashSet());

        var ordersPlaced = 0;

        foreach (var trader in traders)
        {
            var context = new DecisionContext(
                trader,
                trader.AvailableBalance,
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

    private async Task<Market> ResetDemoMarketCoreAsync()
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        await dbContext.CrisisIndustries.ExecuteDeleteAsync();
        await dbContext.Crises.ExecuteDeleteAsync();
        await dbContext.NewsPostIndustries.ExecuteDeleteAsync();
        await dbContext.NewsPosts.ExecuteDeleteAsync();
        await dbContext.OrderShares.ExecuteDeleteAsync();
        await dbContext.OrderFills.ExecuteDeleteAsync();
        await dbContext.MoneyTransactions.ExecuteDeleteAsync();
        await dbContext.PriceSnapshots.ExecuteDeleteAsync();
        await dbContext.Shares.ExecuteDeleteAsync();
        await dbContext.ShareTransactions.ExecuteDeleteAsync();
        await dbContext.Orders.ExecuteDeleteAsync();
        await dbContext.MarketCycles.ExecuteDeleteAsync();
        await dbContext.Participants.ExecuteDeleteAsync();
        await dbContext.Companies.ExecuteDeleteAsync();
        await dbContext.Industries.ExecuteDeleteAsync();
        await dbContext.Markets.ExecuteDeleteAsync();

        dbContext.ChangeTracker.Clear();
        await dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM sqlite_sequence WHERE name IN (" +
            "'Companies', 'MarketCycles', 'Markets', 'Orders', 'Participants', " +
            "'ShareTransactions', 'MoneyTransactions', 'OrderFills', 'PriceSnapshots', 'Shares', 'OrderShares', " +
            "'Industries', 'NewsPosts', 'NewsPostIndustries', 'Crises', 'CrisisIndustries')");

        var market = await SeedDemoMarketCoreAsync();
        await transaction.CommitAsync();

        return market;
    }

    private async Task<Market> SeedDemoMarketCoreAsync()
    {
        // Tunable size of the generated demo market; bump these to grow the simulation.
        const int companyCount = 40;
        const int participantCount = 200;
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
                Type = index % 2 == 0 ? ParticipantType.Individual : ParticipantType.AIAgent,
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

                // Every issued share starts unowned and is listed in a single company-originated sell
                // order, so all shares are immediately available for participants to buy.
                var sellOrder = new Order
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
                };

                for (var shareIndex = 0; shareIndex < shareCount; shareIndex++)
                {
                    sellOrder.OrderShares.Add(new OrderShare
                    {
                        Share = new Share
                        {
                            CompanyId = company.Id,
                            OwnerId = null,
                            InitialPrice = price,
                            CurrentPrice = price,
                            LastUpdatedAt = now,
                        },
                    });
                }

                dbContext.Orders.Add(sellOrder);

                dbContext.PriceSnapshots.Add(new PriceSnapshot
                {
                    CompanyId = company.Id,
                    Price = price,
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
