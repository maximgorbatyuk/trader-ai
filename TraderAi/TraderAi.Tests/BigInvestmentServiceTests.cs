using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the big-investment deal with a scripted Random. Disabled draws nothing; enabled draws one NextDouble to
// roll the per-cycle trigger, and only when it fires and an eligible investor/company pair exists it draws one
// Next to pick the pair and one more NextDouble for the deal size. The deal executor itself draws nothing.
public sealed class BigInvestmentServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private int industryId;

    public BigInvestmentServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private BigInvestmentService Service(bool enabled, Random random, bool sentimentEnabled = true) =>
        new(
            context,
            Options.Create(new BigInvestmentOptions { Enabled = enabled }),
            Options.Create(new RandomChanceRatesOptions()),
            new MarketImpactService(context),
            random,
            Options.Create(new IndustrySentimentOptions { Enabled = sentimentEnabled, SentimentValueLimit = 1000 }));

    [Fact]
    public async Task DisabledDoesNothing()
    {
        var cycle = await AddCycleAsync(dayNumber: 10);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 1000m, cycle);
        await AddTraderAsync(balance: 500_000m);
        await AddAuditorAsync();

        await Service(enabled: false, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.OrderFills.CountAsync());
        Assert.Equal(1000, (await context.Companies.AsNoTracking().SingleAsync()).IssuedSharesCount);
    }

    [Fact]
    public async Task TriggerMissDoesNothingAndDrawsNothingFurther()
    {
        var cycle = await AddCycleAsync(dayNumber: 10);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 1000m, cycle);
        await AddTraderAsync(balance: 500_000m);
        await AddAuditorAsync();

        // 0.5 misses the 0.15 trigger; the empty int queue proves the pair draw is never reached.
        await Service(enabled: true, new ScriptedRandom([0.5d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.OrderFills.CountAsync());
        Assert.Equal(1000, (await context.Companies.AsNoTracking().SingleAsync()).IssuedSharesCount);
    }

    [Fact]
    public async Task InvestorBelowFortyPercentOfCapitalizationIsNotEligible()
    {
        var cycle = await AddCycleAsync(dayNumber: 10);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 1000m, cycle); // cap 1,000,000 → 40% floor is 400,000
        await AddTraderAsync(balance: 300_000m);
        await AddAuditorAsync();

        // Trigger fires (0.0 < 0.15) but no pair clears the floor, so no pair or size draw is taken.
        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.OrderFills.CountAsync());
    }

    [Fact]
    public async Task PlayerIsExcludedFromTheAutomatedRoll()
    {
        var cycle = await AddCycleAsync(dayNumber: 10);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 1000m, cycle);
        await AddTraderAsync(balance: 500_000m, type: ParticipantType.Player);
        await AddAuditorAsync();

        // The rich participant is the human player, who the automated roll never acts for, so no pair exists.
        await Service(enabled: true, new ScriptedRandom([0.0d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.OrderFills.CountAsync());
    }

    [Fact]
    public async Task FundsADealMintingSharesAndRecordingEverything()
    {
        var cycle = await AddCycleAsync(dayNumber: 10);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 1000m, cycle);
        var investor = await AddTraderAsync(balance: 500_000m);
        var auditor = await AddAuditorAsync();

        // Trigger fires, the single pair is chosen, and the minimum 0.40 fraction is drawn → 400,000 for 400 shares.
        await Service(enabled: true, new ScriptedRandom([0.0d, 0.0d], [0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedCompany = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(1400, refreshedCompany.IssuedSharesCount);
        Assert.Equal(400_000m, refreshedCompany.CashBalance);
        Assert.Equal(cycle.DayNumber + 5, refreshedCompany.CloseProtectedUntilTradingDayNumber);

        var refreshedInvestor = await context.Participants.AsNoTracking().SingleAsync(p => p.Id == investor.Id);
        Assert.Equal(100_000m, refreshedInvestor.CurrentBalance);
        Assert.Equal(100_000m, refreshedInvestor.SettledCashBalance);

        var holding = await context.Holdings.AsNoTracking().SingleAsync(h => h.ParticipantId == investor.Id);
        Assert.Equal(400, holding.Quantity);
        Assert.Equal(400, holding.SettledQuantity);
        Assert.Equal(1000m, holding.AverageCost);

        var buy = await context.Orders.AsNoTracking().SingleAsync(o => o.ParticipantId == investor.Id);
        Assert.Equal(OrderType.Buy, buy.Type);
        Assert.Equal(OrderStatus.Filled, buy.Status);
        Assert.Equal(400, buy.Quantity);
        var sell = await context.Orders.AsNoTracking().SingleAsync(o => o.ParticipantId == null);
        Assert.Equal(OrderType.Sell, sell.Type);
        Assert.Equal(OrderStatus.Filled, sell.Status);

        var fill = await context.OrderFills.AsNoTracking().SingleAsync();
        Assert.Equal(buy.Id, fill.BuyOrderId);
        Assert.Equal(sell.Id, fill.SellOrderId);
        Assert.Equal(400, fill.Quantity);
        Assert.Equal(1000m, fill.ExecutionPrice);

        var shareTransaction = await context.ShareTransactions.AsNoTracking().SingleAsync();
        Assert.Null(shareTransaction.SellerId);
        Assert.Equal(investor.Id, shareTransaction.BuyerId);
        Assert.Equal(400_000m, shareTransaction.TotalCost);

        var corporateCash = await context.CorporateCashTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(CorporateCashTransactionType.BigInvestment, corporateCash.Type);
        Assert.Equal(400_000m, corporateCash.Amount);

        var debit = await context.MoneyTransactions.AsNoTracking().SingleAsync(t => t.Type == MoneyTransactionType.Debit);
        Assert.Equal(400_000m, debit.Amount);
        Assert.Equal(investor.Id, debit.ParticipantId);

        var rating = await context.CompanyRatings.AsNoTracking().SingleAsync();
        Assert.Equal(CompanyRiskRating.RaisedExpectations, rating.Rating);
        Assert.Equal(auditor.Id, rating.AuditorId);

        var news = await context.NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal(NewsCategory.CapitalRaise, news.Category);
        Assert.Equal(NewsImpactScope.None, news.Scope);

        Assert.Equal(510, (await context.Industries.AsNoTracking().SingleAsync()).SentimentValue);

        // Original snapshot, the deal snapshot, and the auditor raise's lift.
        Assert.Equal(3, await context.PriceSnapshots.CountAsync());

        var record = await context.CompanyInvestments.AsNoTracking().SingleAsync();
        Assert.Equal(refreshedCompany.Id, record.CompanyId);
        Assert.Equal(investor.Id, record.InvestorParticipantId);
        Assert.Equal(400_000m, record.DealValue);
        Assert.Equal(400, record.SharesIssued);
        Assert.Equal(1000, record.SharesBeforeDeal);
        Assert.Equal(1_000_000m, record.CapitalizationBeforeDeal);
        Assert.Equal(1_400_000m, record.FinalCapitalization);
        // 400 of the 1,400 shares after the deal.
        Assert.Equal(28.57m, record.InvestorSharePercent);
        Assert.Equal(cycle.DayNumber, record.TradingDayNumber);
        Assert.Equal(cycle.Id, record.CreatedInCycleId);
    }

    [Fact]
    public async Task SentimentDisabledLeavesTheIndustryUntouched()
    {
        var cycle = await AddCycleAsync(dayNumber: 10);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 1000m, cycle);
        await AddTraderAsync(balance: 500_000m);
        await AddAuditorAsync();

        await Service(enabled: true, new ScriptedRandom([0.0d, 0.0d], [0]), sentimentEnabled: false)
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.OrderFills.CountAsync());
        Assert.Equal(500, (await context.Industries.AsNoTracking().SingleAsync()).SentimentValue);
    }

    private async Task<TestCycle> AddCycleAsync(int dayNumber)
    {
        var day = new TradingDay { DayNumber = dayNumber, State = TradingSessionState.Trading, OpenedInCycleId = 0 };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();

        var cycle = new MarketCycle
        {
            CycleNumber = dayNumber * 10,
            TradingDayId = day.Id,
            TradingCycleNumber = 1,
            Status = CycleStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();

        day.OpenedInCycleId = cycle.Id;
        await context.SaveChangesAsync();
        return new TestCycle(cycle.Id, cycle.CycleNumber, dayNumber);
    }

    private async Task SetupMarketAsync(TestCycle cycle)
    {
        var now = DateTime.UtcNow;
        var industry = new Industry { Name = "Tech", SentimentValue = 500 };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();
        industryId = industry.Id;

        context.Markets.Add(new Market
        {
            Name = "Demo Market",
            Status = MarketStatus.Running,
            CurrentCycleId = cycle.Id,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync();
    }

    private async Task<Company> AddCompanyAsync(int issuedShares)
    {
        var now = DateTime.UtcNow;
        var company = new Company
        {
            Name = $"Acme {Guid.NewGuid():N}",
            IndustryId = industryId,
            IssuedSharesCount = issuedShares,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        return company;
    }

    private async Task AddSnapshotAsync(int companyId, decimal price, TestCycle cycle)
    {
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = companyId,
            Price = price,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task<Participant> AddTraderAsync(decimal balance, ParticipantType type = ParticipantType.Individual)
    {
        var trader = new Participant
        {
            Name = $"Trader {Guid.NewGuid():N}",
            Type = type,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = balance,
            CurrentBalance = balance,
            SettledCashBalance = balance,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();
        return trader;
    }

    private async Task<Auditor> AddAuditorAsync()
    {
        var auditor = new Auditor { Name = "Auditor", Description = "Reviews companies", CreatedAt = DateTime.UtcNow };
        context.Auditors.Add(auditor);
        await context.SaveChangesAsync();
        return auditor;
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed record TestCycle(int Id, int CycleNumber, int DayNumber);

    // Returns queued draws so every random branch is forced; throws if drawn past the script.
    private sealed class ScriptedRandom(double[] doubles, int[] ints) : Random
    {
        private readonly Queue<double> doubles = new(doubles);
        private readonly Queue<int> ints = new(ints);

        public override double NextDouble() => doubles.Dequeue();

        public override int Next(int maxValue) => ints.Dequeue();

        public override int Next(int minValue, int maxValue) => ints.Dequeue();
    }
}
