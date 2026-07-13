using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed class SettlementService(AppDbContext dbContext, IOptions<SettlementOptions> options)
{
    private readonly SettlementOptions settings = options.Value;

    public async Task<int> TradeDayNumberAsync(MarketCycle cycle)
    {
        if (cycle.TradingDayId <= 0)
        {
            throw new InvalidOperationException("A settlement instruction requires a trading day.");
        }

        return await dbContext.TradingDays
            .Where(day => day.Id == cycle.TradingDayId)
            .Select(day => day.DayNumber)
            .SingleAsync();
    }

    public void StageInstruction(
        ShareTransaction transaction,
        int tradeDayNumber,
        int createdInCycleId,
        DateTime now,
        decimal buyerMarginAdvance = 0m,
        decimal sellerMarginInterestPayment = 0m,
        decimal sellerMarginDebitRepayment = 0m)
    {
        dbContext.SettlementInstructions.Add(new SettlementInstruction
        {
            ShareTransaction = transaction,
            BuyerId = transaction.BuyerId,
            SellerId = transaction.SellerId,
            CompanyId = transaction.CompanyId,
            Quantity = transaction.Quantity,
            CashAmount = transaction.TotalCost,
            BuyerMarginAdvance = buyerMarginAdvance,
            SellerMarginInterestPayment = sellerMarginInterestPayment,
            SellerMarginDebitRepayment = sellerMarginDebitRepayment,
            TradeDayNumber = tradeDayNumber,
            DueDayNumber = tradeDayNumber + settings.SettlementLagTradingDays,
            Status = SettlementStatus.Pending,
            CreatedInCycleId = createdInCycleId,
            CreatedAt = now,
        });
    }

    public async Task<int> SettleDueAsync(int tradingDayNumber, int settledInCycleId, DateTime now)
    {
        var instructions = (await dbContext.SettlementInstructions
                .Where(instruction => instruction.Status == SettlementStatus.Pending
                    && instruction.DueDayNumber <= tradingDayNumber)
                .OrderBy(instruction => instruction.Id)
                .ToListAsync())
            // A second call before SaveChanges can be returned from EF's tracked identity map even though the
            // database predicate still sees Pending; the in-memory filter keeps that retry idempotent too.
            .Where(instruction => instruction.Status == SettlementStatus.Pending)
            .ToList();
        if (instructions.Count == 0)
        {
            return 0;
        }

        var participantIds = instructions
            .SelectMany(instruction => instruction.SellerId is int sellerId
                ? new[] { instruction.BuyerId, sellerId }
                : new[] { instruction.BuyerId })
            .Distinct()
            .ToList();
        var participants = await dbContext.Participants
            .Where(participant => participantIds.Contains(participant.Id))
            .ToDictionaryAsync(participant => participant.Id);
        if (participants.Count != participantIds.Count)
        {
            throw new InvalidOperationException("A pending settlement participant no longer exists.");
        }

        var cashDeltas = new Dictionary<int, decimal>();
        var quantityDeltas = new Dictionary<(int ParticipantId, int CompanyId), int>();
        var issuerCash = new Dictionary<int, decimal>();
        foreach (var instruction in instructions)
        {
            Add(cashDeltas, instruction.BuyerId, -instruction.CashAmount);
            Add(quantityDeltas, (instruction.BuyerId, instruction.CompanyId), instruction.Quantity);
            if (instruction.SellerId is int sellerId)
            {
                Add(cashDeltas, sellerId, instruction.CashAmount);
                Add(quantityDeltas, (sellerId, instruction.CompanyId), -instruction.Quantity);
            }
            else
            {
                Add(issuerCash, instruction.CompanyId, instruction.CashAmount);
            }
        }

        foreach (var (participantId, delta) in cashDeltas)
        {
            participants[participantId].SettledCashBalance += delta;
        }

        var positionKeys = quantityDeltas.Keys.ToList();
        var positionParticipantIds = positionKeys.Select(key => key.ParticipantId).Distinct().ToList();
        var positionCompanyIds = positionKeys.Select(key => key.CompanyId).Distinct().ToList();
        var holdings = await dbContext.Holdings
            .Where(holding => positionParticipantIds.Contains(holding.ParticipantId)
                && positionCompanyIds.Contains(holding.CompanyId))
            .ToDictionaryAsync(holding => (holding.ParticipantId, holding.CompanyId));
        foreach (var (key, delta) in quantityDeltas)
        {
            if (!holdings.TryGetValue(key, out var holding))
            {
                throw new InvalidOperationException("A pending settlement holding no longer exists.");
            }

            var settled = holding.SettledQuantity + delta;
            if (settled < 0)
            {
                throw new InvalidOperationException("Settlement would create a negative settled position.");
            }

            holding.SettledQuantity = settled;
        }

        if (issuerCash.Count > 0)
        {
            var companyIds = issuerCash.Keys.ToList();
            var companies = await dbContext.Companies
                .Where(company => companyIds.Contains(company.Id))
                .ToDictionaryAsync(company => company.Id);
            foreach (var (companyId, amount) in issuerCash)
            {
                if (amount <= 0m)
                {
                    throw new InvalidOperationException("Issuer cash settlement must be positive.");
                }

                var company = companies.GetValueOrDefault(companyId)
                    ?? throw new InvalidOperationException("A pending issuer settlement company no longer exists.");
                company.CashBalance += amount;
                dbContext.CorporateCashTransactions.Add(new CorporateCashTransaction
                {
                    CompanyId = companyId,
                    Type = CorporateCashTransactionType.PrimaryIssuance,
                    Amount = amount,
                    CreatedInCycleId = settledInCycleId,
                    CreatedAt = now,
                });
            }
        }

        foreach (var instruction in instructions)
        {
            instruction.Status = SettlementStatus.Settled;
            instruction.SettledInCycleId = settledInCycleId;
            instruction.SettledAt = now;
        }

        return instructions.Count;
    }

    private static void Add<TKey>(Dictionary<TKey, decimal> values, TKey key, decimal amount) where TKey : notnull =>
        values[key] = values.GetValueOrDefault(key) + amount;

    private static void Add<TKey>(Dictionary<TKey, int> values, TKey key, int amount) where TKey : notnull =>
        values[key] = values.GetValueOrDefault(key) + amount;
}
