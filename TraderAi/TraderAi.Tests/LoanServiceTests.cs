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
        Assert.Equal(0m, refreshed.PastDueAmount);

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
    }

    [Fact]
    public async Task MissedPaymentAccruesTenPercentFineIntoArrears()
    {
        var (cycle, _) = await SeedAsync(price: 100m);
        var trader = await AddTraderAsync(currentBalance: 0m);
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termCycles: 100, cycle.Id);

        await Service().ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        // Nothing paid; the 110 due is carried at a 10% fine → 121 arrears, principal untouched.
        Assert.Equal(10_000m, refreshed.RemainingPrincipal);
        Assert.Equal(121m, refreshed.PastDueAmount);

        var fine = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(money => money.Type == MoneyTransactionType.LoanFine);
        Assert.Equal(11m, fine.Amount);
        Assert.Equal(loan.Id, fine.RelatedLoanId);
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
        Assert.Equal(0m, refreshedOlder.PastDueAmount);
        Assert.Equal(10_000m, refreshedNewer.RemainingPrincipal);
        Assert.Equal(121m, refreshedNewer.PastDueAmount);
    }

    [Fact]
    public async Task OriginationSizesTheTermByLoanValueAgainstWorth()
    {
        var (cycle, company) = await SeedAsync(price: 100m);
        // A margin fill left the buyer 200 in the red holding 1000 in shares (worth ≈ 800 before the loan credit).
        var trader = await AddTraderAsync(currentBalance: -200m);
        await AddSharesAsync(trader.Id, company.Id, count: 10, price: 100m);

        await Service().OriginateLoansForNegativeBalancesAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);

        var loan = await context.Loans.AsNoTracking().SingleAsync(candidate => candidate.ParticipantId == trader.Id);
        Assert.Equal(230m, loan.Principal);
        Assert.Equal(230m, loan.RemainingPrincipal);
        // ratio = 230 / (0.40 × 800) = 0.71875 → term = 25 + 0.71875 × 175 ≈ 151.
        Assert.Equal(151, loan.TermCycles);

        var buyer = await context.Participants.AsNoTracking().FirstAsync(candidate => candidate.Id == trader.Id);
        Assert.Equal(30m, buyer.CurrentBalance);

        var disbursement = await context.MoneyTransactions.AsNoTracking()
            .SingleAsync(money => money.Type == MoneyTransactionType.LoanDisbursement);
        Assert.Equal(230m, disbursement.Amount);
        Assert.Equal(loan.Id, disbursement.RelatedLoanId);
    }

    [Fact]
    public async Task DistressWindowForceSellsAnArrearedBorrowerIncludingThePlayer()
    {
        var (cycle, company) = await SeedAsync(price: 100m);
        var player = await AddTraderAsync(currentBalance: 0m, type: ParticipantType.Player);
        await AddSharesAsync(player.Id, company.Id, count: 100, price: 100m);
        // Opened this cycle with a 10-cycle term → 10 cycles left (inside the 15-cycle window), already in arrears.
        var loan = await AddLoanAsync(player.Id, principal: 5_000m, termCycles: 10, cycle.Id, pastDueAmount: 500m);

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
        var loan = await AddLoanAsync(trader.Id, principal: 10_000m, termCycles: 100, cycle.Id, pastDueAmount: 250m);

        await LoanService.CloseOpenLoansForParticipantAsync(context, trader.Id, cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Loans.AsNoTracking().FirstAsync(candidate => candidate.Id == loan.Id);
        Assert.Equal(LoanStatus.Closed, refreshed.Status);
        Assert.Equal(LoanCloseReason.ParticipantDeparted, refreshed.CloseReason);
        Assert.Equal(0m, refreshed.RemainingPrincipal);
        Assert.Equal(0m, refreshed.PastDueAmount);
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

    private async Task<Loan> AddLoanAsync(int participantId, decimal principal, int termCycles, int openedInCycleId, decimal pastDueAmount = 0m)
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
            PastDueAmount = pastDueAmount,
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
