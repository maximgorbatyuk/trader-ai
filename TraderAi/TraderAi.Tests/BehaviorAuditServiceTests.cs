using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the thirty-cycle behavioural audit deterministically (it uses no randomness): the five activity
// metrics, their min-max normalisation into the two indices, the nearest-cluster reclassification of the Player
// and Player's Fund, the multiple-of-thirty cadence, and the guarantee that no other participant is reassigned.
public sealed class BehaviorAuditServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public BehaviorAuditServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    // Off-cadence cycles leave every index and personality untouched.
    [Fact]
    public async Task AuditSkipsCyclesThatAreNotAMultipleOfThirty()
    {
        var (_, cycle, _) = await SeedAsync(cycleNumber: 29);
        var trader = await AddTraderAsync(Temperament.Balanced, RiskProfile.Medium);
        await AddOrdersAsync(trader.Id, cycle.Id, count: 5);

        await new BehaviorAuditService(context).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == trader.Id);
        Assert.Equal(0m, refreshed.TemperamentIndex);
        Assert.Equal(0m, refreshed.RiskProfileIndex);
    }

    // Order counts drive the two order-frequency metrics; with the price and size metrics constant they normalise
    // to zero, so the busier trader's indices are exactly the two normalised order terms.
    [Fact]
    public async Task MetricsNormaliseIntoTheTwoIndices()
    {
        var (_, cycle, _) = await SeedAsync(cycleNumber: 30);
        var quiet = await AddTraderAsync(Temperament.Conservative, RiskProfile.Low);
        var busy = await AddTraderAsync(Temperament.Aggressive, RiskProfile.High);
        await AddOrdersAsync(quiet.Id, cycle.Id, count: 1);
        await AddOrdersAsync(busy.Id, cycle.Id, count: 3);

        await new BehaviorAuditService(context).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var quietRow = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == quiet.Id);
        var busyRow = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == busy.Id);

        // Order-frequency terms normalise to 0 and 1; the three constant metrics normalise to 0 for both.
        Assert.Equal(0m, quietRow.TemperamentIndex);
        Assert.Equal(2m, busyRow.TemperamentIndex);
        Assert.Equal(0m, quietRow.RiskProfileIndex);
        Assert.Equal(1m, busyRow.RiskProfileIndex);
    }

    // The market-price-difference metric reads each trade's move away from the price snapshot immediately before
    // it, and the move counts for the trade's buyer.
    [Fact]
    public async Task MarketPriceDifferenceMetricUsesThePreTradeSnapshot()
    {
        var (_, cycle, company) = await SeedAsync(cycleNumber: 30);
        var buyer = await AddTraderAsync(Temperament.Balanced, RiskProfile.Medium);
        var idle = await AddTraderAsync(Temperament.Balanced, RiskProfile.Medium);

        // Baseline price 100, then an issuer-sold trade at 110 with its own snapshot: a 10% move.
        await AddPriceSnapshotAsync(company.Id, price: 100m, cycle.Id, sourceTransactionId: null);
        var trade = await AddTradeAsync(buyer.Id, sellerId: null, company.Id, price: 110m, cycle.Id);
        await AddPriceSnapshotAsync(company.Id, price: 110m, cycle.Id, sourceTransactionId: trade.Id);

        await new BehaviorAuditService(context).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var buyerRow = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == buyer.Id);
        var idleRow = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == idle.Id);

        // The 10% move is the only non-zero metric, so it normalises to 1 and appears in both index sums.
        Assert.Equal(1m, buyerRow.TemperamentIndex);
        Assert.Equal(1m, buyerRow.RiskProfileIndex);
        Assert.Equal(0m, idleRow.TemperamentIndex);
        Assert.Equal(0m, idleRow.RiskProfileIndex);
    }

    // The Player and Player's Fund snap to whichever fixed-personality cluster average sits nearest their index.
    [Fact]
    public async Task PlayerAndFundSnapToTheNearestCluster()
    {
        // Every audited trader places at least one same-priced, same-sized order, so only order frequency varies
        // between them and the cost and size metrics stay constant. The busy trader anchors the aggressive
        // cluster, the sparse one the conservative cluster.
        var (_, cycle, _) = await SeedAsync(cycleNumber: 30);
        var aggressive = await AddTraderAsync(Temperament.Aggressive, RiskProfile.High);
        var conservative = await AddTraderAsync(Temperament.Conservative, RiskProfile.Low);
        await AddOrdersAsync(aggressive.Id, cycle.Id, count: 10);
        await AddOrdersAsync(conservative.Id, cycle.Id, count: 1);

        var player = await AddPlayerAsync();
        var (_, fundParticipant) = await AddPlayerFundAsync(cycle.Id);
        await AddOrdersAsync(player.Id, cycle.Id, count: 8);
        await AddOrdersAsync(fundParticipant.Id, cycle.Id, count: 2);

        await new BehaviorAuditService(context).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var playerRow = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == player.Id);
        var fundRow = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == fundParticipant.Id);

        // The player's activity sits close to the aggressive cluster; the barely-active fund sits by the low one.
        Assert.Equal(Temperament.Aggressive, playerRow.Temperament);
        Assert.Equal(RiskProfile.High, playerRow.RiskProfile);
        Assert.Equal(Temperament.Conservative, fundRow.Temperament);
        Assert.Equal(RiskProfile.Low, fundRow.RiskProfile);

        Assert.True(await context.NewsPosts.AnyAsync(newsPost =>
            newsPost.Scope == NewsImpactScope.None && newsPost.PublishedInCycleId == cycle.Id));
    }

    // A fixed-personality trader keeps its seeded personality even when its own activity would classify it
    // elsewhere; only the Player and Player's Fund are reassigned.
    [Fact]
    public async Task OnlyThePlayerAndFundAreReassigned()
    {
        var (_, cycle, _) = await SeedAsync(cycleNumber: 30);
        var aggressive = await AddTraderAsync(Temperament.Aggressive, RiskProfile.High);
        var lowFixed = await AddTraderAsync(Temperament.Conservative, RiskProfile.Low);
        var calmButBusy = await AddTraderAsync(Temperament.Conservative, RiskProfile.Low);
        await AddOrdersAsync(aggressive.Id, cycle.Id, count: 10);
        await AddOrdersAsync(lowFixed.Id, cycle.Id, count: 1);
        await AddOrdersAsync(calmButBusy.Id, cycle.Id, count: 10);

        var player = await AddPlayerAsync();
        await AddOrdersAsync(player.Id, cycle.Id, count: 6);

        await new BehaviorAuditService(context).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // calmButBusy trades like the aggressive cluster but is not reassigned, so it stays Conservative.
        var calmRow = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == calmButBusy.Id);
        Assert.Equal(Temperament.Conservative, calmRow.Temperament);

        // The player is reassigned away from its neutral seed.
        var playerRow = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == player.Id);
        Assert.NotEqual(Temperament.Balanced, playerRow.Temperament);
    }

    private async Task<(Market Market, MarketCycle Cycle, Company Company)> SeedAsync(int cycleNumber)
    {
        var now = DateTime.UtcNow;

        var cycle = new MarketCycle { CycleNumber = cycleNumber, Status = CycleStatus.Running, StartedAt = now };
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
            IssuedSharesCount = 1000,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();

        market.CurrentCycleId = cycle.Id;
        await context.SaveChangesAsync();
        return (market, cycle, company);
    }

    private async Task<Participant> AddTraderAsync(Temperament temperament, RiskProfile riskProfile)
    {
        var trader = new Participant
        {
            Name = "Trader",
            Type = ParticipantType.Individual,
            Temperament = temperament,
            RiskProfile = riskProfile,
            InitialBalance = 100_000m,
            CurrentBalance = 100_000m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();
        return trader;
    }

    private async Task<Participant> AddPlayerAsync()
    {
        var player = new Participant
        {
            Name = "Player",
            Type = ParticipantType.Player,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 100_000m,
            CurrentBalance = 100_000m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(player);
        await context.SaveChangesAsync();
        return player;
    }

    private async Task<(CollectiveFund Fund, Participant Participant)> AddPlayerFundAsync(int cycleId)
    {
        var fundParticipant = new Participant
        {
            Name = "Player's Fund",
            Type = ParticipantType.CollectiveFund,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 0m,
            CurrentBalance = 100_000m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(fundParticipant);
        await context.SaveChangesAsync();

        var fund = new CollectiveFund
        {
            ParticipantId = fundParticipant.Id,
            FoundedByParticipantId = fundParticipant.Id,
            IsPlayerManaged = true,
            Status = CollectiveFundStatus.Active,
            CreatedInCycleId = cycleId,
            CreatedAt = DateTime.UtcNow,
        };
        context.CollectiveFunds.Add(fund);
        await context.SaveChangesAsync();
        return (fund, fundParticipant);
    }

    private async Task AddOrdersAsync(int participantId, int cycleId, int count, decimal limitPrice = 100m, int quantity = 10)
    {
        var now = DateTime.UtcNow;
        for (var index = 0; index < count; index++)
        {
            context.Orders.Add(new Order
            {
                ParticipantId = participantId,
                CompanyId = 1,
                Type = OrderType.Buy,
                Status = OrderStatus.Open,
                Quantity = quantity,
                FilledQuantity = 0,
                LimitPrice = limitPrice,
                ReservedCashAmount = 0m,
                CreatedInCycleId = cycleId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await context.SaveChangesAsync();
    }

    private async Task AddPriceSnapshotAsync(int companyId, decimal price, int cycleId, int? sourceTransactionId)
    {
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = companyId,
            Price = price,
            SourceShareTransactionId = sourceTransactionId,
            CreatedInCycleId = cycleId,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task<ShareTransaction> AddTradeAsync(int buyerId, int? sellerId, int companyId, decimal price, int cycleId)
    {
        var now = DateTime.UtcNow;
        var trade = new ShareTransaction
        {
            BuyerId = buyerId,
            SellerId = sellerId,
            CompanyId = companyId,
            Quantity = 1,
            Price = price,
            TotalCost = price,
            CreatedInCycleId = cycleId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.ShareTransactions.Add(trade);
        await context.SaveChangesAsync();
        return trade;
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
