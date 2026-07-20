using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Matches open buy and sell orders for a cycle and records the resulting share transfers, money
// movements, and price snapshots. Orders pair by price-time priority, but each cross executes at the
// midpoint of the two limits. Mutations are tracked on the shared DbContext; the caller owns saving and
// the surrounding transaction.
//
// When trade fees are enabled, a participant seller's proceeds are skimmed by TradeFeeOptions.FeeRate and
// the fee accrues to the National bank's balance. Company-float (primary-issuance) sells carry no fee, so
// the fee only ever touches secondary-market cash and the ledger stays reconciled.
//
// When the collective-fund manager profit fee is enabled, a fund seller's realized gain on each fill is split:
// the founder is paid ManagerProfitFeeShare of the gain at once, funded by debiting the fund, and the rest of
// the gain stays in the fund. A loss or break-even fill pays nothing.
public sealed class MatchingEngine(
    AppDbContext dbContext,
    IOptions<TradeFeeOptions>? tradeFeeOptions = null,
    SettlementService? settlementService = null,
    MarginService? marginService = null,
    IOptions<CollectiveFundOptions>? collectiveFundOptions = null)
{
    private readonly bool feeEnabled = tradeFeeOptions?.Value.Enabled ?? false;
    private readonly decimal feeRate = tradeFeeOptions?.Value.FeeRate ?? 0m;
    private readonly string feeBankName = tradeFeeOptions?.Value.BankName ?? "National bank";

    private readonly bool managerProfitFeeEnabled = collectiveFundOptions?.Value.ManagerProfitFeeEnabled ?? false;
    private readonly decimal managerProfitFeeShare = collectiveFundOptions?.Value.ManagerProfitFeeShare ?? 0m;

    // Resolved once per run only when a participant sell could be charged, so a fee-disabled book (or one
    // with only company-float sells) never touches the Banks table.
    private Bank? feeBank;

    // Active funds keyed by their trading participant id, loaded once per run only when the manager profit fee
    // is enabled and a participant sell could be a fund sale, so an ordinary book never touches the table.
    private Dictionary<int, CollectiveFund>? fundsBySellerId;

    public async Task<int> RunAsync(MarketCycle cycle, bool holdOrdersCreatedThisCycle = false)
    {
        var now = DateTime.UtcNow;
        var tradeDayNumber = settlementService is null || cycle.TradingDayId <= 0
            ? (int?)null
            : await settlementService.TradeDayNumberAsync(cycle);
        var participants = await dbContext.Participants.ToDictionaryAsync(participant => participant.Id);

        // Positions of everyone who might trade this cycle; buyers acquiring their first shares of a
        // company get a fresh row added on the fly.
        var holdings = await dbContext.Holdings.ToDictionaryAsync(holding => (holding.ParticipantId, holding.CompanyId));

        // Companies stay tracked so primary fills can retain their proceeds without another query per match.
        var companiesById = await dbContext.Companies.ToDictionaryAsync(company => company.Id);

        var priceBandByCompany = await dbContext.PriceBandStates.ToDictionaryAsync(state => state.CompanyId);

        var openOrders = await dbContext.Orders
            .Where(order => order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled)
            .ToListAsync();

        // An order rests at least one cycle before it can cross, so an order born this cycle is visible in
        // the book to the player and other participants before it becomes matchable. This also excludes it
        // from the reopening auction below, which draws from the same order set.
        if (holdOrdersCreatedThisCycle)
        {
            openOrders = openOrders.Where(order => order.CreatedInCycleId != cycle.Id).ToList();
        }

        if (feeEnabled && feeRate > 0m
            && openOrders.Any(order => order.Type == OrderType.Sell && order.ParticipantId != null))
        {
            feeBank = await ResolveFeeBankAsync();
        }

        if (managerProfitFeeEnabled && managerProfitFeeShare > 0m
            && openOrders.Any(order => order.Type == OrderType.Sell && order.ParticipantId != null))
        {
            fundsBySellerId = await dbContext.CollectiveFunds
                .Where(fund => fund.Status == CollectiveFundStatus.Active)
                .ToDictionaryAsync(fund => fund.ParticipantId);
        }

        var fillCount = 0;

        foreach (var companyOrders in openOrders.GroupBy(order => order.CompanyId))
        {
            priceBandByCompany.TryGetValue(companyOrders.Key, out var priceBand);
            if (priceBand?.State is LuldState.LimitState or LuldState.TradingPause)
            {
                continue;
            }

            if (priceBand?.State == LuldState.Reopening)
            {
                fillCount += await RunReopeningAuctionAsync(
                    companyOrders.ToList(), priceBand, cycle, tradeDayNumber,
                    participants, holdings, companiesById, now);
                VolatilityHaltService.ResetToNormal(priceBand, cycle.Id);
                continue;
            }

            var buys = companyOrders
                .Where(order => order.Type == OrderType.Buy && order.RemainingQuantity > 0)
                .Where(order => IsInsideBand(order.LimitPrice, priceBand))
                .OrderByDescending(order => order.LimitPrice)
                .ThenBy(order => order.CreatedAt)
                .ThenBy(order => order.Id)
                .ToList();

            var sells = companyOrders
                .Where(order => order.Type == OrderType.Sell && order.RemainingQuantity > 0)
                .Where(order => IsInsideBand(order.LimitPrice, priceBand))
                .OrderBy(order => order.LimitPrice)
                .ThenBy(order => order.CreatedAt)
                .ThenBy(order => order.Id)
                .ToList();

            var buyIndex = 0;
            var sellIndex = 0;

            while (buyIndex < buys.Count && sellIndex < sells.Count)
            {
                var buy = buys[buyIndex];
                var sell = sells[sellIndex];

                // Best remaining buy cannot meet the cheapest remaining sell, so no further crosses exist.
                if (buy.LimitPrice < sell.LimitPrice)
                {
                    break;
                }

                if (buy.ParticipantId is int buyerId && buyerId == sell.ParticipantId)
                {
                    var newerOrder = buy.CreatedAt > sell.CreatedAt
                        || (buy.CreatedAt == sell.CreatedAt && buy.Id > sell.Id)
                            ? buy
                            : sell;
                    CancelSelfCross(newerOrder, participants[buyerId], cycle.Id, now);
                    if (ReferenceEquals(newerOrder, buy))
                    {
                        buyIndex++;
                    }
                    else
                    {
                        sellIndex++;
                    }

                    continue;
                }

                var matchQuantity = Math.Min(buy.RemainingQuantity, sell.RemainingQuantity);

                // Crossing guarantees the midpoint sits at or below the buyer's limit and at or above the
                // seller's, so the buyer's unused reservation is still refunded below.
                var executionPrice = Round((buy.LimitPrice + sell.LimitPrice) / 2m);

                await ExecuteFillAsync(
                    buy, sell, matchQuantity, executionPrice, cycle, tradeDayNumber,
                    participants, holdings, companiesById, now);
                fillCount++;

                if (buy.RemainingQuantity == 0)
                {
                    buyIndex++;
                }

                if (sell.RemainingQuantity == 0)
                {
                    sellIndex++;
                }
            }
        }

        return fillCount;
    }

    private async Task<int> RunReopeningAuctionAsync(
        IReadOnlyCollection<Order> orders,
        PriceBandState state,
        MarketCycle cycle,
        int? tradeDayNumber,
        IReadOnlyDictionary<int, Participant> participants,
        Dictionary<(int ParticipantId, int CompanyId), Holding> holdings,
        IReadOnlyDictionary<int, Company> companiesById,
        DateTime now)
    {
        var eligible = orders
            .Where(order => order.RemainingQuantity > 0 && IsInsideBand(order.LimitPrice, state))
            .ToList();
        var candidates = eligible
            .Select(order => order.LimitPrice)
            .Distinct()
            .Select(price =>
            {
                var buyQuantity = eligible
                    .Where(order => order.Type == OrderType.Buy && order.LimitPrice >= price)
                    .Sum(order => order.RemainingQuantity);
                var sellQuantity = eligible
                    .Where(order => order.Type == OrderType.Sell && order.LimitPrice <= price)
                    .Sum(order => order.RemainingQuantity);
                return new
                {
                    Price = price,
                    Executable = Math.Min(buyQuantity, sellQuantity),
                    Imbalance = Math.Abs(buyQuantity - sellQuantity),
                    Distance = Math.Abs(price - state.ReferencePrice),
                };
            })
            .Where(candidate => candidate.Executable > 0)
            .OrderByDescending(candidate => candidate.Executable)
            .ThenBy(candidate => candidate.Imbalance)
            .ThenBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Price)
            .FirstOrDefault();
        if (candidates is null)
        {
            return 0;
        }

        var buys = eligible
            .Where(order => order.Type == OrderType.Buy && order.LimitPrice >= candidates.Price)
            .OrderByDescending(order => order.LimitPrice)
            .ThenBy(order => order.CreatedAt)
            .ThenBy(order => order.Id)
            .ToList();
        var sells = eligible
            .Where(order => order.Type == OrderType.Sell && order.LimitPrice <= candidates.Price)
            .OrderBy(order => order.LimitPrice)
            .ThenBy(order => order.CreatedAt)
            .ThenBy(order => order.Id)
            .ToList();
        var buyIndex = 0;
        var sellIndex = 0;
        var fills = 0;
        while (buyIndex < buys.Count && sellIndex < sells.Count)
        {
            var buy = buys[buyIndex];
            var sell = sells[sellIndex];
            if (buy.ParticipantId is int buyerId && buyerId == sell.ParticipantId)
            {
                var newer = buy.CreatedAt > sell.CreatedAt || (buy.CreatedAt == sell.CreatedAt && buy.Id > sell.Id) ? buy : sell;
                CancelSelfCross(newer, participants[buyerId], cycle.Id, now);
                if (ReferenceEquals(newer, buy)) buyIndex++; else sellIndex++;
                continue;
            }

            await ExecuteFillAsync(
                buy, sell, Math.Min(buy.RemainingQuantity, sell.RemainingQuantity), candidates.Price,
                cycle, tradeDayNumber, participants, holdings, companiesById, now);
            fills++;
            if (buy.RemainingQuantity == 0) buyIndex++;
            if (sell.RemainingQuantity == 0) sellIndex++;
        }
        return fills;
    }

    private static bool IsInsideBand(decimal price, PriceBandState? state) =>
        state is null || state.ReferencePrice <= 0m || (price >= state.LowerBandPrice && price <= state.UpperBandPrice);

    private void CancelSelfCross(Order order, Participant participant, int cycleId, DateTime now)
    {
        if (order.Type == OrderType.Buy && order.ReservedCashAmount > 0m)
        {
            var release = order.ReservedCashAmount;
            participant.ReservedBalance -= release;
            order.ReservedCashAmount = 0m;
            dbContext.MoneyTransactions.Add(new MoneyTransaction
            {
                ParticipantId = participant.Id,
                Type = MoneyTransactionType.Release,
                Amount = release,
                RelatedOrderId = order.Id,
                Description = "Reserved cash released on self-crossing order cancel",
                CreatedInCycleId = cycleId,
                CreatedAt = now,
            });
        }

        order.Status = OrderStatus.Cancelled;
        order.UpdatedAt = now;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private async Task ExecuteFillAsync(
        Order buy,
        Order sell,
        int quantity,
        decimal executionPrice,
        MarketCycle cycle,
        int? tradeDayNumber,
        IReadOnlyDictionary<int, Participant> participants,
        Dictionary<(int ParticipantId, int CompanyId), Holding> holdings,
        IReadOnlyDictionary<int, Company> companiesById,
        DateTime now)
    {
        var buyer = participants[buy.ParticipantId!.Value];
        var seller = sell.ParticipantId is int sellerId ? participants[sellerId] : null;
        var companyId = buy.CompanyId;
        var company = companiesById[companyId];

        var shareTransaction = new ShareTransaction
        {
            SellerId = seller?.Id,
            BuyerId = buyer.Id,
            CompanyId = companyId,
            Quantity = quantity,
            Price = executionPrice,
            TotalCost = executionPrice * quantity,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.ShareTransactions.Add(shareTransaction);

        // A company-originated offer has no seller position; only a participant seller's holding shrinks.
        // Capture cost basis before the reduce so a profitable fund sale can pay its manager below; a sell never
        // moves AverageCost, so reading it here or after the reduce is equivalent.
        var sellerAverageCost = 0m;
        if (seller is not null)
        {
            sellerAverageCost = holdings[(seller.Id, companyId)].AverageCost;
            ReduceHolding(holdings, seller.Id, companyId, quantity);
        }

        AddToHolding(holdings, buyer.Id, companyId, quantity, executionPrice);

        var spent = executionPrice * quantity;
        var reservationForFilled = buy.LimitPrice * quantity;
        var released = reservationForFilled - spent;

        var buyerMarginAdvance = 0m;
        if (marginService is not null)
        {
            var buyerAccount = await marginService.GetOrCreateAccountAsync(buyer.Id, cycle.TradingDayId > 0 ? cycle.TradingDayId : null);
            buyerMarginAdvance = marginService.ApplyPurchase(
                buyerAccount, buyer, spent, reservationForFilled, cycle.Id, now);
        }

        buyer.CurrentBalance -= spent;
        buyer.ReservedBalance -= reservationForFilled;
        buy.ReservedCashAmount -= reservationForFilled;

        dbContext.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = buyer.Id,
            Type = MoneyTransactionType.Debit,
            Amount = spent,
            RelatedOrderId = buy.Id,
            RelatedShareTransaction = shareTransaction,
            Description = $"Paid for {quantity} shares of {company.Name}",
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });

        // A fill below the buyer's limit frees the unused reservation on the filled shares.
        if (released > 0)
        {
            dbContext.MoneyTransactions.Add(new MoneyTransaction
            {
                ParticipantId = buyer.Id,
                Type = MoneyTransactionType.Release,
                Amount = released,
                RelatedOrderId = buy.Id,
                Description = "Unused buy reservation released on fill",
                CreatedInCycleId = cycle.Id,
                CreatedAt = now,
            });
        }

        var sellerMarginAllocation = new MarginSaleAllocation(0m, 0m, spent);
        if (seller is not null)
        {
            dbContext.MoneyTransactions.Add(new MoneyTransaction
            {
                ParticipantId = seller.Id,
                Type = MoneyTransactionType.Credit,
                Amount = spent,
                RelatedOrderId = sell.Id,
                RelatedShareTransaction = shareTransaction,
                FromWhomId = buyer.Id,
                Description = $"Proceeds from selling {quantity} shares of {company.Name}",
                CreatedInCycleId = cycle.Id,
                CreatedAt = now,
            });

            // The seller keeps the full sale credit above and pays the fee as a separate debit, so the two
            // ledger rows net to the seller's actual balance change. Company-float sells (seller == null) are
            // primary issuance and carry no fee.
            var fee = 0m;
            if (feeBank is not null)
            {
                fee = Round(spent * feeRate);
                if (fee > 0m)
                {
                    seller.SettledCashBalance -= fee;
                    feeBank.Balance += fee;

                    dbContext.MoneyTransactions.Add(new MoneyTransaction
                    {
                        ParticipantId = seller.Id,
                        Type = MoneyTransactionType.TradeFee,
                        Amount = fee,
                        RelatedOrderId = sell.Id,
                        RelatedShareTransaction = shareTransaction,
                        Description = $"Trading fee on sale of {company.Name}",
                        CreatedInCycleId = cycle.Id,
                        CreatedAt = now,
                    });
                }
            }

            var netProceeds = spent - fee;
            if (marginService is null)
            {
                seller.CurrentBalance += netProceeds;
            }
            else
            {
                var sellerAccount = await marginService.GetOrCreateAccountAsync(
                    seller.Id, cycle.TradingDayId > 0 ? cycle.TradingDayId : null);
                sellerMarginAllocation = marginService.ApplySaleProceeds(
                    sellerAccount, seller, netProceeds, cycle.Id, now);
                if (sellerMarginAllocation.InterestPaid > 0m)
                {
                    feeBank ??= await ResolveFeeBankAsync();
                    feeBank.Balance += sellerMarginAllocation.InterestPaid;
                }
            }

            // A profitable fund sale hands the founder a slice of the realized gain at once, debited from the
            // fund so the two ledger rows net to zero and no money is created. Skipped on a loss or break-even,
            // when the founder is gone, or when the fund lacks the free cash for it (then the fund keeps it all).
            if (fundsBySellerId is not null && fundsBySellerId.TryGetValue(seller.Id, out var sellerFund))
            {
                var gain = spent - Round(sellerAverageCost * quantity);
                var managerFee = gain > 0m ? Round(gain * managerProfitFeeShare) : 0m;
                if (managerFee > 0m
                    && participants.TryGetValue(sellerFund.FoundedByParticipantId, out var manager)
                    && manager.IsActive && !manager.IsBankrupt
                    && Math.Min(seller.AvailableBalance, seller.SettledCashBalance) >= managerFee)
                {
                    seller.CurrentBalance -= managerFee;
                    seller.SettledCashBalance -= managerFee;
                    manager.CurrentBalance += managerFee;
                    manager.SettledCashBalance += managerFee;

                    dbContext.MoneyTransactions.Add(new MoneyTransaction
                    {
                        ParticipantId = seller.Id,
                        Type = MoneyTransactionType.CollectiveFundManagerFee,
                        Amount = managerFee,
                        RelatedOrderId = sell.Id,
                        RelatedShareTransaction = shareTransaction,
                        Description = $"Manager profit fee on sale of {company.Name}",
                        CreatedInCycleId = cycle.Id,
                        CreatedAt = now,
                    });
                    dbContext.MoneyTransactions.Add(new MoneyTransaction
                    {
                        ParticipantId = manager.Id,
                        Type = MoneyTransactionType.CollectiveFundManagerFeeReceived,
                        Amount = managerFee,
                        RelatedOrderId = sell.Id,
                        RelatedShareTransaction = shareTransaction,
                        FromWhomId = seller.Id,
                        Description = $"Manager profit fee from fund on sale of {company.Name}",
                        CreatedInCycleId = cycle.Id,
                        CreatedAt = now,
                    });
                }
            }
        }
        else if (settlementService is null)
        {
            if (spent <= 0m)
            {
                throw new InvalidOperationException("Primary issuance proceeds must be positive.");
            }

            company.CashBalance += spent;
            dbContext.CorporateCashTransactions.Add(new CorporateCashTransaction
            {
                CompanyId = company.Id,
                Type = CorporateCashTransactionType.PrimaryIssuance,
                Amount = spent,
                CreatedInCycleId = cycle.Id,
                CreatedAt = now,
            });
        }

        if (settlementService is not null && tradeDayNumber is int settledTradeDayNumber)
        {
            settlementService.StageInstruction(
                shareTransaction,
                settledTradeDayNumber,
                cycle.Id,
                now,
                buyerMarginAdvance,
                sellerMarginAllocation.InterestPaid,
                sellerMarginAllocation.DebitPaid);
        }

        dbContext.OrderFills.Add(new OrderFill
        {
            BuyOrderId = buy.Id,
            SellOrderId = sell.Id,
            Quantity = quantity,
            ExecutionPrice = executionPrice,
            CreatedInCycleId = cycle.Id,
            ShareTransaction = shareTransaction,
            CreatedAt = now,
        });

        dbContext.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = companyId,
            Price = executionPrice,
            Capitalization = executionPrice * company.IssuedSharesCount,
            SourceShareTransaction = shareTransaction,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });

        buy.FilledQuantity += quantity;
        buy.Status = buy.RemainingQuantity == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
        buy.UpdatedAt = now;

        sell.FilledQuantity += quantity;
        sell.Status = sell.RemainingQuantity == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
        sell.UpdatedAt = now;
    }

    private void AddToHolding(
        Dictionary<(int ParticipantId, int CompanyId), Holding> holdings,
        int participantId,
        int companyId,
        int quantity,
        decimal price)
    {
        if (holdings.TryGetValue((participantId, companyId), out var holding))
        {
            var blended = ((holding.Quantity * holding.AverageCost) + (quantity * price)) / (holding.Quantity + quantity);
            holding.AverageCost = Round(blended);
            holding.Quantity += quantity;
            return;
        }

        var created = new Holding
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Quantity = quantity,
            AverageCost = price,
        };
        dbContext.Holdings.Add(created);
        holdings[(participantId, companyId)] = created;
    }

    private static void ReduceHolding(
        Dictionary<(int ParticipantId, int CompanyId), Holding> holdings,
        int participantId,
        int companyId,
        int quantity)
    {
        // Leave a zero-quantity row rather than deleting it: every holdings read filters on Quantity > 0, and
        // keeping the row avoids a delete-then-insert on the same (participant, company) unique key when a
        // seller sells out and rebuys the same company within one matching run.
        holdings[(participantId, companyId)].Quantity -= quantity;
    }

    // Mirrors LoanService's first-by-id resolve-or-create so the fee sink and the lender converge on the one
    // seeded bank. A created bank only appears when none exists yet (fee on, loans off, fresh database).
    private async Task<Bank> ResolveFeeBankAsync()
    {
        var existing = await dbContext.Banks.OrderBy(bank => bank.Id).FirstOrDefaultAsync();
        if (existing is not null)
        {
            return existing;
        }

        var created = new Bank { Name = feeBankName, InterestRate = 0m };
        dbContext.Banks.Add(created);
        return created;
    }
}
