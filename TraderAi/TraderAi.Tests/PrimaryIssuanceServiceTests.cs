using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class PrimaryIssuanceServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public PrimaryIssuanceServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task ScarceFloatMintsSharesAndListsThemAtTheCurrentPrice()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var holder = await AddParticipantAsync();
        context.Holdings.Add(new Holding
        {
            ParticipantId = holder.Id,
            CompanyId = company.Id,
            Quantity = 950,
            SettledQuantity = 950,
            AverageCost = 80m,
        });
        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom(0d))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_020, await context.Companies.Select(candidate => candidate.IssuedSharesCount).SingleAsync());
        var order = await context.Orders.AsNoTracking().SingleAsync();
        Assert.Null(order.ParticipantId);
        Assert.Equal(OrderType.Sell, order.Type);
        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Equal(20, order.Quantity);
        Assert.Equal(100m, order.LimitPrice);
    }

    [Fact]
    public async Task ExistingPrimaryOfferOnTheTradingDayPreventsAnotherIssuanceWithoutDrawing()
    {
        var firstCycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var secondCycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 2);
        var company = await AddCompanyAsync(issuedShares: 1_000, price: 100m, firstCycle.Id);
        var holder = await AddParticipantAsync();
        context.Holdings.Add(new Holding
        {
            ParticipantId = holder.Id,
            CompanyId = company.Id,
            Quantity = 950,
            SettledQuantity = 950,
            AverageCost = 80m,
        });
        context.Orders.Add(new Order
        {
            ParticipantId = null,
            CompanyId = company.Id,
            Type = OrderType.Sell,
            Status = OrderStatus.Filled,
            Quantity = 10,
            FilledQuantity = 10,
            LimitPrice = 100m,
            IsFloatReplenishment = true,
            CreatedInCycleId = firstCycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom())
            .ProcessForCycleAsync(secondCycle.Id, secondCycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_000, await context.Companies.Select(candidate => candidate.IssuedSharesCount).SingleAsync());
        Assert.Single(await context.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ListingFloatOfferDoesNotConsumeTheReplenishmentCooldown()
    {
        var firstCycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var secondCycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 2);
        var company = await AddCompanyAsync(issuedShares: 1_000, price: 100m, firstCycle.Id);
        var holder = await AddParticipantAsync();
        context.Holdings.Add(new Holding
        {
            ParticipantId = holder.Id,
            CompanyId = company.Id,
            Quantity = 950,
            SettledQuantity = 950,
            AverageCost = 80m,
        });
        context.Orders.Add(new Order
        {
            ParticipantId = null,
            CompanyId = company.Id,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = 50,
            LimitPrice = 100m,
            CreatedInCycleId = firstCycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom(0d))
            .ProcessForCycleAsync(secondCycle.Id, secondCycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_020, await context.Companies.Select(candidate => candidate.IssuedSharesCount).SingleAsync());
        Assert.Equal(2, await context.Orders.CountAsync());
    }

    [Fact]
    public async Task FloatAtTheScarcityThresholdDoesNotIssueOrDraw()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var holder = await AddParticipantAsync();
        context.Holdings.Add(new Holding
        {
            ParticipantId = holder.Id,
            CompanyId = company.Id,
            Quantity = 900,
            SettledQuantity = 900,
            AverageCost = 80m,
        });
        await context.SaveChangesAsync();

        await Service(enabled: true, new ScriptedRandom())
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Empty(await context.Orders.AsNoTracking().ToListAsync());
    }

    private PrimaryIssuanceService Service(bool enabled, Random random) => new(
        context,
        Options.Create(new PrimaryIssuanceOptions
        {
            Enabled = enabled,
            FloatScarcityThresholdPercent = 10m,
        }),
        Options.Create(new RandomChanceRatesOptions
        {
            RandomMagnitudeBands =
            {
                PrimaryIssuanceRateMin = 0.02,
                PrimaryIssuanceRateMax = 0.20,
            },
        }),
        random);

    private async Task<MarketCycle> AddCycleAsync(int dayNumber, int cycleNumber)
    {
        var cycle = new MarketCycle
        {
            CycleNumber = cycleNumber,
            TradingCycleNumber = cycleNumber,
            Status = CycleStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();

        var day = await context.TradingDays.SingleOrDefaultAsync(candidate => candidate.DayNumber == dayNumber);
        if (day is null)
        {
            day = new TradingDay
            {
                DayNumber = dayNumber,
                State = TradingSessionState.Trading,
                OpenedInCycleId = cycle.Id,
            };
            context.TradingDays.Add(day);
            await context.SaveChangesAsync();
        }

        cycle.TradingDayId = day.Id;
        await context.SaveChangesAsync();
        return cycle;
    }

    private async Task<Company> AddCompanyAsync(int issuedShares, decimal price, int cycleId)
    {
        var company = new Company
        {
            Name = "Acme",
            IssuedSharesCount = issuedShares,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = price,
            Capitalization = price * issuedShares,
            CreatedInCycleId = cycleId,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        return company;
    }

    private async Task<Participant> AddParticipantAsync()
    {
        var participant = new Participant
        {
            Name = "Holder",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 10_000m,
            CurrentBalance = 10_000m,
            SettledCashBalance = 10_000m,
            IsActive = true,
        };
        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        return participant;
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed class ScriptedRandom(params double[] values) : Random
    {
        private readonly Queue<double> values = new(values);

        public override double NextDouble() => values.Dequeue();
    }
}
