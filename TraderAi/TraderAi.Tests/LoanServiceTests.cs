using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class LoanServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public LoanServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private LoanService Service() => new(context, Options.Create(new LoanOptions { Enabled = true }));

    [Fact]
    public async Task ServicingChargesInstallmentAndInterestAndRecordsLinkedTransactions()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 10_000m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termTradingDays: 20, cycle.Id, day.Id);

        await Service().ProcessForTradingDayAsync(day.Id, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        // Flat interest = 10000 × 0.10 / 20 = 50; principal installment = 500; total 550 charged this day.
        Assert.Equal(9_500m, refreshed.RemainingPrincipal);
        Assert.Equal(950m, refreshed.RemainingInterest);
        Assert.Equal(0m, refreshed.PastDuePrincipal);
        Assert.Equal(0m, refreshed.PastDueInterest);
        Assert.Equal(0m, refreshed.AccruedFees);

        var buyer = await context.Participants.AsNoTracking().FirstAsync(candidate => candidate.Id == trader.Id);
        Assert.Equal(9_450m, buyer.CurrentBalance);

        var interest = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(money => money.Type == MoneyTransactionType.LoanInterest);
        Assert.Equal(50m, interest.Amount);
        Assert.Equal(loan.Id, interest.RelatedLoanId);

        var repayment = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(money => money.Type == MoneyTransactionType.LoanRepayment);
        Assert.Equal(500m, repayment.Amount);
        Assert.Equal(loan.Id, repayment.RelatedLoanId);

        // The interest is the lender's revenue and accrues to the bank; principal repayment is not banked.
        var bank = await context.Banks.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.BankId);
        Assert.Equal(50m, bank.Balance);
    }

    [Fact]
    public async Task ServicedTermCollectsExactlyTenPercentInterest()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 1_000_000m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termTradingDays: 20, cycle.Id, day.Id);
        var service = Service();

        // Run every trading day of the term; the flat schedule must total exactly the fixed 10% interest.
        var currentCycle = cycle;
        var currentDay = day;
        for (var dayNumber = 2; dayNumber <= 21; dayNumber++)
        {
            (currentCycle, currentDay) = await AddNextDayAsync(dayNumber);
            await service.ProcessForTradingDayAsync(currentDay.Id, currentCycle.Id, DateTime.UtcNow);
            await context.SaveChangesAsync();
        }

        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(LoanStatus.Closed, refreshed.Status);
        Assert.Equal(0m, refreshed.RemainingInterest);

        var totalInterest = await context.MoneyTransactions.AsNoTracking()
            .Where(money => money.Type == MoneyTransactionType.LoanInterest)
            .SumAsync(money => money.Amount);
        Assert.Equal(1_000m, totalInterest);
    }

    [Fact]
    public async Task ServicingSkipsALoanAlreadyServicedThisTradingDay()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 10_000m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termTradingDays: 20, cycle.Id, day.Id);
        var service = Service();

        await service.ProcessForTradingDayAsync(day.Id, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();
        // A second pass for the same trading day must not charge the loan again.
        await service.ProcessForTradingDayAsync(day.Id, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(9_500m, refreshed.RemainingPrincipal);
        Assert.Equal(950m, refreshed.RemainingInterest);
    }

    [Fact]
    public async Task OriginatedLoanIsNotServicedOnItsOpeningDay()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var borrower = await AddTraderAsync(currentBalance: 10_000m);
        var loan = await Service().OriginateLoanAsync(borrower, principal: 10_000m, grossWorth: 200_000m, cycle.Id, day.Id, DateTime.UtcNow);
        Assert.NotNull(loan);

        // Servicing on the opening day is skipped because origination marks that day as already serviced.
        await Service().ProcessForTradingDayAsync(day.Id, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan!.Id);
        Assert.Equal(10_000m, refreshed.RemainingPrincipal);
        Assert.False(await context.MoneyTransactions.AsNoTracking()
            .AnyAsync(money => money.Type == MoneyTransactionType.LoanRepayment));
    }

    [Fact]
    public async Task OriginateLoanCreditsBorrowerAndRecordsDisbursement()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var borrower = await AddTraderAsync(currentBalance: 1_000m, type: ParticipantType.CollectiveFund);

        var loan = await Service().OriginateLoanAsync(borrower, principal: 50_000m, grossWorth: 200_000m, cycle.Id, day.Id, DateTime.UtcNow);

        Assert.NotNull(loan);
        Assert.Equal(LoanStatus.Open, loan!.Status);
        Assert.Equal(50_000m, loan.Principal);
        Assert.Equal(50_000m, loan.RemainingPrincipal);
        Assert.Equal(5_000m, loan.RemainingInterest);

        var refreshed = await context.Participants.AsNoTracking().FirstAsync(candidate => candidate.Id == borrower.Id);
        Assert.Equal(51_000m, refreshed.CurrentBalance);
        Assert.Equal(51_000m, refreshed.SettledCashBalance);

        var disbursement = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(money => money.Type == MoneyTransactionType.LoanDisbursement);
        Assert.Equal(50_000m, disbursement.Amount);
        Assert.Equal(loan.Id, disbursement.RelatedLoanId);
    }

    [Fact]
    public async Task OriginateLoanReturnsNullWhenLoansDisabled()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var borrower = await AddTraderAsync(currentBalance: 1_000m, type: ParticipantType.CollectiveFund);

        var disabled = new LoanService(context, Options.Create(new LoanOptions { Enabled = false }));
        var loan = await disabled.OriginateLoanAsync(borrower, principal: 50_000m, grossWorth: 200_000m, cycle.Id, day.Id, DateTime.UtcNow);

        Assert.Null(loan);
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(candidate => candidate.Id == borrower.Id);
        Assert.Equal(1_000m, refreshed.CurrentBalance);
        Assert.False(await context.Loans.AnyAsync());
    }

    [Fact]
    public async Task BorrowSucceedsWithinCapAndScalesTermInTradingDays()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var borrower = await AddTraderAsync(currentBalance: 100_000m);

        // 40% of 100k worth is 40k, well above this request; a largest-possible loan runs the maximum term.
        var result = await Service().BorrowLoanAsync(borrower, amount: 40_000m, grossWorth: 100_000m, cycle.Id, day.Id, DateTime.UtcNow);

        Assert.True(result.Success);
        Assert.NotNull(result.Loan);
        Assert.Equal(20, result.Loan!.TermTradingDays);
        Assert.Equal(4_000m, result.Loan.RemainingInterest);

        var refreshed = await context.Participants.AsNoTracking().FirstAsync(candidate => candidate.Id == borrower.Id);
        Assert.Equal(140_000m, refreshed.CurrentBalance);
    }

    [Fact]
    public async Task BorrowRejectedWhenItExceedsTheDebtCap()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var borrower = await AddTraderAsync(currentBalance: 100_000m);

        // The cap is 40% of gross worth (40k); a 50k request must be refused and open no loan.
        var result = await Service().BorrowLoanAsync(borrower, amount: 50_000m, grossWorth: 100_000m, cycle.Id, day.Id, DateTime.UtcNow);

        Assert.False(result.Success);
        Assert.Null(result.Loan);
        Assert.False(await context.Loans.AnyAsync());
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(candidate => candidate.Id == borrower.Id);
        Assert.Equal(100_000m, refreshed.CurrentBalance);
    }

    [Fact]
    public async Task UnpaidInterestDoesNotAccrueToBankBalance()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 0m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termTradingDays: 20, cycle.Id, day.Id);

        await Service().ProcessForTradingDayAsync(day.Id, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // The borrower had no cash, so no interest was paid and the bank earns nothing this day.
        var bank = await context.Banks.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.BankId);
        Assert.Equal(0m, bank.Balance);
    }

    [Fact]
    public async Task MissedPaymentClassifiesArrearsWithoutCountingPrincipalTwice()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 0m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termTradingDays: 20, cycle.Id, day.Id);

        await Service().ProcessForTradingDayAsync(day.Id, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        // The missed installment stays inside remaining principal; only interest and the assessed fee add liability.
        Assert.Equal(10_000m, refreshed.RemainingPrincipal);
        Assert.Equal(500m, refreshed.PastDuePrincipal);
        Assert.Equal(50m, refreshed.PastDueInterest);
        // Fine = (500 + 50) × 0.10 = 55.
        Assert.Equal(55m, refreshed.AccruedFees);
        Assert.Equal(10_105m, refreshed.TotalLiability);

        Assert.False(await context.MoneyTransactions.AsNoTracking()
            .AnyAsync(money => money.Type == MoneyTransactionType.LoanFine));
    }

    [Fact]
    public async Task LaterServicingPaysFeesInterestAndOverduePrincipalBeforeCurrentPrincipal()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 0m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termTradingDays: 20, cycle.Id, day.Id);
        var service = Service();

        await service.ProcessForTradingDayAsync(day.Id, cycle.Id, DateTime.UtcNow);
        // Day one left fees 55, overdue interest 50, overdue principal 500. Day two brings just enough to clear all
        // of those plus the new interest, but nothing for the new principal, which then falls into arrears.
        trader.CurrentBalance = 655m;
        var (nextCycle, nextDay) = await AddNextDayAsync(dayNumber: 2);
        await service.ProcessForTradingDayAsync(nextDay.Id, nextCycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(9_500m, refreshed.RemainingPrincipal);
        Assert.Equal(500m, refreshed.PastDuePrincipal);
        Assert.Equal(0m, refreshed.PastDueInterest);
        // The new arrears (500 overdue principal) draw a fresh 50 fine.
        Assert.Equal(50m, refreshed.AccruedFees);

        var bank = await context.Banks.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.BankId);
        // Bank collects the paid fee (55) and both interest slices (50 + 50).
        Assert.Equal(155m, bank.Balance);
        Assert.Equal(55m, await context.MoneyTransactions.AsNoTracking()
            .Where(money => money.Type == MoneyTransactionType.LoanFine)
            .SumAsync(money => money.Amount));
        Assert.Equal(100m, await context.MoneyTransactions.AsNoTracking()
            .Where(money => money.Type == MoneyTransactionType.LoanInterest)
            .SumAsync(money => money.Amount));
        Assert.Equal(500m, await context.MoneyTransactions.AsNoTracking()
            .Where(money => money.Type == MoneyTransactionType.LoanRepayment)
            .SumAsync(money => money.Amount));
    }

    [Fact]
    public async Task CashPaysTheOldestLoanFirst()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 550m);
        var older = await AddLoanAsync(trader.Id, principal: 10_000m, termTradingDays: 20, cycle.Id, day.Id);
        var newer = await AddLoanAsync(trader.Id, principal: 10_000m, termTradingDays: 20, cycle.Id, day.Id);

        await Service().ProcessForTradingDayAsync(day.Id, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // Exactly one loan's 550 due is affordable; the older one is fully serviced, the newer one falls into arrears.
        var refreshedOlder = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == older.Id);
        var refreshedNewer = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == newer.Id);
        Assert.Equal(9_500m, refreshedOlder.RemainingPrincipal);
        Assert.Equal(0m, refreshedOlder.PastDuePrincipal);
        Assert.Equal(10_000m, refreshedNewer.RemainingPrincipal);
        Assert.Equal(500m, refreshedNewer.PastDuePrincipal);
        Assert.Equal(50m, refreshedNewer.PastDueInterest);
        Assert.Equal(55m, refreshedNewer.AccruedFees);
    }

    [Fact]
    public async Task DistressWindowForceSellsAnArrearedBorrowerIncludingThePlayer()
    {
        var (cycle, company, day) = await SeedAsync(price: 100m);
        var player = await AddTraderAsync(currentBalance: 0m, type: ParticipantType.Player);
        await AddSharesAsync(player.Id, company.Id, count: 100, price: 100m);
        // Opened this trading day with a 2-day term → 2 days left (inside the 2-day window), already in arrears.
        var loan = await AddLoanAsync(player.Id, principal: 5_000m, termTradingDays: 2, cycle.Id, day.Id, pastDuePrincipal: 500m);

        await Service().ProcessForTradingDayAsync(day.Id, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var distressSell = await context.Orders.AsNoTracking()
            .SingleAsync(order => order.ParticipantId == player.Id && order.Type == OrderType.Sell);
        Assert.Equal(loan.Id, distressSell.RelatedLoanId);
        Assert.Equal(OrderStatus.Open, distressSell.Status);
        // First distress listing is 20% below the 100 market price.
        Assert.Equal(80m, distressSell.LimitPrice);
        Assert.True(distressSell.Quantity > 0);
    }

    [Fact]
    public async Task DistressSellIsFlooredToTheLowerBand()
    {
        var (cycle, company, day) = await SeedAsync(price: 100m);
        // The 20% base discount would ask 80, but an active band floors the distress sell at 85.
        await AddBandAsync(company.Id, reference: 100m, lower: 85m, upper: 110m);
        var player = await AddTraderAsync(currentBalance: 0m, type: ParticipantType.Player);
        await AddSharesAsync(player.Id, company.Id, count: 100, price: 100m);
        await AddLoanAsync(player.Id, principal: 5_000m, termTradingDays: 2, cycle.Id, day.Id, pastDuePrincipal: 500m);

        await Service().ProcessForTradingDayAsync(day.Id, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var distressSell = await context.Orders.AsNoTracking()
            .SingleAsync(order => order.ParticipantId == player.Id && order.Type == OrderType.Sell);
        Assert.Equal(85m, distressSell.LimitPrice);
    }

    [Fact]
    public async Task RepayInFullClosesTheLoan()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 20_000m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termTradingDays: 20, cycle.Id, day.Id);

        var result = await Service().RepayLoanAsync(loan.Id, amount: null, cycle.Id, DateTime.UtcNow);

        Assert.True(result.Success);
        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(LoanStatus.Closed, refreshed.Status);
        Assert.Equal(LoanCloseReason.PaidInFull, refreshed.CloseReason);
        Assert.Equal(0m, refreshed.RemainingPrincipal);

        // Early full payoff settles principal only; the not-yet-charged scheduled interest is forgone.
        var buyer = await context.Participants.AsNoTracking().FirstAsync(candidate => candidate.Id == trader.Id);
        Assert.Equal(10_000m, buyer.CurrentBalance);
    }

    [Fact]
    public async Task PartialRepayReducesPrincipalAndLeavesLoanOpen()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 20_000m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termTradingDays: 20, cycle.Id, day.Id);

        var result = await Service().RepayLoanAsync(loan.Id, amount: 3_000m, cycle.Id, DateTime.UtcNow);

        Assert.True(result.Success);
        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(LoanStatus.Open, refreshed.Status);
        Assert.Equal(7_000m, refreshed.RemainingPrincipal);

        var buyer = await context.Participants.AsNoTracking().FirstAsync(candidate => candidate.Id == trader.Id);
        Assert.Equal(17_000m, buyer.CurrentBalance);
    }

    [Fact]
    public async Task ManualRepaymentUsesUnreservedCashAndAllocatesFeesInterestThenPrincipal()
    {
        var (cycle, company, day) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 200m);
        var loan = await AddLoanAsync(
            trader.Id,
            principal: 10_000m,
            termTradingDays: 20,
            cycle.Id,
            day.Id,
            pastDuePrincipal: 100m,
            pastDueInterest: 10m,
            accruedFees: 11m);
        trader.ReservedBalance = 50m;
        context.Orders.Add(new Order
        {
            ParticipantId = trader.Id,
            CompanyId = company.Id,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 1,
            LimitPrice = 50m,
            ReservedCashAmount = 50m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var result = await Service().RepayLoanAsync(loan.Id, amount: 150m, cycle.Id, DateTime.UtcNow);

        Assert.True(result.Success);
        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(9_871m, refreshed.RemainingPrincipal);
        Assert.Equal(0m, refreshed.PastDuePrincipal);
        Assert.Equal(0m, refreshed.PastDueInterest);
        Assert.Equal(0m, refreshed.AccruedFees);

        var borrower = await context.Participants.AsNoTracking().FirstAsync(candidate => candidate.Id == trader.Id);
        Assert.Equal(50m, borrower.CurrentBalance);
        Assert.Equal(0m, borrower.AvailableBalance);

        var bank = await context.Banks.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.BankId);
        Assert.Equal(21m, bank.Balance);
        Assert.Equal(11m, await context.MoneyTransactions.AsNoTracking()
            .Where(money => money.Type == MoneyTransactionType.LoanFine)
            .SumAsync(money => money.Amount));
        Assert.Equal(10m, await context.MoneyTransactions.AsNoTracking()
            .Where(money => money.Type == MoneyTransactionType.LoanInterest)
            .SumAsync(money => money.Amount));
        Assert.Equal(129m, await context.MoneyTransactions.AsNoTracking()
            .Where(money => money.Type == MoneyTransactionType.LoanRepayment)
            .SumAsync(money => money.Amount));
    }

    [Fact]
    public async Task RepayRejectedWhenAvailableCashIsInsufficient()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 100m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termTradingDays: 20, cycle.Id, day.Id);

        var result = await Service().RepayLoanAsync(loan.Id, amount: 5_000m, cycle.Id, DateTime.UtcNow);

        Assert.False(result.Success);
        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(10_000m, refreshed.RemainingPrincipal);
    }

    [Fact]
    public async Task ClosingOpenLoansForAParticipantDischargesThem()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 0m);
        var loan = await AddLoanAsync(
            trader.Id,
            principal: 10_000m,
            termTradingDays: 20,
            cycle.Id,
            day.Id,
            pastDuePrincipal: 100m,
            pastDueInterest: 100m,
            accruedFees: 50m);

        await LoanService.CloseOpenLoansForParticipantAsync(context, trader.Id, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(LoanStatus.Closed, refreshed.Status);
        Assert.Equal(LoanCloseReason.ParticipantDeparted, refreshed.CloseReason);
        Assert.Equal(0m, refreshed.RemainingPrincipal);
        Assert.Equal(0m, refreshed.PastDuePrincipal);
        Assert.Equal(0m, refreshed.PastDueInterest);
        Assert.Equal(0m, refreshed.AccruedFees);
    }

    [Fact]
    public async Task AccruedFeesAreCappedAtPrincipalAndNeverExceedIt()
    {
        var (cycle, _, day) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 0m);
        // Fees already near the loan value; the next unpaid day's fine would otherwise push them over 100%.
        var loan = await AddLoanAsync(trader.Id, principal: 1_000m, termTradingDays: 20, cycle.Id, day.Id, accruedFees: 990m);

        var service = Service();
        await service.ProcessForTradingDayAsync(day.Id, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var afterOne = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(1_000m, afterOne.AccruedFees);

        // A borrower who still cannot pay keeps missing payments, but the fee stays pinned at the loan value.
        var (nextCycle, nextDay) = await AddNextDayAsync(dayNumber: 2);
        await service.ProcessForTradingDayAsync(nextDay.Id, nextCycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var afterTwo = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(1_000m, afterTwo.AccruedFees);
    }

    private async Task<(MarketCycle Cycle, Company Company, TradingDay Day)> SeedAsync(decimal price)
    {
        var now = DateTime.UtcNow;
        var day = new TradingDay { DayNumber = 1, State = TradingSessionState.Trading };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();

        var cycle = new MarketCycle { CycleNumber = 100, TradingDayId = day.Id, TradingCycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);
        var market = new Market { Name = "Demo", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(market);
        var company = new Company { Name = "Acme", IssuedSharesCount = 1000, CreatedAt = now, UpdatedAt = now };
        context.Companies.Add(company);
        await context.SaveChangesAsync();

        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = price,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });
        market.CurrentCycleId = cycle.Id;
        market.CurrentTradingDayId = day.Id;
        await context.SaveChangesAsync();
        return (cycle, company, day);
    }

    private async Task<(MarketCycle Cycle, TradingDay Day)> AddNextDayAsync(int dayNumber)
    {
        var now = DateTime.UtcNow;
        var day = new TradingDay { DayNumber = dayNumber, State = TradingSessionState.Trading };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();

        var cycle = new MarketCycle { CycleNumber = 100 + dayNumber, TradingDayId = day.Id, TradingCycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();
        return (cycle, day);
    }

    private async Task<Participant> AddTraderAsync(decimal currentBalance, ParticipantType type = ParticipantType.Individual)
    {
        var trader = new Participant
        {
            Name = "Trader",
            Type = type,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = currentBalance,
            CurrentBalance = currentBalance,
            SettledCashBalance = currentBalance,
            IsActive = true,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();
        return trader;
    }

    private async Task AddBandAsync(int companyId, decimal reference, decimal lower, decimal upper)
    {
        context.PriceBandStates.Add(new PriceBandState
        {
            CompanyId = companyId,
            State = LuldState.Normal,
            ReferencePrice = reference,
            LowerBandPrice = lower,
            UpperBandPrice = upper,
        });
        await context.SaveChangesAsync();
    }

    private async Task AddSharesAsync(int ownerId, int companyId, int count, decimal price)
    {
        context.Holdings.Add(new Holding
        {
            ParticipantId = ownerId,
            CompanyId = companyId,
            Quantity = count,
            AverageCost = price,
        });
        await context.SaveChangesAsync();
    }

    private async Task<Loan> AddLoanAsync(
        int participantId,
        decimal principal,
        int termTradingDays,
        int openedInCycleId,
        int openedInTradingDayId,
        decimal pastDuePrincipal = 0m,
        decimal pastDueInterest = 0m,
        decimal accruedFees = 0m)
    {
        var bank = await context.Banks.FirstOrDefaultAsync();
        if (bank is null)
        {
            bank = new Bank { Name = "National bank", InterestRate = 0.10m };
            context.Banks.Add(bank);
            await context.SaveChangesAsync();
        }

        var loan = new Loan
        {
            BankId = bank.Id,
            ParticipantId = participantId,
            Principal = principal,
            RemainingPrincipal = principal,
            InterestRate = bank.InterestRate,
            RemainingInterest = decimal.Round(principal * bank.InterestRate, 2),
            TermTradingDays = termTradingDays,
            ScheduledInstallment = decimal.Round(principal / termTradingDays, 2),
            PastDuePrincipal = pastDuePrincipal,
            PastDueInterest = pastDueInterest,
            AccruedFees = accruedFees,
            Status = LoanStatus.Open,
            OpenedInCycleId = openedInCycleId,
            OpenedInTradingDayId = openedInTradingDayId,

            // Left unset so servicing charges the loan on the seeded trading day (models a loan opened earlier).
            LastServicedTradingDayId = null,
            CreatedAt = DateTime.UtcNow,
        };
        context.Loans.Add(loan);
        await context.SaveChangesAsync();
        return loan;
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
