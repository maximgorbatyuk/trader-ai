using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Covers the player-managed fund advertisement: the pricing curve (dear when flat, cheap once grown past the
// cap), the fund-cash debit, the popularity increment and cycle stamp, the recruiting newswire, and the
// affordability guard. Uses the same in-memory SQLite fixture as the other service tests.
public sealed class FundAdvertisingTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public FundAdvertisingTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private MarketService Service() =>
        new(context, new MatchingEngine(context), new NoOpDecisionEngine(), new MarketCycleLock(), new FixedRoll(0d));

    // Opens the player fund and forces its worth to a round figure so the advertisement pricing is exact.
    private async Task<(MarketService Market, CollectiveFund Fund, Participant FundParticipant)> OpenFundWithWorthAsync(decimal fundWorth)
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service();
        await market.CreatePlayerAsync("Ada");
        Assert.True((await market.OpenPlayerFundAsync(4_000m, null)).Success);

        var fund = await context.CollectiveFunds.SingleAsync();
        var fundParticipant = await context.Participants.FirstAsync(participant => participant.Id == fund.ParticipantId);
        fundParticipant.CurrentBalance = fundWorth;
        await context.SaveChangesAsync();
        return (market, fund, fundParticipant);
    }

    private async Task AddFundWorthSnapshotAsync(int participantId, decimal netWorth)
    {
        var cycleId = (await context.Markets.FirstAsync()).CurrentCycleId ?? 0;
        context.ParticipantWorthSnapshots.Add(new ParticipantWorthSnapshot
        {
            ParticipantId = participantId,
            CreatedInCycleId = cycleId,
            Balance = netWorth,
            HoldingsValue = 0m,
            LoanLiability = 0m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    // A fund with no growth history pays the dear 10% fraction of its worth.
    [Fact]
    public async Task QuoteChargesTheDearFractionWhenTheFundHasNotGrown()
    {
        var (market, fund, _) = await OpenFundWithWorthAsync(fundWorth: 10_000m);

        var quote = (await market.GetFundAdvertiseQuoteAsync(fund.ParticipantId)).Quote!;

        Assert.Equal(0.10m, quote.Fraction);
        Assert.Equal(1_000m, quote.Price);
        Assert.Equal(10_000m, quote.FundWorth);
        Assert.Equal(0m, quote.GrowthPct);
        Assert.Equal(0, quote.PopularityIndex);
    }

    // A fund up by the growth cap or more over the window pays the cheap 0.1% fraction.
    [Fact]
    public async Task QuoteChargesTheCheapFractionWhenTheFundGrewPastTheCap()
    {
        var (market, fund, fundParticipant) = await OpenFundWithWorthAsync(fundWorth: 10_000m);
        await AddFundWorthSnapshotAsync(fundParticipant.Id, netWorth: 1_000m);
        await AddFundWorthSnapshotAsync(fundParticipant.Id, netWorth: 1_200m);

        var quote = (await market.GetFundAdvertiseQuoteAsync(fund.ParticipantId)).Quote!;

        Assert.Equal(0.001m, quote.Fraction);
        Assert.Equal(10m, quote.Price);
        Assert.Equal(20m, quote.GrowthPct);
    }

    // Advertising debits the fund's cash, records the spend, lifts popularity, stamps the cycle, and posts a
    // no-impact recruiting newswire.
    [Fact]
    public async Task AdvertiseDebitsFundCashLiftsPopularityAndPostsNews()
    {
        var (market, fund, fundParticipant) = await OpenFundWithWorthAsync(fundWorth: 10_000m);

        var result = await market.AdvertiseFundAsync(fund.ParticipantId);

        Assert.True(result.Success);
        await context.Entry(fundParticipant).ReloadAsync();
        Assert.Equal(9_000m, fundParticipant.CurrentBalance);

        await context.Entry(fund).ReloadAsync();
        Assert.Equal(1, fund.PopularityIndex);
        Assert.Equal(1, fund.LastAdvertisedInCycleNumber);

        var spend = await context.MoneyTransactions
            .SingleAsync(transaction => transaction.Type == MoneyTransactionType.FundAdvertisement);
        Assert.Equal(fundParticipant.Id, spend.ParticipantId);
        Assert.Equal(1_000m, spend.Amount);

        var news = await context.NewsPosts.SingleAsync(newsPost => newsPost.Category == NewsCategory.FundAdvertisement);
        Assert.Equal(NewsImpactScope.None, news.Scope);
    }

    // A fund whose spendable cash cannot cover the price is refused, leaving popularity and cash untouched.
    [Fact]
    public async Task AdvertiseFailsWhenTheFundCannotAfford()
    {
        var (market, fund, fundParticipant) = await OpenFundWithWorthAsync(fundWorth: 10_000m);
        fundParticipant.ReservedBalance = 9_900m;
        await context.SaveChangesAsync();

        var result = await market.AdvertiseFundAsync(fund.ParticipantId);

        Assert.False(result.Success);
        Assert.Equal("The fund cannot afford the advertisement.", result.Error);
        await context.Entry(fund).ReloadAsync();
        Assert.Equal(0, fund.PopularityIndex);
        await context.Entry(fundParticipant).ReloadAsync();
        Assert.Equal(10_000m, fundParticipant.CurrentBalance);
    }

    // The endpoints only serve the player's own managed fund; any other participant id is rejected.
    [Fact]
    public async Task QuoteRejectsAParticipantThatIsNotThePlayerFund()
    {
        var (market, fund, _) = await OpenFundWithWorthAsync(fundWorth: 10_000m);

        var result = await market.GetFundAdvertiseQuoteAsync(fund.ParticipantId + 999);

        Assert.False(result.Success);
        Assert.Equal("The fund is not player-managed.", result.Error);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    // Pins every ranged draw to its low end so the player's starting balance is deterministic.
    private sealed class FixedRoll(double value) : Random
    {
        public override double NextDouble() => value;

        public override int Next(int minValue, int maxValue) => minValue;
    }
}
