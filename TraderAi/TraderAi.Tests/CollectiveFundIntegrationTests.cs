using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Exercises the collective-fund hooks inside MarketService itself: a member's buying is switched off in the
// decision loop while its selling continues, and the can't-buy drought counter the join odds key off advances
// across real ticks.
public sealed class CollectiveFundIntegrationTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public CollectiveFundIntegrationTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task FundMembersStopBuyingButStillSell()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var marketService = new MarketService(context, new MatchingEngine(context), new DeterministicDecisionEngine(), new MarketCycleLock(), new Random(1));

        // Pool the share-holding seller and the cash-rich buyer into one fund; both hand their bidding to the
        // fund, but the seller keeps offering its shares.
        var seller = await context.Participants.FirstAsync(participant => participant.Name == "Alice");
        var buyer = await context.Participants.FirstAsync(participant => participant.Name == "Bob");
        var fund = await PoolIntoFundAsync(seller, buyer);

        var decisions = await marketService.GenerateDecisionsAsync();

        Assert.True(decisions.Success);
        Assert.Equal(0, await context.Orders.CountAsync(order => order.Type == OrderType.Buy));
        Assert.Equal(1, await context.Orders.CountAsync(order => order.Type == OrderType.Sell));

        // The fund itself holds nothing yet, so it places no orders this cycle.
        Assert.Equal(0, await context.Orders.CountAsync(order => order.ParticipantId == fund.ParticipantId));
    }

    [Fact]
    public async Task CannotBuyCyclesGrowsWhileStarvedThenResetsWhenAffordable()
    {
        var trader = await SeedStarvedTraderAsync();
        var marketService = new MarketService(context, new MatchingEngine(context), new NoOpDecisionEngine(), new MarketCycleLock(), new Random(1));

        await marketService.StepCycleAsync();
        await marketService.StepCycleAsync();

        var starved = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == trader.Id);
        Assert.Equal(2, starved.CannotBuyCycles);

        // Once it can afford the cheapest share again the streak clears.
        var tracked = await context.Participants.FirstAsync(participant => participant.Id == trader.Id);
        tracked.CurrentBalance = 1_000m;
        await context.SaveChangesAsync();

        await marketService.StepCycleAsync();

        var recovered = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == trader.Id);
        Assert.Equal(0, recovered.CannotBuyCycles);
    }

    private async Task<CollectiveFund> PoolIntoFundAsync(Participant first, Participant second)
    {
        var fundParticipant = new Participant
        {
            Name = "Collective Fund",
            Type = ParticipantType.CollectiveFund,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 0m,
            CurrentBalance = 0m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(fundParticipant);
        await context.SaveChangesAsync();

        var fund = new CollectiveFund
        {
            ParticipantId = fundParticipant.Id,
            FoundedByParticipantId = first.Id,
            Status = CollectiveFundStatus.Active,
            CreatedInCycleId = 1,
            CreatedAt = DateTime.UtcNow,
        };
        context.CollectiveFunds.Add(fund);
        await context.SaveChangesAsync();

        foreach (var member in new[] { first, second })
        {
            context.CollectiveFundParticipants.Add(new CollectiveFundParticipant
            {
                CollectiveFundId = fund.Id,
                ParticipantId = member.Id,
                JoinedAt = DateTime.UtcNow,
                JoinedInCycleId = 1,
                DepositAmount = 100m,
            });
        }

        await context.SaveChangesAsync();
        return fund;
    }

    private async Task<Participant> SeedStarvedTraderAsync()
    {
        var now = DateTime.UtcNow;

        var cycle = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);
        var market = new Market { Name = "Demo Market", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(market);
        var industry = new Industry { Name = "Tech" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();

        var company = new Company
        {
            Name = "Acme",
            IndustryId = industry.Id,
            IssuedSharesCount = 10,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Companies.Add(company);

        // Far too little cash to afford the cheapest share at 100.
        var trader = new Participant
        {
            Name = "Pauper",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 50m,
            CurrentBalance = 50m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();

        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = 100m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });
        market.CurrentCycleId = cycle.Id;
        await context.SaveChangesAsync();
        return trader;
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
