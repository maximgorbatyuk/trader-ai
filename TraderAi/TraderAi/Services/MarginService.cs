using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record MarginMetrics(
    decimal GrossMarketValue,
    decimal AccountEquity,
    decimal DebitBalance,
    decimal AccruedInterest,
    decimal BuyingPower,
    decimal InitialMarginRate,
    decimal MaintenanceMarginRate,
    decimal InitialRequirement,
    decimal MaintenanceRequirement,
    decimal MaintenanceExcess,
    decimal Deficiency,
    string? CallStatus);

public readonly record struct MarginSaleAllocation(decimal InterestPaid, decimal DebitPaid, decimal FreeCash);

public sealed class MarginService(AppDbContext dbContext, IOptions<MarginOptions> options)
{
    private readonly MarginOptions settings = options.Value;

    public async Task<MarginAccount> GetOrCreateAccountAsync(int participantId, int? currentTradingDayId = null)
    {
        var existing = await dbContext.MarginAccounts.FirstOrDefaultAsync(account => account.ParticipantId == participantId);
        if (existing is not null)
        {
            return existing;
        }

        var account = new MarginAccount
        {
            ParticipantId = participantId,
            InitialMarginRate = settings.InitialMarginRate,
            MaintenanceMarginRate = settings.MaintenanceMarginRate,
            Status = MarginAccountStatus.Active,
            LastInterestAccruedTradingDayId = currentTradingDayId,
        };
        dbContext.MarginAccounts.Add(account);
        await dbContext.SaveChangesAsync();
        return account;
    }

    public async Task<decimal> GetBuyingPowerAsync(int participantId, IReadOnlyDictionary<int, decimal>? prices = null)
    {
        var participant = await dbContext.Participants.SingleAsync(candidate => candidate.Id == participantId);
        var account = await GetOrCreateAccountAsync(participantId, await CurrentTradingDayIdAsync());
        var gross = await GrossMarketValueAsync(participantId, prices);
        return BuyingPower(participant, account, gross);
    }

    public async Task<MarginMetrics> GetMetricsAsync(int participantId, IReadOnlyDictionary<int, decimal>? prices = null)
    {
        var participant = await dbContext.Participants.SingleAsync(candidate => candidate.Id == participantId);
        var account = await GetOrCreateAccountAsync(participantId, await CurrentTradingDayIdAsync());
        var gross = await GrossMarketValueAsync(participantId, prices);
        return BuildMetrics(participant, account, gross, await OpenCallStatusAsync(account));
    }

    public async Task<MarginMetrics> GetReadOnlyMetricsAsync(Participant participant, decimal grossMarketValue)
    {
        var account = await dbContext.MarginAccounts
            .FirstOrDefaultAsync(candidate => candidate.ParticipantId == participant.Id);
        return BuildMetrics(participant, account, grossMarketValue, await OpenCallStatusAsync(account));
    }

    public async Task<Dictionary<int, MarginMetrics>> GetReadOnlyMetricsByParticipantAsync(
        IReadOnlyCollection<Participant> participants,
        IReadOnlyDictionary<int, decimal> grossMarketValueByParticipant)
    {
        if (participants.Count == 0)
        {
            return [];
        }

        var participantIds = participants.Select(participant => participant.Id).ToList();
        var accounts = await dbContext.MarginAccounts
            .Where(account => participantIds.Contains(account.ParticipantId))
            .ToDictionaryAsync(account => account.ParticipantId);
        var accountIds = accounts.Values.Select(account => account.Id).ToList();
        var openCalls = await dbContext.MarginCalls
            .Where(call => accountIds.Contains(call.MarginAccountId) && call.Status == MarginCallStatus.Open)
            .Select(call => new { call.MarginAccountId, call.Status })
            .ToListAsync();
        var callStatusByAccount = openCalls
            .GroupBy(call => call.MarginAccountId)
            .ToDictionary(group => group.Key, group => group.First().Status.ToString());

        return participants.ToDictionary(
            participant => participant.Id,
            participant =>
            {
                accounts.TryGetValue(participant.Id, out var account);
                var callStatus = account is null ? null : callStatusByAccount.GetValueOrDefault(account.Id);
                return BuildMetrics(
                    participant,
                    account,
                    grossMarketValueByParticipant.GetValueOrDefault(participant.Id),
                    callStatus);
            });
    }

    private MarginMetrics BuildMetrics(Participant participant, MarginAccount? account, decimal gross, string? callStatus)
    {
        var debit = account?.DebitBalance ?? 0m;
        var interest = account?.AccruedInterest ?? 0m;
        var liability = debit + interest;
        var initialRate = account?.InitialMarginRate ?? settings.InitialMarginRate;
        var maintenanceRate = account?.MaintenanceMarginRate ?? settings.MaintenanceMarginRate;
        var status = account?.Status ?? MarginAccountStatus.Active;
        var equity = participant.CurrentBalance + gross - liability;
        var initial = gross * initialRate;
        var maintenance = gross * maintenanceRate;
        var excess = equity - maintenance;
        return new MarginMetrics(
            gross, equity, debit, interest,
            BuyingPower(participant, liability, initialRate, status, gross), initialRate, maintenanceRate,
            initial, maintenance, excess, Math.Max(0m, -excess), callStatus);
    }

