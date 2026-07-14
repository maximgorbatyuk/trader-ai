using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AiMarketSnapshotBuilderTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public AiMarketSnapshotBuilderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task BuildsComprehensiveSnapshot()
    {
        var seed = await SeedThirtyFiveCycleMarketAsync();

        var snapshot = await Builder().BuildAsync(seed.AiParticipantId);

        Assert.NotNull(snapshot);
        Assert.Equal(35, snapshot!.Market.CycleNumber);
        Assert.Equal(1, snapshot.Market.TradingDayNumber);
        Assert.Equal("Trading", snapshot.Market.Session);
        Assert.Null(snapshot.Market.ActiveCrisis);

        Assert.Equal(0.005m, snapshot.Settings.TradeFeeRate);
        Assert.Equal(1, snapshot.Settings.SettlementLagTradingDays);
        Assert.False(snapshot.Settings.MarginEnabled);
        Assert.Equal(10, snapshot.Settings.MaxOrdersPerDecision);

        var holding = Assert.Single(snapshot.Participant.Holdings);
        Assert.Equal(seed.Company1Id, holding.CompanyId);
        Assert.Equal(135m, holding.CurrentPrice);
        Assert.Equal(1_350m, holding.CurrentValue);
        Assert.Equal(450m, holding.UnrealizedResult);

        var openOrder = Assert.Single(snapshot.Participant.OpenOrders);
        Assert.Equal(5, openOrder.RemainingQuantity);
        Assert.Equal(4_500m, snapshot.Participant.BuyingPower);
        Assert.Equal(6_350m, snapshot.Participant.NetWorth);

        Assert.Equal(2, snapshot.Companies.Count);
        var company1 = snapshot.Companies.Single(company => company.CompanyId == seed.Company1Id);
        Assert.Equal(135m, company1.CurrentPrice);
        Assert.Equal(135_000m, company1.Capitalization);
        Assert.Equal("Normal", company1.TradingStatus);
        Assert.Equal(115m, company1.ActiveLowerPrice);
        Assert.Equal(3, company1.RecentRatings.Count);
        Assert.Equal("High", company1.RecentRatings[0].Rating);

        var company2 = snapshot.Companies.Single(company => company.CompanyId == seed.Company2Id);
        Assert.NotNull(company2.AllowedMinimumPrice);
        Assert.Empty(company2.RecentRatings);

        Assert.Equal(2, snapshot.Industries.Count);
        Assert.NotEmpty(snapshot.OrderBook.Buys);
        Assert.NotEmpty(snapshot.OrderBook.Sells);

        var company1CapPoints = snapshot.CapitalizationHistory.Where(point => point.CompanyId == seed.Company1Id).ToList();
        Assert.Equal(30, company1CapPoints.Count);
        Assert.Equal(6, company1CapPoints.Min(point => point.CycleNumber));
        Assert.Equal(35, company1CapPoints.Max(point => point.CycleNumber));

        Assert.Equal(30, snapshot.SentimentHistory.Count(point => point.IndustryId == seed.Industry1Id));
    }

    [Fact]
    public async Task FundMemberHasZeroBuyingPowerButKeepsHoldings()
    {
        var day = new TradingDay { DayNumber = 1, State = TradingSessionState.Trading, OpenedInCycleId = 0 };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();
        var cycle = new MarketCycle { CycleNumber = 1, TradingDayId = day.Id, TradingCycleNumber = 1, Status = CycleStatus.Running };
        var market = new Market { Name = "M", Status = MarketStatus.Running };
        var industry = new Industry { Name = "Tech" };
        context.AddRange(cycle, market, industry);
        await context.SaveChangesAsync();

        var company = new Company { Name = "Acme", IndustryId = industry.Id, IssuedSharesCount = 100 };
        context.Companies.Add(company);
        var fund = new CollectiveFund { Status = CollectiveFundStatus.Active };
        context.CollectiveFunds.Add(fund);
        // A pooled fund member holds shares but has no discretionary cash.
        var member = new Participant { Name = "Member", Type = ParticipantType.Individual, IsActive = true, CurrentBalance = 0m, SettledCashBalance = 0m };
        context.Participants.Add(member);
        await context.SaveChangesAsync();

        context.CollectiveFundParticipants.Add(new CollectiveFundParticipant
        {
            CollectiveFundId = fund.Id,
            ParticipantId = member.Id,
            JoinedInCycleId = cycle.Id,
            DepositAmount = 500m,
        });
        context.PriceSnapshots.Add(new PriceSnapshot { CompanyId = company.Id, Price = 100m, Capitalization = 10_000m, CreatedInCycleId = cycle.Id });
        context.Holdings.Add(new Holding { ParticipantId = member.Id, CompanyId = company.Id, Quantity = 5, SettledQuantity = 5, AverageCost = 90m });
        day.OpenedInCycleId = cycle.Id;
        market.CurrentCycleId = cycle.Id;
        market.CurrentTradingDayId = day.Id;
        await context.SaveChangesAsync();

        var snapshot = await Builder().BuildAsync(member.Id);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsFundMember);
        Assert.Equal(0m, snapshot.Participant.BuyingPower);
        var holding = Assert.Single(snapshot.Participant.Holdings);
        Assert.Equal(5, holding.SettledQuantity);
    }

    private AiMarketSnapshotBuilder Builder() => new(
        context,
        new MarginService(context, Options.Create(new MarginOptions { Enabled = false })),
        new TradingClockService(context, Options.Create(new TradingClockOptions
        {
            TradingCyclesPerDay = 210,
            TradingCycleSeconds = 2,
            BreakDurationSeconds = 60,
        })),
        Options.Create(new AiTradingOptions { HistoryCycles = 30, MaxOrdersPerDecision = 10 }),
        Options.Create(new TradeFeeOptions { Enabled = true, FeeRate = 0.005m }),
        Options.Create(new SettlementOptions { SettlementLagTradingDays = 1 }),
        Options.Create(new MarginOptions { Enabled = false }),
        Options.Create(new VolatilityHaltOptions()));

    private async Task<MarketSeed> SeedThirtyFiveCycleMarketAsync()
    {
        var day = new TradingDay { DayNumber = 1, State = TradingSessionState.Trading, OpenedInCycleId = 0 };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();

        var cycles = new List<MarketCycle>();
        for (var number = 1; number <= 35; number++)
        {
            cycles.Add(new MarketCycle
            {
                CycleNumber = number,
                TradingDayId = day.Id,
                TradingCycleNumber = number,
                Status = number == 35 ? CycleStatus.Running : CycleStatus.Completed,
            });
        }

        var market = new Market { Name = "Market", Status = MarketStatus.Running };
        var tech = new Industry { Name = "Tech", SentimentValue = 40 };
        var energy = new Industry { Name = "Energy", SentimentValue = -10 };
        context.AddRange(cycles);
        context.AddRange(market, tech, energy);
        await context.SaveChangesAsync();

        var company1 = new Company { Name = "Acme", IndustryId = tech.Id, IssuedSharesCount = 1_000 };
        var company2 = new Company { Name = "Zenith", IndustryId = energy.Id, IssuedSharesCount = 2_000 };
        context.AddRange(company1, company2);
        await context.SaveChangesAsync();

        foreach (var cycle in cycles)
        {
            context.PriceSnapshots.Add(new PriceSnapshot
            {
                CompanyId = company1.Id,
                Price = 100m + cycle.CycleNumber,
                Capitalization = (100m + cycle.CycleNumber) * company1.IssuedSharesCount,
                CreatedInCycleId = cycle.Id,
            });
            context.PriceSnapshots.Add(new PriceSnapshot
            {
                CompanyId = company2.Id,
                Price = 50m + cycle.CycleNumber,
                Capitalization = (50m + cycle.CycleNumber) * company2.IssuedSharesCount,
                CreatedInCycleId = cycle.Id,
            });
            context.SectorSentimentSnapshots.Add(new SectorSentimentSnapshot
            {
                IndustryId = tech.Id,
                SentimentValue = cycle.CycleNumber,
                CreatedInCycleId = cycle.Id,
            });
            context.SectorSentimentSnapshots.Add(new SectorSentimentSnapshot
            {
                IndustryId = energy.Id,
                SentimentValue = -cycle.CycleNumber,
                CreatedInCycleId = cycle.Id,
            });
        }

        var ratingCycles = cycles.TakeLast(4).ToList();
        var ratingValues = new[]
        {
            CompanyRiskRating.Low,
            CompanyRiskRating.Extra,
            CompanyRiskRating.High,
            CompanyRiskRating.High,
        };
        for (var index = 0; index < ratingCycles.Count; index++)
        {
            context.CompanyRatings.Add(new CompanyRating
            {
                CompanyId = company1.Id,
                AuditorId = 1,
                Rating = ratingValues[index],
                ImpactPercent = 5m,
                CreatedInCycleId = ratingCycles[index].Id,
            });
        }

        context.PriceBandStates.Add(new PriceBandState
        {
            CompanyId = company1.Id,
            State = LuldState.Normal,
            ReferencePrice = 135m,
            LowerBandPrice = 115m,
            UpperBandPrice = 155m,
            UpdatedInCycleId = cycles[^1].Id,
        });

        var aiTrader = new Participant
        {
            Name = "AI Trader",
            Type = ParticipantType.AIAgent,
            IsActive = true,
            CurrentBalance = 5_000m,
            SettledCashBalance = 5_000m,
            ReservedBalance = 500m,
        };
        var other = new Participant
        {
            Name = "Rival",
            Type = ParticipantType.Individual,
            IsActive = true,
            CurrentBalance = 9_000m,
            SettledCashBalance = 9_000m,
        };
        context.AddRange(aiTrader, other);
        await context.SaveChangesAsync();

        context.Holdings.Add(new Holding
        {
            ParticipantId = aiTrader.Id,
            CompanyId = company1.Id,
            Quantity = 10,
            SettledQuantity = 10,
            AverageCost = 90m,
        });
        context.Orders.AddRange(
            new Order { ParticipantId = aiTrader.Id, CompanyId = company1.Id, Type = OrderType.Buy, Status = OrderStatus.Open, Quantity = 5, LimitPrice = 130m, ReservedCashAmount = 500m, CreatedInCycleId = cycles[^1].Id },
            new Order { ParticipantId = other.Id, CompanyId = company1.Id, Type = OrderType.Sell, Status = OrderStatus.Open, Quantity = 8, LimitPrice = 140m, CreatedInCycleId = cycles[^1].Id },
            new Order { ParticipantId = other.Id, CompanyId = company2.Id, Type = OrderType.Buy, Status = OrderStatus.Open, Quantity = 3, LimitPrice = 80m, ReservedCashAmount = 240m, CreatedInCycleId = cycles[^1].Id });

        day.OpenedInCycleId = cycles[0].Id;
        market.CurrentCycleId = cycles[^1].Id;
        market.CurrentTradingDayId = day.Id;
        await context.SaveChangesAsync();

        return new MarketSeed(aiTrader.Id, company1.Id, company2.Id, tech.Id);
    }

    private sealed record MarketSeed(int AiParticipantId, int Company1Id, int Company2Id, int Industry1Id);

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
