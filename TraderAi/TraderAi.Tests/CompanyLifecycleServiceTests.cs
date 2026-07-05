using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the company life cycle with a scripted Random. Closure is deterministic and draws nothing; appearance
// draws one NextDouble for the roll (only past the safe period) then a share count, a price, an industry index, and
// one Next for the name. Tests that isolate closure pin the appearance clock to the current cycle so its chance is
// zero and no draw is taken.
public sealed class CompanyLifecycleServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private int industryId;

    public CompanyLifecycleServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private CompanyLifecycleService Service(bool enabled, Random random) =>
        new(context, Options.Create(new CompanyLifecycleOptions { Enabled = enabled }), random);

    [Fact]
    public async Task DisabledDoesNothing()
    {
        var cycle = await AddCycleAsync(200);
        await SetupMarketAsync(cycle, lastAppearanceCycleNumber: 1);
        var company = await AddCompanyAsync();

        await Service(enabled: false, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Null(refreshed.ClosedInCycleId);
        Assert.Equal(1, await context.Companies.CountAsync());
    }

    [Fact]
    public async Task WithinSafePeriodNoCompanyAppearsAndNoDrawIsTaken()
    {
        // 99 cycles since the last listing, one short of the 100-cycle safe period, so the chance is zero. Passing
        // an empty scripted Random proves no roll is drawn inside the safe period.
        var cycle = await AddCycleAsync(100);
        await SetupMarketAsync(cycle, lastAppearanceCycleNumber: 1);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.Companies.CountAsync());
    }

    [Fact]
    public async Task PastSafePeriodAppearanceListsCompanyWithFloatSnapshotAndNews()
    {
        // 101 cycles on → chance 0.1%; the roll of 0.0005 clears it. Then share count, price, industry index, name.
        var cycle = await AddCycleAsync(201);
        await SetupMarketAsync(cycle, lastAppearanceCycleNumber: 100);

        await Service(enabled: true, new ScriptedRandom([0.0005d], [500, 100, 0, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var company = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(500, company.IssuedSharesCount);
        Assert.Equal(cycle.Id, company.CreatedInCycleId);
        Assert.Null(company.ClosedInCycleId);

        var floatOrder = await context.Orders.AsNoTracking().SingleAsync();
        Assert.Null(floatOrder.ParticipantId);
        Assert.Equal(OrderType.Sell, floatOrder.Type);
        Assert.Equal(OrderStatus.Open, floatOrder.Status);
        Assert.Equal(500, floatOrder.Quantity);
        Assert.Equal(100m, floatOrder.LimitPrice);

        var snapshot = await context.PriceSnapshots.AsNoTracking().SingleAsync();
        Assert.Equal(100m, snapshot.Price);
        Assert.Equal(50_000m, snapshot.Capitalization);

        Assert.Equal(1, await context.NewsPosts.CountAsync(post => post.Scope == NewsImpactScope.None));

        var refreshedMarket = await context.Markets.AsNoTracking().SingleAsync();
        Assert.Equal(201, refreshedMarket.LastCompanyAppearanceCycleNumber);
    }

    [Fact]
    public async Task AppearanceRollThatMissesListsNothingAndDrawsNoParameters()
    {
        var cycle = await AddCycleAsync(201);
        await SetupMarketAsync(cycle, lastAppearanceCycleNumber: 100);

        // Roll 0.5 exceeds the 0.1% chance, so nothing is listed. The empty int queue proves no parameter is drawn
        // once the roll misses.
        await Service(enabled: true, new ScriptedRandom([0.5d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.Companies.CountAsync());
        var refreshedMarket = await context.Markets.AsNoTracking().SingleAsync();
        Assert.Equal(100, refreshedMarket.LastCompanyAppearanceCycleNumber);
    }

    [Fact]
    public async Task SustainedPriceDeclineDelistsCompanyCancellingOrdersAndWipingHoldings()
    {
        var cycles = await AddCyclesAsync(21);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var company = await AddCompanyAsync();
        // 21 strictly decreasing closes → 20 down-moves, well past the 15-of-20 line.
        await AddDecliningSnapshotsAsync(company.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);

        var holder = await AddTraderAsync(balance: 5_000m, reserved: 0m);
        await AddHoldingAsync(holder.Id, company.Id, quantity: 10);
        var buyer = await AddTraderAsync(balance: 5_000m, reserved: 500m);
        var buy = await AddBuyOrderAsync(buyer.Id, company.Id, quantity: 5, price: 90m, reserved: 500m, current);
        await AddFloatSellOrderAsync(company.Id, quantity: 200, price: 100m, current);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(current.Id, refreshed.ClosedInCycleId);
        Assert.NotNull(refreshed.ClosedAt);

        // Every order for the company is cancelled — the participant buy and the issuer float alike.
        Assert.Equal(0, await context.Orders.CountAsync(order =>
            order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled));

        // The buyer's reservation is released back and logged; no cash is credited to the holder.
        var refreshedBuyer = await context.Participants.AsNoTracking().SingleAsync(p => p.Id == buyer.Id);
        Assert.Equal(0m, refreshedBuyer.ReservedBalance);
        Assert.Equal(1, await context.MoneyTransactions.CountAsync(t =>
            t.Type == MoneyTransactionType.Release && t.ParticipantId == buyer.Id));
        var refreshedHolder = await context.Participants.AsNoTracking().SingleAsync(p => p.Id == holder.Id);
        Assert.Equal(5_000m, refreshedHolder.CurrentBalance);

        // Holdings are zeroed with no payout row.
        var holding = await context.Holdings.AsNoTracking().SingleAsync();
        Assert.Equal(0, holding.Quantity);
        Assert.Equal(0, await context.MoneyTransactions.CountAsync(t => t.Type == MoneyTransactionType.Credit));

        Assert.Equal(1, await context.NewsPosts.CountAsync(post => post.Scope == NewsImpactScope.None));
    }

    [Fact]
    public async Task ThreeConsecutiveHighOrExtraRatingsDelistCompany()
    {
        var cycles = await AddCyclesAsync(3);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var company = await AddCompanyAsync();
        await AddRatingAsync(company.Id, CompanyRiskRating.High, cycles[0]);
        await AddRatingAsync(company.Id, CompanyRiskRating.Extra, cycles[1]);
        await AddRatingAsync(company.Id, CompanyRiskRating.High, cycles[2]);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(current.Id, refreshed.ClosedInCycleId);
    }

    [Fact]
    public async Task ALowRatingInTheStreakSpareCompany()
    {
        var cycles = await AddCyclesAsync(3);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var company = await AddCompanyAsync();
        await AddRatingAsync(company.Id, CompanyRiskRating.High, cycles[0]);
        await AddRatingAsync(company.Id, CompanyRiskRating.Low, cycles[1]);
        await AddRatingAsync(company.Id, CompanyRiskRating.High, cycles[2]);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Null(refreshed.ClosedInCycleId);
    }

    [Fact]
    public async Task OnlyTheWorstPerformerClosesWhenSeveralQualify()
    {
        var cycles = await AddCyclesAsync(21);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var worse = await AddCompanyAsync();
        var milder = await AddCompanyAsync();
        // Both decline every cycle (each qualifies), but 'worse' falls further, so only it is delisted.
        await AddDecliningSnapshotsAsync(worse.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);
        await AddDecliningSnapshotsAsync(milder.Id, cycles, startPrice: 100m, decrementPerCycle: 0.5m);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedWorse = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == worse.Id);
        var refreshedMilder = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == milder.Id);
        Assert.Equal(current.Id, refreshedWorse.ClosedInCycleId);
        Assert.Null(refreshedMilder.ClosedInCycleId);
    }

    [Fact]
    public async Task FullMarketWithNoQualifierForceClosesTheWorstPerformer()
    {
        var cycles = await AddCyclesAsync(2);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);

        // 300 live companies with no decline streak and no ratings — none qualifies on its own.
        var companies = await AddCompaniesAsync(300);
        // Give one company a short two-point drop: too little history to be a decline-streak qualifier, but the
        // most-negative recent change, so the pressure valve delists exactly it.
        var doomed = companies[150];
        await AddSnapshotAsync(doomed.Id, price: 100m, cycles[0]);
        await AddSnapshotAsync(doomed.Id, price: 40m, current);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.Companies.CountAsync(c => c.ClosedInCycleId != null));
        var refreshedDoomed = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == doomed.Id);
        Assert.Equal(current.Id, refreshedDoomed.ClosedInCycleId);
    }

    [Fact]
    public async Task DelistingDuringACrisisIsLoggedToTheTimeline()
    {
        var cycles = await AddCyclesAsync(21);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var company = await AddCompanyAsync();
        await AddDecliningSnapshotsAsync(company.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);
        var crisis = await AddCrisisAsync(current);

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow, crisis);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(current.Id, refreshed.ClosedInCycleId);

        var timelineEvent = await context.CrisisEvents.AsNoTracking()
            .SingleAsync(row => row.Type == CrisisEventType.CompanyClosed);
        Assert.Equal(crisis.Id, timelineEvent.CrisisId);
        Assert.Equal(company.Id, timelineEvent.CompanyId);
    }

    private async Task<Crisis> AddCrisisAsync(MarketCycle cycle)
    {
        var crisis = new Crisis
        {
            Title = "Shock",
            Content = "Body",
            Scope = CrisisScope.Global,
            TriggeredInCycleId = cycle.Id,
            TriggeredInCycleNumber = cycle.CycleNumber,
            DurationCycles = 20,
            TriggeredAt = DateTime.UtcNow,
        };
        context.Crises.Add(crisis);
        await context.SaveChangesAsync();
        return crisis;
    }

    private async Task<MarketCycle> AddCycleAsync(int number)
    {
        var cycle = new MarketCycle { CycleNumber = number, Status = CycleStatus.Running, StartedAt = DateTime.UtcNow };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();
        return cycle;
    }

    private async Task<List<MarketCycle>> AddCyclesAsync(int count)
    {
        var cycles = new List<MarketCycle>(count);
        for (var number = 1; number <= count; number++)
        {
            cycles.Add(await AddCycleAsync(number));
        }

        return cycles;
    }

    private async Task SetupMarketAsync(MarketCycle currentCycle, int lastAppearanceCycleNumber)
    {
        var now = DateTime.UtcNow;
        var industry = new Industry { Name = "Tech" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();
        industryId = industry.Id;

        context.Markets.Add(new Market
        {
            Name = "Demo Market",
            Status = MarketStatus.Running,
            CurrentCycleId = currentCycle.Id,
            LastCompanyAppearanceCycleNumber = lastAppearanceCycleNumber,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync();
    }

    private async Task<Company> AddCompanyAsync(int issuedShares = 1000)
    {
        var now = DateTime.UtcNow;
        var company = new Company
        {
            Name = $"Acme {Guid.NewGuid():N}",
            IndustryId = industryId,
            IssuedSharesCount = issuedShares,
            CreatedInCycleId = null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        return company;
    }

    private async Task<List<Company>> AddCompaniesAsync(int count)
    {
        var now = DateTime.UtcNow;
        var companies = new List<Company>(count);
        for (var index = 0; index < count; index++)
        {
            var company = new Company
            {
                Name = $"Acme {Guid.NewGuid():N}",
                IndustryId = industryId,
                IssuedSharesCount = 1000,
                CreatedAt = now,
                UpdatedAt = now,
            };
            companies.Add(company);
            context.Companies.Add(company);
        }

        await context.SaveChangesAsync();
        return companies;
    }

    private async Task AddSnapshotAsync(int companyId, decimal price, MarketCycle cycle)
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

    private async Task AddDecliningSnapshotsAsync(int companyId, List<MarketCycle> cycles, decimal startPrice, decimal decrementPerCycle)
    {
        for (var index = 0; index < cycles.Count; index++)
        {
            await AddSnapshotAsync(companyId, startPrice - (decrementPerCycle * index), cycles[index]);
        }
    }

    private async Task<Participant> AddTraderAsync(decimal balance, decimal reserved)
    {
        var trader = new Participant
        {
            Name = $"Trader {Guid.NewGuid():N}",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = balance,
            CurrentBalance = balance,
            ReservedBalance = reserved,
            IsActive = true,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();
        return trader;
    }

    private async Task AddHoldingAsync(int participantId, int companyId, int quantity)
    {
        context.Holdings.Add(new Holding
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Quantity = quantity,
            AverageCost = 100m,
        });
        await context.SaveChangesAsync();
    }

    private async Task<Order> AddBuyOrderAsync(int participantId, int companyId, int quantity, decimal price, decimal reserved, MarketCycle cycle)
    {
        var now = DateTime.UtcNow;
        var order = new Order
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = quantity,
            FilledQuantity = 0,
            LimitPrice = price,
            ReservedCashAmount = reserved,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return order;
    }

    private async Task AddFloatSellOrderAsync(int companyId, int quantity, decimal price, MarketCycle cycle)
    {
        var now = DateTime.UtcNow;
        context.Orders.Add(new Order
        {
            ParticipantId = null,
            CompanyId = companyId,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = quantity,
            FilledQuantity = 0,
            LimitPrice = price,
            ReservedCashAmount = 0m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync();
    }

    private async Task AddRatingAsync(int companyId, CompanyRiskRating rating, MarketCycle cycle)
    {
        var auditor = new Auditor { Name = "Ratings", Description = "Test", CreatedAt = DateTime.UtcNow };
        context.Auditors.Add(auditor);
        await context.SaveChangesAsync();

        context.CompanyRatings.Add(new CompanyRating
        {
            CompanyId = companyId,
            AuditorId = auditor.Id,
            Rating = rating,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

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
