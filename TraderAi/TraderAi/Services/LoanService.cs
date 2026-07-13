using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record RepayLoanResult(bool Success, Loan? Loan, string? Error)
{
    public static RepayLoanResult Ok(Loan loan) => new(true, loan, null);

    public static RepayLoanResult Fail(string error) => new(false, null, error);
}

// Owns explicit debt as Loan rows so a participant's CurrentBalance is never left negative. It originates,
// services, and closes loans while keeping principal, interest, and fee accounting reconcilable.
public sealed class LoanService(
    AppDbContext dbContext,
    IOptions<LoanOptions> options)
{
    // Scales explicit-loan terms against a stable fraction of borrower worth so larger obligations amortize longer.
    private const decimal DebtLimitFraction = 0.40m;

    // Forced distress-sale discount, mirroring BankruptcyService: start below market and deepen each unsold
    // re-listing, floored so the ask never reaches zero.
    private const decimal BaseDiscount = 0.20m;
    private const decimal DiscountStepSize = 0.05m;
    private const decimal MaxDiscount = 0.95m;

    private Bank? bank;

    // Originates one loan of an explicit principal for a single borrower and credits it to their balance, for a
    // caller that must honour a specific obligation rather than a negative balance left by matching (a collective
    // fund returning a departing member's deposit). Unlike the margin-buy path this ignores the debt cap, since
    // the obligation must be met; the debt is then serviced by the normal loan machinery. Saves so the
    // disbursement transaction can link the loan id, and returns the open loan (null when loans are disabled).
    public async Task<Loan?> OriginateLoanAsync(Participant borrower, decimal principal, decimal grossWorth, int currentCycleId, DateTime now)
    {
        if (!options.Value.Enabled)
        {
            return null;
        }

        var roundedPrincipal = Round(principal);
        if (roundedPrincipal <= 0m)
        {
            return null;
        }

        var loanBank = await ResolveBankAsync();
        var termCycles = TermForLoan(roundedPrincipal, grossWorth);
        var loan = new Loan
        {
            Bank = loanBank,
            BankId = loanBank.Id,
            ParticipantId = borrower.Id,
            Principal = roundedPrincipal,
            RemainingPrincipal = roundedPrincipal,
            InterestRatePerCycle = loanBank.InterestRatePerCycle,
            TermCycles = termCycles,
            ScheduledInstallment = Round(roundedPrincipal / termCycles),
            PastDuePrincipal = 0m,
            PastDueInterest = 0m,
            AccruedFees = 0m,
            DistressDiscountStep = 0,
            Status = LoanStatus.Open,
            OpenedInCycleId = currentCycleId,
            CreatedAt = now,
        };
        dbContext.Loans.Add(loan);
        borrower.CurrentBalance += roundedPrincipal;
        borrower.SettledCashBalance += roundedPrincipal;

        // Save so the loan has an id to link its disbursement transaction to.
        await dbContext.SaveChangesAsync();
        AddTransaction(borrower.Id, MoneyTransactionType.LoanDisbursement, roundedPrincipal, loan.Id, currentCycleId, now);
        await dbContext.SaveChangesAsync();
        return loan;
    }

    // Charges each open loan its per-cycle installment plus interest (oldest loan first, sharing the borrower's
    // cash), fining any shortfall into arrears, then force-sells a borrower still in arrears inside the final
    // stretch of a loan's term. Stages changes only; the caller saves.
    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var openLoans = await dbContext.Loans
            .Where(loan => loan.Status == LoanStatus.Open)
            .OrderBy(loan => loan.ParticipantId)
            .ThenBy(loan => loan.Id)
            .ToListAsync();
        if (openLoans.Count == 0)
        {
            return;
        }

        // Interest paid this cycle is the lender's income, so it accrues to the loan's own bank balance.
        var bankIds = openLoans.Select(loan => loan.BankId).Distinct().ToList();
        var banksById = await dbContext.Banks
            .Where(bank => bankIds.Contains(bank.Id))
            .ToDictionaryAsync(bank => bank.Id);

        var loansByParticipant = openLoans
            .GroupBy(loan => loan.ParticipantId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var participantIds = loansByParticipant.Keys.ToList();
        var participantsById = await dbContext.Participants
            .Where(participant => participantIds.Contains(participant.Id))
            .ToDictionaryAsync(participant => participant.Id);

        var cycleNumberById = await dbContext.MarketCycles
            .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

        foreach (var participantId in participantIds.OrderBy(id => id))
        {
            var loans = loansByParticipant[participantId];

            // A loan whose borrower row is gone (a departure that failed to close it) is closed defensively.
            if (!participantsById.TryGetValue(participantId, out var participant))
            {
                foreach (var loan in loans)
                {
                    MarkClosed(loan, LoanCloseReason.ParticipantDeparted, currentCycleId, now);
                }

                continue;
            }

            foreach (var loan in loans)
            {
                ServiceLoan(participant, loan, banksById.GetValueOrDefault(loan.BankId), currentCycleId, now);
            }
        }

        await ForceSellDistressedBorrowersAsync(loansByParticipant, participantsById, cycleNumberById, currentCycleNumber, currentCycleId, now);
    }

    private void ServiceLoan(Participant participant, Loan loan, Bank? bank, int currentCycleId, DateTime now)
    {
        var currentInterest = Round(loan.RemainingPrincipal * loan.InterestRatePerCycle);
        var currentPrincipal = Math.Min(
            loan.ScheduledInstallment,
            Math.Max(0m, loan.RemainingPrincipal - loan.PastDuePrincipal));
        var totalDue = loan.AccruedFees
            + loan.PastDueInterest
            + currentInterest
            + loan.PastDuePrincipal
            + currentPrincipal;
        if (totalDue <= 0m)
        {
            MaybeClose(loan, currentCycleId, now);
            return;
        }

        var paid = Math.Min(Math.Max(0m, participant.CurrentBalance), totalDue);
        participant.CurrentBalance -= paid;
        participant.SettledCashBalance -= paid;
        var allocation = AllocatePayment(loan, paid, currentInterest, currentPrincipal);
        loan.PastDueInterest = Round(loan.PastDueInterest + allocation.UnpaidCurrentInterest);
        loan.PastDuePrincipal = Round(loan.PastDuePrincipal + allocation.UnpaidCurrentPrincipal);

        if (allocation.FeesPaid > 0m)
        {
            AddTransaction(participant.Id, MoneyTransactionType.LoanFine, allocation.FeesPaid, loan.Id, currentCycleId, now);
        }

        if (allocation.InterestPaid > 0m)
        {
            AddTransaction(participant.Id, MoneyTransactionType.LoanInterest, allocation.InterestPaid, loan.Id, currentCycleId, now);
        }

        if (allocation.PrincipalPaid > 0m)
        {
            AddTransaction(participant.Id, MoneyTransactionType.LoanRepayment, allocation.PrincipalPaid, loan.Id, currentCycleId, now);
        }

        if (bank is not null)
        {
            bank.Balance += allocation.FeesPaid + allocation.InterestPaid;
        }

        var unpaidDue = loan.AccruedFees + loan.PastDueInterest + loan.PastDuePrincipal;
        if (unpaidDue > 0m)
        {
            var fine = Round(unpaidDue * options.Value.MissedPaymentFineRate);
            loan.AccruedFees = Round(loan.AccruedFees + fine);
        }

        MaybeClose(loan, currentCycleId, now);
    }

    // Once per borrower with an open loan inside the final window and in arrears, cancel any unsold distress
    // sells (deepening the discount) and list fresh below-market sells to raise the outstanding liability. Fires
    // for every borrower, the player included, as a scoped exception to "the market never forces the player".
    private async Task ForceSellDistressedBorrowersAsync(
        Dictionary<int, List<Loan>> loansByParticipant,
        IReadOnlyDictionary<int, Participant> participantsById,
        IReadOnlyDictionary<int, int> cycleNumberById,
        int currentCycleNumber,
        int currentCycleId,
        DateTime now)
    {
        var distressedIds = loansByParticipant
            .Where(entry => participantsById.ContainsKey(entry.Key)
                && entry.Value.Any(loan => loan.Status == LoanStatus.Open
                    && (loan.PastDuePrincipal > 0m || loan.PastDueInterest > 0m || loan.AccruedFees > 0m)
                    && RemainingTerm(loan, cycleNumberById, currentCycleNumber) <= options.Value.DistressWindowCycles))
            .Select(entry => entry.Key)
            .OrderBy(id => id)
            .ToList();
        if (distressedIds.Count == 0)
        {
            return;
        }

        var latestPriceByCompany = await LatestPriceByCompanyAsync();

        var ownedByParticipant = (await dbContext.Holdings
                .Where(holding => holding.Quantity > 0 && distressedIds.Contains(holding.ParticipantId))
                .Select(holding => new { holding.ParticipantId, holding.CompanyId, holding.Quantity })
                .ToListAsync())
            .GroupBy(holding => holding.ParticipantId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(holding => holding.CompanyId, holding => holding.Quantity));

        var openSells = await dbContext.Orders
            .Where(order => order.Type == OrderType.Sell
                && order.ParticipantId != null
                && distressedIds.Contains(order.ParticipantId.Value)
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .ToListAsync();

        // Uncommitted quantity per (participant, company): owned minus what is already listed for sale.
        var available = new Dictionary<(int ParticipantId, int CompanyId), int>();
        foreach (var (participantId, byCompany) in ownedByParticipant)
        {
            foreach (var (companyId, quantity) in byCompany)
            {
                available[(participantId, companyId)] = quantity;
            }
        }

        foreach (var order in openSells)
        {
            var key = (order.ParticipantId!.Value, order.CompanyId);
            if (available.TryGetValue(key, out var remaining))
            {
                available[key] = remaining - order.RemainingQuantity;
            }
        }

        var distressSellsByParticipant = openSells
            .Where(order => order.RelatedLoanId != null)
            .GroupBy(order => order.ParticipantId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var participantId in distressedIds)
        {
            var participant = participantsById[participantId];
            var loans = loansByParticipant[participantId];

            // The oldest open loan tracks how many times distress sells have gone unsold, deepening the discount.
            var trackingLoan = loans.First(loan => loan.Status == LoanStatus.Open);

            // Any distress sell still open did not clear last cycle; cancel it, deepen the discount, and re-list.
            if (distressSellsByParticipant.TryGetValue(participantId, out var priorSells) && priorSells.Count > 0)
            {
                foreach (var order in priorSells)
                {
                    var key = (order.ParticipantId!.Value, order.CompanyId);
                    available[key] = available.GetValueOrDefault(key) + order.RemainingQuantity;
                    order.Status = OrderStatus.Cancelled;
                    order.UpdatedAt = now;
                }

                trackingLoan.DistressDiscountStep++;
            }

            var cashNeeded = loans
                .Where(loan => loan.Status == LoanStatus.Open)
                .Sum(loan => loan.TotalLiability);
            if (cashNeeded <= 0m)
            {
                continue;
            }

            ListDistressSells(participant, trackingLoan, cashNeeded, ownedByParticipant.GetValueOrDefault(participantId) ?? [], latestPriceByCompany, available, currentCycleId, now);
        }
    }

    private void ListDistressSells(
        Participant participant,
        Loan trackingLoan,
        decimal cashNeeded,
        IReadOnlyDictionary<int, int> owned,
        IReadOnlyDictionary<int, decimal> latestPriceByCompany,
        Dictionary<(int ParticipantId, int CompanyId), int> available,
        int currentCycleId,
        DateTime now)
    {
        var discount = Math.Min(BaseDiscount + (DiscountStepSize * trackingLoan.DistressDiscountStep), MaxDiscount);
        var raised = 0m;

        // Priciest holdings first raise the needed cash from the fewest shares; ties break on id for determinism.
        foreach (var companyId in owned.Keys
            .OrderByDescending(id => latestPriceByCompany.GetValueOrDefault(id))
            .ThenBy(id => id))
        {
            if (raised >= cashNeeded)
            {
                break;
            }

            if (!latestPriceByCompany.TryGetValue(companyId, out var price) || price <= 0m)
            {
                continue;
            }

            var availableQuantity = available.GetValueOrDefault((participant.Id, companyId));
            if (availableQuantity <= 0)
            {
                continue;
            }

            var sellPrice = Round(price * (1m - discount));
            if (sellPrice <= 0m)
            {
                continue;
            }

            var stillNeeded = cashNeeded - raised;
            var quantityNeeded = (int)Math.Clamp(Math.Ceiling(stillNeeded / sellPrice), 1m, int.MaxValue);
            var quantity = Math.Min(availableQuantity, quantityNeeded);

            dbContext.Orders.Add(new Order
            {
                ParticipantId = participant.Id,
                CompanyId = companyId,
                Type = OrderType.Sell,
                Status = OrderStatus.Open,
                Quantity = quantity,
                FilledQuantity = 0,
                LimitPrice = sellPrice,
                ReservedCashAmount = 0m,
                RelatedLoanId = trackingLoan.Id,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
                UpdatedAt = now,
            });

            available[(participant.Id, companyId)] = availableQuantity - quantity;
            raised += quantity * sellPrice;
        }
    }

    // Player-initiated repayment never spends reserved cash and uses the same allocation order as servicing.
    public async Task<RepayLoanResult> RepayLoanAsync(int loanId, decimal? amount, int currentCycleId, DateTime now)
    {
        var loan = await dbContext.Loans.FirstOrDefaultAsync(candidate => candidate.Id == loanId);
        if (loan is null)
        {
            return RepayLoanResult.Fail("Loan not found.");
        }

        if (loan.Status != LoanStatus.Open)
        {
            return RepayLoanResult.Fail("Loan is already closed.");
        }

        var participant = await dbContext.Participants.FirstOrDefaultAsync(candidate => candidate.Id == loan.ParticipantId);
        if (participant is null)
        {
            return RepayLoanResult.Fail("Borrower not found.");
        }

        var liability = loan.TotalLiability;
        var requested = amount is decimal value ? Round(value) : liability;
        if (requested <= 0m)
        {
            return RepayLoanResult.Fail("Repayment amount must be greater than zero.");
        }

        var pay = Math.Min(requested, liability);
        if (participant.AvailableBalance < pay)
        {
            return RepayLoanResult.Fail("Insufficient available cash to repay the loan.");
        }

        var currentPrincipal = Math.Max(0m, loan.RemainingPrincipal - loan.PastDuePrincipal);
        var allocation = AllocatePayment(loan, pay, currentInterest: 0m, currentPrincipal);
        participant.CurrentBalance -= pay;
        participant.SettledCashBalance -= pay;

        if (allocation.FeesPaid > 0m)
        {
            AddTransaction(participant.Id, MoneyTransactionType.LoanFine, allocation.FeesPaid, loan.Id, currentCycleId, now);
        }

        if (allocation.InterestPaid > 0m)
        {
            AddTransaction(participant.Id, MoneyTransactionType.LoanInterest, allocation.InterestPaid, loan.Id, currentCycleId, now);
        }

        if (allocation.PrincipalPaid > 0m)
        {
            AddTransaction(participant.Id, MoneyTransactionType.LoanRepayment, allocation.PrincipalPaid, loan.Id, currentCycleId, now);
        }

        var loanBank = await dbContext.Banks.FirstOrDefaultAsync(candidate => candidate.Id == loan.BankId);
        if (loanBank is not null)
        {
            loanBank.Balance += allocation.FeesPaid + allocation.InterestPaid;
        }

        MaybeClose(loan, currentCycleId, now);

        await dbContext.SaveChangesAsync();
        return RepayLoanResult.Ok(loan);
    }

    // Closes every open loan a departing or bankrupt participant holds; the debt is discharged. Stages changes.
    public static async Task CloseOpenLoansForParticipantAsync(AppDbContext dbContext, int participantId, int currentCycleId, DateTime now)
    {
        var loans = await dbContext.Loans
            .Where(loan => loan.ParticipantId == participantId && loan.Status == LoanStatus.Open)
            .ToListAsync();
        foreach (var loan in loans)
        {
            MarkClosed(loan, LoanCloseReason.ParticipantDeparted, currentCycleId, now);
        }
    }

    // Past-due principal is already included in remaining principal, so only interest and fees add liability.
    public static async Task<Dictionary<int, decimal>> OpenLoanLiabilityByParticipantAsync(AppDbContext dbContext) =>
        (await dbContext.Loans
            .Where(loan => loan.Status == LoanStatus.Open)
            .Select(loan => new { loan.ParticipantId, loan.RemainingPrincipal, loan.PastDueInterest, loan.AccruedFees })
            .ToListAsync())
        .GroupBy(loan => loan.ParticipantId)
        .ToDictionary(group => group.Key, group => group.Sum(loan => loan.RemainingPrincipal + loan.PastDueInterest + loan.AccruedFees));

    public static void MarkClosed(Loan loan, LoanCloseReason reason, int currentCycleId, DateTime now)
    {
        loan.RemainingPrincipal = 0m;
        loan.PastDuePrincipal = 0m;
        loan.PastDueInterest = 0m;
        loan.AccruedFees = 0m;
        loan.Status = LoanStatus.Closed;
        loan.CloseReason = reason;
        loan.ClosedInCycleId = currentCycleId;
        loan.ClosedAt = now;
    }

    private void MaybeClose(Loan loan, int currentCycleId, DateTime now)
    {
        if (loan.TotalLiability <= 0m)
        {
            MarkClosed(loan, LoanCloseReason.PaidInFull, currentCycleId, now);
        }
    }

    private static PaymentAllocation AllocatePayment(Loan loan, decimal payment, decimal currentInterest, decimal currentPrincipal)
    {
        var available = payment;
        var feesPaid = TakePayment(ref available, loan.AccruedFees);
        loan.AccruedFees -= feesPaid;

        var overdueInterestPaid = TakePayment(ref available, loan.PastDueInterest);
        loan.PastDueInterest -= overdueInterestPaid;
        var currentInterestPaid = TakePayment(ref available, currentInterest);

        var overduePrincipalPaid = TakePayment(ref available, loan.PastDuePrincipal);
        loan.PastDuePrincipal -= overduePrincipalPaid;
        loan.RemainingPrincipal -= overduePrincipalPaid;
        var currentPrincipalPaid = TakePayment(ref available, currentPrincipal);
        loan.RemainingPrincipal -= currentPrincipalPaid;

        return new PaymentAllocation(
            feesPaid,
            overdueInterestPaid + currentInterestPaid,
            overduePrincipalPaid + currentPrincipalPaid,
            currentInterest - currentInterestPaid,
            currentPrincipal - currentPrincipalPaid);
    }

    private static decimal TakePayment(ref decimal available, decimal due)
    {
        var paid = Math.Min(available, Math.Max(0m, due));
        available -= paid;
        return paid;
    }

    private readonly record struct PaymentAllocation(
        decimal FeesPaid,
        decimal InterestPaid,
        decimal PrincipalPaid,
        decimal UnpaidCurrentInterest,
        decimal UnpaidCurrentPrincipal);

    private static int RemainingTerm(Loan loan, IReadOnlyDictionary<int, int> cycleNumberById, int currentCycleNumber)
    {
        var openedNumber = cycleNumberById.GetValueOrDefault(loan.OpenedInCycleId);
        return openedNumber + loan.TermCycles - currentCycleNumber;
    }

    // A bigger loan relative to the borrower's worth runs a longer term; the clamp keeps it in the option band.
    private int TermForLoan(decimal principal, decimal grossWorth)
    {
        var min = options.Value.MinTermCycles;
        var max = options.Value.MaxTermCycles;
        if (grossWorth <= 0m || principal <= 0m)
        {
            return max;
        }

        var ratio = (double)(principal / (DebtLimitFraction * grossWorth));
        var term = min + (ratio * (max - min));
        return (int)Math.Round(Math.Clamp(term, min, max), MidpointRounding.AwayFromZero);
    }

    private async Task<Bank> ResolveBankAsync()
    {
        if (bank is not null)
        {
            return bank;
        }

        bank = await dbContext.Banks.OrderBy(candidate => candidate.Id).FirstOrDefaultAsync();
        if (bank is null)
        {
            bank = new Bank { Name = options.Value.BankName, InterestRatePerCycle = options.Value.InterestRatePerCycle };
            dbContext.Banks.Add(bank);
            await dbContext.SaveChangesAsync();
        }

        return bank;
    }

    private void AddTransaction(int participantId, MoneyTransactionType type, decimal amount, int loanId, int currentCycleId, DateTime now) =>
        dbContext.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = participantId,
            Type = type,
            Amount = amount,
            RelatedLoanId = loanId,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });

    private Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync() =>
        PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
