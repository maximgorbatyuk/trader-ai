using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class OrderRetentionTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public OrderRetentionTests()
    {
        // Foreign keys are enforced to mirror the app connection, so archival deletes exercise the real constraint.
        connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task ArchivingMovesTerminalOrdersPastRetentionAndKeepsLiveOnes()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        var market = await context.Markets.SingleAsync();
        market.NextDividendCycleNumber = 100;
        var company = await context.Companies.SingleAsync();
        var seller = await context.Participants.SingleAsync(participant => participant.Name == "Alice");
        var firstCycle = await context.MarketCycles.SingleAsync(cycle => cycle.CycleNumber == 1);
        var currentCycle = await AddCyclesThroughAsync(4);
        var now = DateTime.UtcNow;

        var oldCancelled = NewOrder(seller.Id, company.Id, OrderStatus.Cancelled, firstCycle.Id, filled: 0, now);
        var oldFilled = NewOrder(seller.Id, company.Id, OrderStatus.Filled, firstCycle.Id, filled: 1, now);
        // An old open order stays live: matching, decisions, and ageing still read the resting book. Its limit
        // sits far above the latest price so this cycle's matching cannot cross it.
        var oldOpen = NewOrder(seller.Id, company.Id, OrderStatus.Open, firstCycle.Id, filled: 0, now, limitPrice: 999m);
        var recentCancelled = NewOrder(seller.Id, company.Id, OrderStatus.Cancelled, currentCycle.Id, filled: 0, now);
        context.Orders.AddRange(oldCancelled, oldFilled, oldOpen, recentCancelled);
        market.CurrentCycleId = currentCycle.Id;
        await context.SaveChangesAsync();

        var service = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            archiveOptions: Options.Create(new ArchiveOptions { Enabled = true, RetentionCycles = 1 }));

        var result = await service.AdvanceCycleAsync();

        Assert.True(result.Success);
        Assert.Equal(
            new[] { oldCancelled.Id, oldFilled.Id },
            await context.OrderArchives.Select(order => order.Id).OrderBy(id => id).ToListAsync());

        var liveIds = await context.Orders.Select(order => order.Id).ToListAsync();
        Assert.DoesNotContain(oldCancelled.Id, liveIds);
        Assert.DoesNotContain(oldFilled.Id, liveIds);
        Assert.Contains(oldOpen.Id, liveIds);
        Assert.Contains(recentCancelled.Id, liveIds);
    }

    [Fact]
    public async Task ArchivingClearsMoneyTransactionLinksToTheOrdersItRemoves()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = await context.Markets.SingleAsync();
        market.NextDividendCycleNumber = 100;
        var company = await context.Companies.SingleAsync();
        var seller = await context.Participants.SingleAsync(participant => participant.Name == "Alice");
        var firstCycle = await context.MarketCycles.SingleAsync(cycle => cycle.CycleNumber == 1);
        var currentCycle = await AddCyclesThroughAsync(4);
        var now = DateTime.UtcNow;

        var agedOrder = NewOrder(seller.Id, company.Id, OrderStatus.Filled, firstCycle.Id, filled: 1, now);
        context.Orders.Add(agedOrder);
        await context.SaveChangesAsync();

        // A still-live transaction references the aged order through the enforced foreign key.
        var liveTransaction = new MoneyTransaction
        {
            ParticipantId = seller.Id,
            Type = MoneyTransactionType.Debit,
            Amount = 1m,
            RelatedOrderId = agedOrder.Id,
            CreatedInCycleId = currentCycle.Id,
            CreatedAt = now,
        };
        context.MoneyTransactions.Add(liveTransaction);
        market.CurrentCycleId = currentCycle.Id;
        await context.SaveChangesAsync();

        var service = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            archiveOptions: Options.Create(new ArchiveOptions { Enabled = true, RetentionCycles = 1 }));

        var result = await service.AdvanceCycleAsync();

        Assert.True(result.Success);
        Assert.Equal([agedOrder.Id], await context.OrderArchives.Select(order => order.Id).ToListAsync());
        Assert.DoesNotContain(agedOrder.Id, await context.Orders.Select(order => order.Id).ToListAsync());
        var reloaded = await context.MoneyTransactions.AsNoTracking().SingleAsync(row => row.Id == liveTransaction.Id);
        Assert.Null(reloaded.RelatedOrderId);
    }

    private static Order NewOrder(
        int participantId,
        int companyId,
        OrderStatus status,
        int createdInCycleId,
        int filled,
        DateTime now,
        decimal limitPrice = 150m) =>
        new()
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Type = OrderType.Sell,
            Status = status,
            Quantity = 1,
            FilledQuantity = filled,
            LimitPrice = limitPrice,
            CreatedInCycleId = createdInCycleId,
            CreatedAt = now,
            UpdatedAt = now,
        };

    private async Task<MarketCycle> AddCyclesThroughAsync(int finalCycleNumber)
    {
        for (var cycleNumber = 2; cycleNumber <= finalCycleNumber; cycleNumber++)
        {
            context.MarketCycles.Add(new MarketCycle
            {
                CycleNumber = cycleNumber,
                Status = CycleStatus.Running,
                StartedAt = DateTime.UtcNow,
            });
        }

        await context.SaveChangesAsync();
        return await context.MarketCycles.SingleAsync(cycle => cycle.CycleNumber == finalCycleNumber);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