    private Task<string?> OpenCallStatusAsync(MarginAccount? account) =>
        account is null
            ? Task.FromResult<string?>(null)
            : dbContext.MarginCalls
                .Where(candidate => candidate.MarginAccountId == account.Id && candidate.Status == MarginCallStatus.Open)
                .Select(candidate => candidate.Status.ToString())
                .FirstOrDefaultAsync();

    public decimal ApplyPurchase(
        MarginAccount account,
        Participant buyer,
        decimal spent,
        decimal reservationForFilled,
        int cycleId,
        DateTime now)
    {
        var otherReservations = Math.Max(0m, buyer.ReservedBalance - reservationForFilled);
        var cashAvailable = Math.Max(0m, buyer.CurrentBalance - otherReservations);
        var advance = Round(Math.Max(0m, spent - cashAvailable));
        if (advance <= 0m)
        {
            return 0m;
        }

        account.DebitBalance = Round(account.DebitBalance + advance);
        buyer.CurrentBalance += advance;
        buyer.SettledCashBalance += advance;
        dbContext.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = buyer.Id,
            Type = MoneyTransactionType.MarginAdvance,
            Amount = advance,
            Description = "Margin advance to cover purchase",
            CreatedInCycleId = cycleId,
            CreatedAt = now,
        });
        return advance;
    }

    public MarginSaleAllocation ApplySaleProceeds(
        MarginAccount account,
        Participant seller,
        decimal proceeds,
        int cycleId,
        DateTime now)
    {
        var remaining = proceeds;
        var interest = Take(ref remaining, account.AccruedInterest);
        account.AccruedInterest = Round(account.AccruedInterest - interest);
        var debit = Take(ref remaining, account.DebitBalance);
        account.DebitBalance = Round(account.DebitBalance - debit);
        seller.CurrentBalance += remaining;
        seller.SettledCashBalance -= interest + debit;

        if (interest > 0m)
        {
            AddTransaction(seller.Id, MoneyTransactionType.MarginInterestPayment, interest, cycleId, now);
        }
        if (debit > 0m)
        {
            AddTransaction(seller.Id, MoneyTransactionType.MarginDebitRepayment, debit, cycleId, now);
        }
        return new MarginSaleAllocation(interest, debit, remaining);
    }

    public async Task ProcessForTradingDayAsync(int tradingDayId, int currentCycleId, DateTime now)
    {
        if (!settings.Enabled)
        {
            return;
        }

        var participants = await dbContext.Participants.Where(participant => participant.IsActive).ToListAsync();
        foreach (var participant in participants)
        {
            var account = await GetOrCreateAccountAsync(participant.Id, tradingDayId);
            if (account.LastInterestAccruedTradingDayId is null)
            {
                account.LastInterestAccruedTradingDayId = tradingDayId;
            }
            else if (account.LastInterestAccruedTradingDayId != tradingDayId)
            {
                account.AccruedInterest = Round(account.AccruedInterest + (account.DebitBalance * settings.DailyInterestRate));
                account.LastInterestAccruedTradingDayId = tradingDayId;
            }
        }
        await dbContext.SaveChangesAsync();

        var prices = await PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);
        var bandByCompany = await dbContext.PriceBandStates.ToDictionaryAsync(state => state.CompanyId);
        foreach (var participant in participants)
        {
            var account = await dbContext.MarginAccounts.SingleAsync(candidate => candidate.ParticipantId == participant.Id);
            var gross = await GrossMarketValueAsync(participant.Id, prices);
            var equity = participant.CurrentBalance + gross - account.TotalLiability;
            var requirement = gross * account.MaintenanceMarginRate;
            var deficiency = Math.Max(0m, requirement - equity);
            var call = await dbContext.MarginCalls.FirstOrDefaultAsync(candidate =>
                candidate.MarginAccountId == account.Id && candidate.Status == MarginCallStatus.Open);
            if (deficiency <= 0m)
            {
                if (call is not null)
                {
                    call.Status = MarginCallStatus.Satisfied;
                    call.ClosedInTradingDayId = tradingDayId;
                    call.ClosedAt = now;
                }
                account.Status = MarginAccountStatus.Active;
                continue;
            }

            if (call is null)
            {
                call = new MarginCall
                {
                    MarginAccountId = account.Id,
                    OpenedInTradingDayId = tradingDayId,
                    OpenedInCycleId = currentCycleId,
                    Status = MarginCallStatus.Open,
                    CreatedAt = now,
                };
                dbContext.MarginCalls.Add(call);
                await dbContext.SaveChangesAsync();
            }
            call.AccountEquity = equity;
            call.MaintenanceRequirement = requirement;
            call.Deficiency = deficiency;
            account.Status = MarginAccountStatus.UnderCall;

            if (await dbContext.Orders.AnyAsync(order => order.RelatedMarginCallId == call.Id
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)))
            {
                continue;
            }

            var targetRate = account.MaintenanceMarginRate + settings.MaintenanceBufferRate;
            var targetSaleValue = targetRate > 0m ? Math.Max(0m, gross - (equity / targetRate)) : 0m;
            var holdings = await dbContext.Holdings
                .Where(holding => holding.ParticipantId == participant.Id && holding.SettledQuantity > 0)
                .ToListAsync();
            var committedSellQuantityByCompany = (await dbContext.Orders
                    .Where(order => order.ParticipantId == participant.Id
                        && order.Type == OrderType.Sell
                        && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
                    .ToListAsync())
                .GroupBy(order => order.CompanyId)
                .ToDictionary(group => group.Key, group => group.Sum(order => order.RemainingQuantity));
            var raised = 0m;
            foreach (var holding in holdings.OrderByDescending(holding => prices.GetValueOrDefault(holding.CompanyId)))
            {
                var price = prices.GetValueOrDefault(holding.CompanyId);
                if (price <= 0m || raised >= targetSaleValue)
                {
                    continue;
                }
                var remainingValue = targetSaleValue - raised;
                var uncommittedQuantity = Math.Max(
                    0,
                    holding.SettledQuantity - committedSellQuantityByCompany.GetValueOrDefault(holding.CompanyId));
                var quantity = Math.Min(uncommittedQuantity, (int)Math.Ceiling(remainingValue / price));
                if (quantity <= 0)
                {
                    continue;
                }

                // A margin call must actually raise cash, so its ask is pulled onto the active band rather than
                // resting outside it where matching would never cross.
                var askPrice = Round(price * (1m - settings.ForcedSaleDiscountRate));
                if (bandByCompany.GetValueOrDefault(holding.CompanyId) is { } band)
                {
                    askPrice = band.ClampToActiveBand(askPrice);
                }

                dbContext.Orders.Add(new Order
                {
                    ParticipantId = participant.Id,
                    CompanyId = holding.CompanyId,
                    Type = OrderType.Sell,
                    Status = OrderStatus.Open,
                    Quantity = quantity,
                    LimitPrice = askPrice,
                    RelatedMarginCallId = call.Id,
                    CreatedInCycleId = currentCycleId,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                committedSellQuantityByCompany[holding.CompanyId] =
                    committedSellQuantityByCompany.GetValueOrDefault(holding.CompanyId) + quantity;
                raised += quantity * price;
            }
        }
        await dbContext.SaveChangesAsync();
    }

    public static async Task<Dictionary<int, decimal>> LiabilityByParticipantAsync(AppDbContext dbContext) =>
        await dbContext.MarginAccounts.ToDictionaryAsync(account => account.ParticipantId, account => account.DebitBalance + account.AccruedInterest);

    private async Task<decimal> GrossMarketValueAsync(int participantId, IReadOnlyDictionary<int, decimal>? prices)
    {
        var priceMap = prices ?? await PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);
        var holdings = await dbContext.Holdings
            .Where(holding => holding.ParticipantId == participantId && holding.Quantity > 0)
            .Select(holding => new { holding.CompanyId, holding.Quantity })
            .ToListAsync();
        return holdings.Sum(holding => holding.Quantity * priceMap.GetValueOrDefault(holding.CompanyId));
    }

    private decimal BuyingPower(Participant participant, MarginAccount account, decimal gross)
        => BuyingPower(participant, account.TotalLiability, account.InitialMarginRate, account.Status, gross);

    private decimal BuyingPower(
        Participant participant,
        decimal liability,
        decimal initialMarginRate,
        MarginAccountStatus status,
        decimal gross)
    {
        if (!settings.Enabled || status == MarginAccountStatus.Closed || initialMarginRate <= 0m)
        {
            return Math.Max(0m, participant.AvailableBalance);
        }
        var equity = participant.CurrentBalance + gross - liability;
        return Math.Max(0m, (equity / initialMarginRate) - gross - participant.ReservedBalance);
    }

    private async Task<int?> CurrentTradingDayIdAsync() =>
        await dbContext.Markets.Select(market => market.CurrentTradingDayId).FirstOrDefaultAsync();

    private void AddTransaction(int participantId, MoneyTransactionType type, decimal amount, int cycleId, DateTime now) =>
        dbContext.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = participantId,
            Type = type,
            Amount = amount,
            Description = type switch
            {
                MoneyTransactionType.MarginInterestPayment => "Margin interest paid to broker",
                MoneyTransactionType.MarginDebitRepayment => "Margin debit repaid to broker",
                _ => null,
            },
            CreatedInCycleId = cycleId,
            CreatedAt = now,
        });

    private static decimal Take(ref decimal available, decimal due)
    {
        var amount = Math.Min(available, Math.Max(0m, due));
        available -= amount;
        return amount;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
