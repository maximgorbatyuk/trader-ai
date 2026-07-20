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

        var openOrder = Assert.Single(snapshot.Participant.OpenOrders);
        Assert.Equal(5, openOrder.RemainingQuantity);
        Assert.Equal(4_500m, snapshot.Participant.BuyingPower);
        Assert.Equal(6_350m, snapshot.Participant.NetWorth);

        Assert.Equal(2, snapshot.Companies.Count);
        var company1 = snapshot.Companies.Single(company => company.CompanyId == seed.Company1Id);
        Assert.Equal(135m, company1.CurrentPrice);
        Assert.Null(company1.TradingStatus);
        Assert.Equal(115m, company1.ActiveLowerPrice);
        Assert.Single(company1.RecentRatings);
        Assert.Equal("High", company1.RecentRatings[0].Rating);

        var company2 = snapshot.Companies.Single(company => company.CompanyId == seed.Company2Id);
        Assert.NotNull(company2.AllowedMinimumPrice);
        Assert.Empty(company2.RecentRatings);

        Assert.Equal(2, snapshot.Industries.Count);

        var company1CapPoints = snapshot.CapitalizationHistory
            .Where(point => point.CompanyId == seed.Company1Id)
            .OrderBy(point => point.CycleNumber)
            .ToList();
        // The 30-cycle window (cycles 6-35) is averaged into five 6-cycle periods labelled by their most recent cycle.
        Assert.Equal(5, company1CapPoints.Count);
        Assert.Equal(11, company1CapPoints.Min(point => point.CycleNumber));
        Assert.Equal(35, company1CapPoints.Max(point => point.CycleNumber));
        // The latest period averages capitalization over cycles 30-35: mean(130..135) x 1000 shares.
        Assert.Equal(132_500m, company1CapPoints.Single(point => point.CycleNumber == 35).Capitalization!.Value);

        var industry1SentimentPoints = snapshot.SentimentHistory
            .Where(point => point.IndustryId == seed.Industry1Id)
            .OrderBy(point => point.CycleNumber)
            .ToList();
        // The same 30-cycle window is averaged into five 6-cycle periods, matching the capitalization chart.
        Assert.Equal(5, industry1SentimentPoints.Count);
        Assert.Equal(11, industry1SentimentPoints.Min(point => point.CycleNumber));
        Assert.Equal(35, industry1SentimentPoints.Max(point => point.CycleNumber));
        // The latest period averages sentiment over cycles 30-35: mean(30..35) rounds to 33.
        Assert.Equal(33, industry1SentimentPoints.Single(point => point.CycleNumber == 35).SentimentValue);
        Assert.Empty(snapshot.BigInvestmentOpportunities);
    }

    [Fact]
    public async Task SnapshotListsBigInvestmentBoundsUsingTransferableSettledCash()
    {
        var seed = await SeedThirtyFiveCycleMarketAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.AiParticipantId);
        participant.CurrentBalance = 100_000m;
        participant.SettledCashBalance = 80_000m;
        participant.ReservedBalance = 10_000m;
        await context.SaveChangesAsync();

        var snapshot = await Builder().BuildAsync(seed.AiParticipantId);

        var opportunity = snapshot!.BigInvestmentOpportunities
            .Single(candidate => candidate.CompanyId == seed.Company1Id);
        Assert.Equal(54_000m, opportunity.MinimumAmount);
        Assert.Equal(80_000m, opportunity.MaximumAmount);
    }

    [Fact]
    public async Task DisabledBigInvestmentDoesNotAdvertiseOpportunities()
    {
        var seed = await SeedThirtyFiveCycleMarketAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.AiParticipantId);
        participant.CurrentBalance = 100_000m;
        participant.SettledCashBalance = 100_000m;
        await context.SaveChangesAsync();

        var snapshot = await Builder(bigInvestmentEnabled: false).BuildAsync(seed.AiParticipantId);

        Assert.Empty(snapshot!.BigInvestmentOpportunities);
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
        Assert.Empty(snapshot.BigInvestmentOpportunities);
    }

    [Fact]
    public async Task BuildsSharedPolicyEnvelopeExecutableAskExposureAndParticipantFeedback()
    {
        var seed = await SeedThirtyFiveCycleMarketAsync();
        var participant = await context.Participants.SingleAsync(candidate => candidate.Id == seed.AiParticipantId);
        participant.CurrentBalance = 100_000m;
        participant.SettledCashBalance = 100_000m;
        participant.RiskProfile = RiskProfile.Medium;

        var rival = await context.Participants.SingleAsync(candidate => candidate.Name == "Rival");
        context.Orders.Add(new Order
        {
            ParticipantId = rival.Id,
            CompanyId = seed.Company2Id,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = 20,
            LimitPrice = 90m,
            CreatedInCycleId = (await context.Markets.SingleAsync()).CurrentCycleId!.Value,
        });
        context.AiTraderCalls.AddRange(
            new AiTraderCall
            {
                ParticipantId = seed.AiParticipantId,
                ParticipantName = participant.Name,
                ProviderId = "glm",
                ProviderLabel = "GLM",
                Model = "model",
                SnapshotCycleId = 35,
                SnapshotCycleNumber = 35,
                PromptHash = "hash",
                RequestJson = "{}",
                ApplicationResultJson =
                    "{\"configurationStillCurrent\":true,\"cancellations\":[{\"applied\":false,\"rejectionReason\":\"Order is already closed.\"}],\"orders\":[{\"applied\":false,\"rejectionReason\":\"Quantity exceeds the current envelope.\"}],\"bigInvestment\":{\"applied\":false,\"rejectionReason\":\"Investment opportunity expired.\"}}",
                Status = AiTraderCallStatus.Completed,
                RequestedAt = DateTime.UtcNow,
            },
            new AiTraderCall
            {
                ParticipantId = rival.Id,
                ParticipantName = rival.Name,
                ProviderId = "glm",
                ProviderLabel = "GLM",
                Model = "model",
                SnapshotCycleId = 35,
                SnapshotCycleNumber = 35,
                PromptHash = "other-hash",
                RequestJson = "{}",
                Error = "Other participant error.",
                Status = AiTraderCallStatus.InvalidJson,
                RequestedAt = DateTime.UtcNow.AddSeconds(1),
            });
        await context.SaveChangesAsync();

        var snapshot = await Builder().BuildAsync(seed.AiParticipantId);

        Assert.NotNull(snapshot);
        var exposure = Assert.IsType<AiExposureSnapshot>(snapshot!.Participant.Exposure);
        Assert.Equal("Below", exposure.Position);
        Assert.Equal(35m, exposure.MinimumPercent);
        Assert.Equal(55m, exposure.MaximumPercent);

        var company = snapshot.Companies.Single(candidate => candidate.CompanyId == seed.Company2Id);
        Assert.Equal(2_000, company.IssuedShares);
        Assert.Equal(90m, company.BestExecutableSellPrice);
        Assert.Equal(20, company.BestExecutableSellQuantity);
        Assert.NotNull(company.BuyEnvelope);
        Assert.Equal(90m, company.BuyEnvelope!.OrderPrice);
        Assert.Equal(5, company.BuyEnvelope.MinimumQuantity);
        Assert.Equal(20, company.BuyEnvelope.MaximumQuantity);
        Assert.False(company.BuyEnvelope.IsPassive);

        var feedback = Assert.Single(snapshot.RecentApplicationFeedback);
        Assert.Equal(35, feedback.SnapshotCycleNumber);
        Assert.Contains("Quantity exceeds the current envelope.", feedback.RejectionReasons);
        Assert.Contains("Order is already closed.", feedback.RejectionReasons);
        Assert.Contains("Investment opportunity expired.", feedback.RejectionReasons);
        Assert.DoesNotContain(snapshot.RecentApplicationFeedback,
            item => item.RejectionReasons.Contains("Other participant error."));
    }

    [Fact]
    public async Task ExecutableAskAndEnvelopeUseSupplyResidualAfterOlderCrossingDemand()
    {
        var seed = await SeedThirtyFiveCycleMarketAsync();
        var ai = await context.Participants.SingleAsync(candidate => candidate.Id == seed.AiParticipantId);
        ai.CurrentBalance = 100_000m;
        ai.SettledCashBalance = 100_000m;
        ai.RiskProfile = RiskProfile.Medium;
        var buyer = await context.Participants.SingleAsync(candidate => candidate.Name == "Rival");
        var seller = new Participant
        {
            Name = "Seller",
            Type = ParticipantType.Individual,
            IsActive = true,
            CurrentBalance = 1_000m,
            SettledCashBalance = 1_000m,
        };
        context.Participants.Add(seller);
        await context.SaveChangesAsync();
        var cycleId = (await context.Markets.SingleAsync()).CurrentCycleId!.Value;
        var ask = new Order
        {
            ParticipantId = seller.Id,
            CompanyId = seed.Company2Id,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = 10,
            LimitPrice = 90m,
            CreatedInCycleId = cycleId,
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
        };
        var olderCrossingBuy = new Order
        {
            ParticipantId = buyer.Id,
            CompanyId = seed.Company2Id,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 10,
            LimitPrice = 90m,
            ReservedCashAmount = 900m,
            CreatedInCycleId = cycleId,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        context.Orders.AddRange(ask, olderCrossingBuy);
        buyer.ReservedBalance += 900m;
        await context.SaveChangesAsync();

        var fullyShadowed = await Builder().BuildAsync(seed.AiParticipantId);

        var fullyShadowedCompany = fullyShadowed!.Companies
            .Single(company => company.CompanyId == seed.Company2Id);
        Assert.Null(fullyShadowedCompany.BestExecutableSellPrice);
        Assert.Equal(0, fullyShadowedCompany.BestExecutableSellQuantity);
        Assert.NotNull(fullyShadowedCompany.BuyEnvelope);
        Assert.True(fullyShadowedCompany.BuyEnvelope!.IsPassive);

        olderCrossingBuy.Quantity = 4;
        olderCrossingBuy.ReservedCashAmount = 360m;
        buyer.ReservedBalance = 360m;
        await context.SaveChangesAsync();

        var partiallyShadowed = await Builder().BuildAsync(seed.AiParticipantId);

        var partiallyShadowedCompany = partiallyShadowed!.Companies
            .Single(company => company.CompanyId == seed.Company2Id);
        Assert.Equal(90m, partiallyShadowedCompany.BestExecutableSellPrice);
        Assert.Equal(6, partiallyShadowedCompany.BestExecutableSellQuantity);
        Assert.NotNull(partiallyShadowedCompany.BuyEnvelope);
        Assert.False(partiallyShadowedCompany.BuyEnvelope!.IsPassive);
        Assert.Equal(6, partiallyShadowedCompany.BuyEnvelope.MaximumQuantity);
    }

    [Fact]
    public async Task OpenOrdersTellTheModelWhichCancellationsAreServiceOwned()
    {
        var seed = await SeedThirtyFiveCycleMarketAsync();
        var ai = await context.Participants.SingleAsync(candidate => candidate.Id == seed.AiParticipantId);
        var market = await context.Markets.SingleAsync();
        var cycleId = market.CurrentCycleId!.Value;
        var account = new MarginAccount
        {
            ParticipantId = ai.Id,
            InitialMarginRate = 0.5m,
            MaintenanceMarginRate = 0.25m,
            Status = MarginAccountStatus.UnderCall,
        };
        var bank = new Bank { Name = "Snapshot bank", Balance = 1_000m };
        context.AddRange(account, bank);
        await context.SaveChangesAsync();
        var marginCall = new MarginCall
        {
            MarginAccountId = account.Id,
            OpenedInTradingDayId = market.CurrentTradingDayId!.Value,
            OpenedInCycleId = cycleId,
            Status = MarginCallStatus.Open,
            CreatedAt = DateTime.UtcNow,
        };
        var loan = new Loan
        {
            BankId = bank.Id,
            ParticipantId = ai.Id,
            Principal = 100m,
            RemainingPrincipal = 100m,
            TermTradingDays = 10,
            ScheduledInstallment = 10m,
            Status = LoanStatus.Open,
            OpenedInCycleId = cycleId,
            CreatedAt = DateTime.UtcNow,
        };
        context.AddRange(marginCall, loan);
        await context.SaveChangesAsync();
        context.Orders.AddRange(
            new Order
            {
                ParticipantId = ai.Id,
                CompanyId = seed.Company2Id,
                Type = OrderType.Sell,
                Status = OrderStatus.Open,
                Quantity = 1,
                LimitPrice = 90m,
                RelatedMarginCallId = marginCall.Id,
                CreatedInCycleId = cycleId,
            },
            new Order
            {
                ParticipantId = ai.Id,
                CompanyId = seed.Company2Id,
                Type = OrderType.Sell,
                Status = OrderStatus.Open,
                Quantity = 1,
                LimitPrice = 91m,
                RelatedLoanId = loan.Id,
                CreatedInCycleId = cycleId,
            });
        await context.SaveChangesAsync();

        var snapshot = await Builder().BuildAsync(seed.AiParticipantId);

        var ordinary = snapshot!.Participant.OpenOrders.Single(order => order.CompanyId == seed.Company1Id);
        Assert.True(ordinary.CanCancel);
        Assert.Null(ordinary.CancellationRestriction);
        var marginOwned = snapshot.Participant.OpenOrders.Single(order => order.LimitPrice == 90m);
        Assert.False(marginOwned.CanCancel);
        Assert.Equal("MarginCall", marginOwned.CancellationRestriction);
        var loanOwned = snapshot.Participant.OpenOrders.Single(order => order.LimitPrice == 91m);
        Assert.False(loanOwned.CanCancel);
        Assert.Equal("LoanDistress", loanOwned.CancellationRestriction);
    }

    [Fact]
    public async Task SnapshotDoesNotAdvertiseAnEnvelopeAboveEarlierDemandPriority()
    {
        var seed = await SeedPriorityCeilingScenarioAsync(includeResidualAsk: true);

        var snapshot = await Builder().BuildAsync(seed.AiParticipantId);

        var company = snapshot!.Companies.Single(candidate => candidate.CompanyId == seed.Company2Id);
        Assert.Equal("Below", snapshot.Participant.Exposure!.Position);
        Assert.Equal(91m, company.BestExecutableSellPrice);
        Assert.Equal(10, company.BestExecutableSellQuantity);
        Assert.Equal(90m, company.MaximumPrioritySafeBuyPrice);
        Assert.Null(company.BuyEnvelope);
    }

    [Fact]
    public async Task PriorityCeilingProvidesPassiveGuidanceWhenNoResidualAskExists()
    {
        var seed = await SeedPriorityCeilingScenarioAsync(includeResidualAsk: false);

        var snapshot = await Builder().BuildAsync(seed.AiParticipantId);

        var company = snapshot!.Companies.Single(candidate => candidate.CompanyId == seed.Company2Id);
        Assert.Equal(100m, company.CurrentPrice);
        Assert.Null(company.BestExecutableSellPrice);
        Assert.Equal(0, company.BestExecutableSellQuantity);
        Assert.Equal(90m, company.MaximumPrioritySafeBuyPrice);
        Assert.NotNull(company.BuyEnvelope);
        Assert.Equal(90m, company.BuyEnvelope!.OrderPrice);
        Assert.True(company.BuyEnvelope.IsPassive);
    }

    [Fact]
    public async Task WithinExposureUsesPassivePriorityCeilingBelowResidualAsk()
    {
        var seed = await SeedPriorityCeilingScenarioAsync(includeResidualAsk: true);
        var ai = await context.Participants.SingleAsync(candidate => candidate.Id == seed.AiParticipantId);
        ai.CurrentBalance = 16_500m;
        ai.SettledCashBalance = 16_500m;
        ai.ReservedBalance = 0m;
        var holding = await context.Holdings.SingleAsync(candidate =>
            candidate.ParticipantId == ai.Id && candidate.CompanyId == seed.Company1Id);
        holding.Quantity = 100;
        holding.SettledQuantity = 100;
        var ownOrder = await context.Orders.SingleAsync(candidate =>
            candidate.ParticipantId == ai.Id && candidate.Status == OrderStatus.Open);
        ownOrder.Status = OrderStatus.Cancelled;
        ownOrder.ReservedCashAmount = 0m;
        await context.SaveChangesAsync();

        var snapshot = await Builder().BuildAsync(seed.AiParticipantId);

        Assert.Equal("Within", snapshot!.Participant.Exposure!.Position);
        var company = snapshot.Companies.Single(candidate => candidate.CompanyId == seed.Company2Id);
        Assert.Equal(91m, company.BestExecutableSellPrice);
        Assert.Equal(90m, company.MaximumPrioritySafeBuyPrice);
        Assert.NotNull(company.BuyEnvelope);
        Assert.Equal(90m, company.BuyEnvelope!.OrderPrice);
        Assert.True(company.BuyEnvelope.IsPassive);
    }

    private AiMarketSnapshotBuilder Builder(bool bigInvestmentEnabled = true) => new(
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
        Options.Create(new VolatilityHaltOptions()),
        Options.Create(new BigInvestmentOptions { Enabled = bigInvestmentEnabled }),
        Options.Create(new RandomChanceRatesOptions()),
        new AutomatedBuyOrderPolicy(Options.Create(new AutomatedTradingOptions())));

    private async Task<MarketSeed> SeedPriorityCeilingScenarioAsync(bool includeResidualAsk)
    {
        var seed = await SeedThirtyFiveCycleMarketAsync();
        var ai = await context.Participants.SingleAsync(candidate => candidate.Id == seed.AiParticipantId);
        ai.CurrentBalance = 100_000m;
        ai.SettledCashBalance = 100_000m;
        ai.RiskProfile = RiskProfile.Medium;
        var buyer = await context.Participants.SingleAsync(candidate => candidate.Name == "Rival");
        var seller = new Participant
        {
            Name = "Priority seller",
            Type = ParticipantType.Individual,
            IsActive = true,
            CurrentBalance = 1_000m,
            SettledCashBalance = 1_000m,
        };
        context.Participants.Add(seller);
        await context.SaveChangesAsync();
        var cycleId = (await context.Markets.SingleAsync()).CurrentCycleId!.Value;
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = seed.Company2Id,
            Price = 100m,
            Capitalization = 200_000m,
            CreatedInCycleId = cycleId,
        });
        context.PriceBandStates.Add(new PriceBandState
        {
            CompanyId = seed.Company2Id,
            State = LuldState.Normal,
            ReferencePrice = 100m,
            LowerBandPrice = 85m,
            UpperBandPrice = 115m,
            UpdatedInCycleId = cycleId,
        });
        context.Orders.Add(new Order
        {
            ParticipantId = seller.Id,
            CompanyId = seed.Company2Id,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = 10,
            LimitPrice = 90m,
            CreatedInCycleId = cycleId,
        });
        if (includeResidualAsk)
        {
            context.Orders.Add(new Order
            {
                ParticipantId = seller.Id,
                CompanyId = seed.Company2Id,
                Type = OrderType.Sell,
                Status = OrderStatus.Open,
                Quantity = 10,
                LimitPrice = 91m,
                CreatedInCycleId = cycleId,
            });
        }
        context.Orders.Add(new Order
        {
            ParticipantId = buyer.Id,
            CompanyId = seed.Company2Id,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 10,
            LimitPrice = 90m,
            ReservedCashAmount = 900m,
            CreatedInCycleId = cycleId,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
        });
        buyer.ReservedBalance += 900m;
        await context.SaveChangesAsync();
        return seed;
    }

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
