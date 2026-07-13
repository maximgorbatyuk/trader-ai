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
        var (cycle, company) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 10_000m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termCycles: 100, cycle.Id);

        await Service().ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        // interest = 10000 × 0.001 = 10; installment = 100; total 110 charged.
        Assert.Equal(9_900m, refreshed.RemainingPrincipal);
        Assert.Equal(0m, refreshed.PastDuePrincipal);
        Assert.Equal(0m, refreshed.PastDueInterest);
        Assert.Equal(0m, refreshed.AccruedFees);

        var buyer = await context.Participants.AsNoTracking().FirstAsync(candidate => candidate.Id == trader.Id);
        Assert.Equal(9_890m, buyer.CurrentBalance);

        var interest = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(money => money.Type == MoneyTransactionType.LoanInterest);
        Assert.Equal(10m, interest.Amount);
        Assert.Equal(loan.Id, interest.RelatedLoanId);

        var repayment = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(money => money.Type == MoneyTransactionType.LoanRepayment);
        Assert.Equal(100m, repayment.Amount);
        Assert.Equal(loan.Id, repayment.RelatedLoanId);

        // The interest is the lender's revenue and accrues to the bank; principal repayment is not banked.
        var bank = await context.Banks.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.BankId);
        Assert.Equal(10m, bank.Balance);
    }

    [Fact]
    public async Task OriginateLoanCreditsBorrowerAndRecordsDisbursement()
    {
        var (cycle, _) = await SeedAsync(price: 100m);
        var borrower = await AddTraderAsync(currentBalance: 1_000m, type: ParticipantType.CollectiveFund);

        var loan = await Service().OriginateLoanAsync(borrower, principal: 50_000m, grossWorth: 200_000m, cycle.Id, DateTime.UtcNow);

        Assert.NotNull(loan);
        Assert.Equal(LoanStatus.Open, loan!.Status);
        Assert.Equal(50_000m, loan.Principal);
        Assert.Equal(50_000m, loan.RemainingPrincipal);

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
        var (cycle, _) = await SeedAsync(price: 100m);
        var borrower = await AddTraderAsync(currentBalance: 1_000m, type: ParticipantType.CollectiveFund);

        var disabled = new LoanService(context, Options.Create(new LoanOptions { Enabled = false }));
        var loan = await disabled.OriginateLoanAsync(borrower, principal: 50_000m, grossWorth: 200_000m, cycle.Id, DateTime.UtcNow);

        Assert.Null(loan);
        var refreshed = await context.Participants.AsNoTracking().FirstAsync(candidate => candidate.Id == borrower.Id);
        Assert.Equal(1_000m, refreshed.CurrentBalance);
        Assert.False(await context.Loans.AnyAsync());
    }

    [Fact]
    public async Task UnpaidInterestDoesNotAccrueToBankBalance()
    {
        var (cycle, _) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 0m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termCycles: 100, cycle.Id);

        await Service().ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // The borrower had no cash, so no interest was paid and the bank earns nothing this cycle.
        var bank = await context.Banks.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.BankId);
        Assert.Equal(0m, bank.Balance);
    }

    [Fact]
    public async Task MissedPaymentClassifiesArrearsWithoutCountingPrincipalTwice()
    {
        var (cycle, _) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 0m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termCycles: 100, cycle.Id);

        await Service().ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        // The missed installment stays inside remaining principal; only interest and the assessed fee add liability.
        Assert.Equal(10_000m, refreshed.RemainingPrincipal);
        Assert.Equal(100m, refreshed.PastDuePrincipal);
        Assert.Equal(10m, refreshed.PastDueInterest);
        Assert.Equal(11m, refreshed.AccruedFees);
        Assert.Equal(10_021m, refreshed.TotalLiability);

        Assert.False(await context.MoneyTransactions.AsNoTracking()
            .AnyAsync(money => money.Type == MoneyTransactionType.LoanFine));
    }

    [Fact]
    public async Task LaterServicingPaysFeesInterestAndOverduePrincipalBeforeCurrentPrincipal()
    {
        var (cycle, _) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 0m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termCycles: 100, cycle.Id);
        var service = Service();

        await service.ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        trader.CurrentBalance = 231m;
        await service.ProcessForCycleAsync(cycle.Id, cycle.CycleNumber + 1, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(9_800m, refreshed.RemainingPrincipal);
        Assert.Equal(0m, refreshed.PastDuePrincipal);
        Assert.Equal(0m, refreshed.PastDueInterest);
        Assert.Equal(0m, refreshed.AccruedFees);
        Assert.Equal(9_800m, refreshed.TotalLiability);

        var bank = await context.Banks.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.BankId);
        Assert.Equal(31m, bank.Balance);
        Assert.Equal(11m, await context.MoneyTransactions.AsNoTracking()
            .Where(money => money.Type == MoneyTransactionType.LoanFine)
            .SumAsync(money => money.Amount));
        Assert.Equal(20m, await context.MoneyTransactions.AsNoTracking()
            .Where(money => money.Type == MoneyTransactionType.LoanInterest)
            .SumAsync(money => money.Amount));
        Assert.Equal(200m, await context.MoneyTransactions.AsNoTracking()
            .Where(money => money.Type == MoneyTransactionType.LoanRepayment)
            .SumAsync(money => money.Amount));
    }

    [Fact]
    public async Task CashPaysTheOldestLoanFirst()
    {
        var (cycle, _) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 110m);
        var older = await AddLoanAsync(trader.Id, principal: 10_000m, termCycles: 100, cycle.Id);
        var newer = await AddLoanAsync(trader.Id, principal: 10_000m, termCycles: 100, cycle.Id);

        await Service().ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // Exactly one loan's 110 due is affordable; the older one is fully serviced, the newer one falls into arrears.
        var refreshedOlder = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == older.Id);
        var refreshedNewer = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == newer.Id);
        Assert.Equal(9_900m, refreshedOlder.RemainingPrincipal);
        Assert.Equal(0m, refreshedOlder.PastDuePrincipal);
        Assert.Equal(10_000m, refreshedNewer.RemainingPrincipal);
        Assert.Equal(100m, refreshedNewer.PastDuePrincipal);
        Assert.Equal(10m, refreshedNewer.PastDueInterest);
        Assert.Equal(11m, refreshedNewer.AccruedFees);
    }

    [Fact]
    public async Task DistressWindowForceSellsAnArrearedBorrowerIncludingThePlayer()
    {
        var (cycle, company) = await SeedAsync(price: 100m);
        var player = await AddTraderAsync(currentBalance: 0m, type: ParticipantType.Player);
        await AddSharesAsync(player.Id, company.Id, count: 100, price: 100m);
        // Opened this cycle with a 10-cycle term → 10 cycles left (inside the 15-cycle window), already in arrears.
        var loan = await AddLoanAsync(player.Id, principal: 5_000m, termCycles: 10, cycle.Id, pastDuePrincipal: 500m);

        await Service().ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
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
    public async Task RepayInFullClosesTheLoan()
    {
        var (cycle, _) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 20_000m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termCycles: 100, cycle.Id);

        var result = await Service().RepayLoanAsync(loan.Id, amount: null, cycle.Id, DateTime.UtcNow);

        Assert.True(result.Success);
        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(LoanStatus.Closed, refreshed.Status);
        Assert.Equal(LoanCloseReason.PaidInFull, refreshed.CloseReason);
        Assert.Equal(0m, refreshed.RemainingPrincipal);

        var buyer = await context.Participants.AsNoTracking().FirstAsync(candidate => candidate.Id == trader.Id);
        Assert.Equal(10_000m, buyer.CurrentBalance);
    }

    [Fact]
    public async Task PartialRepayReducesPrincipalAndLeavesLoanOpen()
    {
        var (cycle, _) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 20_000m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termCycles: 100, cycle.Id);

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
        var (cycle, company) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 200m);
        var loan = await AddLoanAsync(
            trader.Id,
            principal: 10_000m,
            termCycles: 100,
            cycle.Id,
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
        var (cycle, _) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 100m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termCycles: 100, cycle.Id);

        var result = await Service().RepayLoanAsync(loan.Id, amount: 5_000m, cycle.Id, DateTime.UtcNow);

        Assert.False(result.Success);
        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(10_000m, refreshed.RemainingPrincipal);
    }

    [Fact]
    public async Task ClosingOpenLoansForAParticipantDischargesThem()
    {
        var (cycle, _) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 0m);
        var loan = await AddLoanAsync(
            trader.Id,
            principal: 10_000m,
            termCycles: 100,
            cycle.Id,
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

    private async Task<(MarketCycle Cycle, Company Company)> SeedAsync(decimal price)
    {
        var now = DateTime.UtcNow;
        var cycle = new MarketCycle { CycleNumber = 100, Status = CycleStatus.Running, StartedAt = now };
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
        await context.SaveChangesAsync();
        return (cycle, company);
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
        int termCycles,
        int openedInCycleId,
        decimal pastDuePrincipal = 0m,
        decimal pastDueInterest = 0m,
        decimal accruedFees = 0m)
    {
        var bank = await context.Banks.FirstOrDefaultAsync();
        if (bank is null)
        {
            bank = new Bank { Name = "National bank", InterestRatePerCycle = 0.001m };
            context.Banks.Add(bank);
            await context.SaveChangesAsync();
        }

        var loan = new Loan
        {
            BankId = bank.Id,
            ParticipantId = participantId,
            Principal = principal,
            RemainingPrincipal = principal,
            InterestRatePerCycle = bank.InterestRatePerCycle,
            TermCycles = termCycles,
            ScheduledInstallment = decimal.Round(principal / termCycles, 2),
            PastDuePrincipal = pastDuePrincipal,
            PastDueInterest = pastDueInterest,
            AccruedFees = accruedFees,
            Status = LoanStatus.Open,
            OpenedInCycleId = openedInCycleId,
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
