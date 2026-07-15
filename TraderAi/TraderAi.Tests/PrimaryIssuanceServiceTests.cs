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
    public async Task ScarceFloatIssuesTheIndividualsUnmetDemandAtTheCurrentPrice()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var buyer = await AddParticipantAsync(ParticipantType.Individual);
        AddBuy(buyer, company, quantity: 120, limitPrice: 105m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_120, company.IssuedSharesCount);
        var order = await context.Orders.AsNoTracking().SingleAsync(candidate => candidate.ParticipantId == null);
        Assert.Equal(OrderType.Sell, order.Type);
        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Equal(120, order.Quantity);
        Assert.Equal(100m, order.LimitPrice);
        Assert.True(order.IsFloatReplenishment);
    }

    [Fact]
    public async Task NoEligibleDemandDoesNotIssue()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_000, company.IssuedSharesCount);
        Assert.Empty(await context.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AiAgentDemandCounts()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var buyer = await AddParticipantAsync(ParticipantType.AIAgent);
        AddBuy(buyer, company, quantity: 37, limitPrice: 100m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_037, company.IssuedSharesCount);
        Assert.Equal(37, await IssuedQuantityAsync());
    }

    [Theory]
    [InlineData(ParticipantType.CollectiveFund)]
    [InlineData(ParticipantType.Player)]
    public async Task FundAndPlayerDemandDoesNotTriggerIssuance(ParticipantType participantType)
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var buyer = await AddParticipantAsync(participantType);
        AddBuy(buyer, company, quantity: 100, limitPrice: 100m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_000, company.IssuedSharesCount);
        Assert.DoesNotContain(await context.Orders.AsNoTracking().ToListAsync(), order => order.ParticipantId == null);
    }

    [Fact]
    public async Task OutOfBandAndNonCrossingBuysDoNotTriggerIssuance()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var outOfBandBuyer = await AddParticipantAsync(ParticipantType.Individual);
        var nonCrossingBuyer = await AddParticipantAsync(ParticipantType.AIAgent);
        AddBuy(outOfBandBuyer, company, quantity: 100, limitPrice: 116m, cycle.Id);
        AddBuy(nonCrossingBuyer, company, quantity: 100, limitPrice: 99m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_000, company.IssuedSharesCount);
        Assert.DoesNotContain(await context.Orders.AsNoTracking().ToListAsync(), order => order.ParticipantId == null);
    }

    [Fact]
    public async Task CompatibleSellSupplyPartiallySubtractsEligibleDemand()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var buyer = await AddParticipantAsync(ParticipantType.Individual);
        AddBuy(buyer, company, quantity: 100, limitPrice: 110m, cycle.Id);
        AddSell(company, quantity: 40, limitPrice: 105m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_060, company.IssuedSharesCount);
        Assert.Equal(60, await IssuedQuantityAsync());
    }

    [Fact]
    public async Task CompatibleSellSupplyCanFullySatisfyEligibleDemand()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var buyer = await AddParticipantAsync(ParticipantType.AIAgent);
        AddBuy(buyer, company, quantity: 100, limitPrice: 100m, cycle.Id);
        AddSell(company, quantity: 100, limitPrice: 100m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_000, company.IssuedSharesCount);
        Assert.DoesNotContain(await context.Orders.AsNoTracking().ToListAsync(), order => order.IsFloatReplenishment);
    }

    [Fact]
    public async Task HigherInBandAskDoesNotSubtractFromALowerLimitBuy()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var buyer = await AddParticipantAsync(ParticipantType.Individual);
        AddBuy(buyer, company, quantity: 100, limitPrice: 100m, cycle.Id);
        AddSell(company, quantity: 100, limitPrice: 110m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_100, company.IssuedSharesCount);
        Assert.Equal(100, await IssuedQuantityAsync());
    }

    [Fact]
    public async Task ShadowMatchingUsesTheMatchingEnginePricePriority()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var highBuyer = await AddParticipantAsync(ParticipantType.Individual);
        var lowBuyer = await AddParticipantAsync(ParticipantType.AIAgent);
        AddBuy(highBuyer, company, quantity: 1, limitPrice: 110m, cycle.Id, createdAt: DateTime.UtcNow.AddSeconds(-2));
        AddBuy(lowBuyer, company, quantity: 1, limitPrice: 100m, cycle.Id, createdAt: DateTime.UtcNow.AddSeconds(-1));
        AddSell(company, quantity: 1, limitPrice: 90m, cycle.Id);
        AddSell(company, quantity: 1, limitPrice: 105m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_001, company.IssuedSharesCount);
        Assert.Equal(1, await IssuedQuantityAsync());
    }

    [Fact]
    public async Task NewerOwnDistressSellIsCancelledBeforeTheBuyFillsFromIssuedSupply()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(
            issuedShares: 1_000,
            price: 100m,
            cycle.Id,
            heldShares: 850);
        var buyer = await AddParticipantAsync(ParticipantType.Individual);
        AddHolding(buyer, company, quantity: 100, averageCost: 90m);
        var timestamp = DateTime.UtcNow;
        var buy = AddBuy(
            buyer,
            company,
            quantity: 100,
            limitPrice: 105m,
            cycle.Id,
            createdAt: timestamp.AddSeconds(-2));
        var loan = await AddLoanAsync(buyer, cycle.Id);
        var ownSell = AddSell(
            company,
            quantity: 100,
            limitPrice: 100m,
            cycle.Id,
            participant: buyer,
            createdAt: timestamp.AddSeconds(-1),
            relatedLoanId: loan.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, timestamp);
        await context.SaveChangesAsync();

        var issuerOrder = await context.Orders.SingleAsync(order => order.IsFloatReplenishment);
        Assert.Equal(100, issuerOrder.Quantity);

        var fills = await new MatchingEngine(context).RunAsync(cycle);
        await context.SaveChangesAsync();

        Assert.Equal(1, fills);
        Assert.Equal(OrderStatus.Cancelled, ownSell.Status);
        Assert.Equal(OrderStatus.Filled, buy.Status);
        Assert.Equal(OrderStatus.Filled, issuerOrder.Status);
        Assert.Equal(
            200,
            await context.Holdings
                .Where(holding => holding.ParticipantId == buyer.Id && holding.CompanyId == company.Id)
                .Select(holding => holding.Quantity)
                .SingleAsync());
    }

    [Fact]
    public async Task NewerOwnBuyIsCancelledWithoutTriggeringIssuance()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(
            issuedShares: 1_000,
            price: 100m,
            cycle.Id,
            heldShares: 850);
        var buyer = await AddParticipantAsync(ParticipantType.AIAgent);
        AddHolding(buyer, company, quantity: 100, averageCost: 90m);
        var timestamp = DateTime.UtcNow;
        var ownSell = AddSell(
            company,
            quantity: 100,
            limitPrice: 100m,
            cycle.Id,
            participant: buyer,
            createdAt: timestamp.AddSeconds(-2));
        var buy = AddBuy(
            buyer,
            company,
            quantity: 100,
            limitPrice: 105m,
            cycle.Id,
            createdAt: timestamp.AddSeconds(-1));
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, timestamp);
        await context.SaveChangesAsync();

        Assert.DoesNotContain(await context.Orders.ToListAsync(), order => order.IsFloatReplenishment);

        var fills = await new MatchingEngine(context).RunAsync(cycle);
        await context.SaveChangesAsync();

        Assert.Equal(0, fills);
        Assert.Equal(OrderStatus.Open, ownSell.Status);
        Assert.Equal(OrderStatus.Cancelled, buy.Status);
        Assert.Equal(0m, buyer.ReservedBalance);
    }

    [Theory]
    [InlineData(LuldState.LimitState)]
    [InlineData(LuldState.TradingPause)]
    [InlineData(LuldState.Reopening)]
    public async Task NonNormalLuldStateDefersIssuanceWithoutConsumingTheCooldown(LuldState luldState)
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var band = await context.PriceBandStates.SingleAsync(state => state.CompanyId == company.Id);
        band.State = luldState;
        var buyer = await AddParticipantAsync(ParticipantType.Individual);
        AddBuy(buyer, company, quantity: 100, limitPrice: 100m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_000, company.IssuedSharesCount);
        Assert.DoesNotContain(await context.Orders.ToListAsync(), order => order.IsFloatReplenishment);

        band.State = LuldState.Normal;
        await context.SaveChangesAsync();
        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_100, company.IssuedSharesCount);
        Assert.Equal(100, await IssuedQuantityAsync());
    }

    [Fact]
    public async Task PartiallyFilledBuyContributesOnlyItsRemainingQuantity()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var buyer = await AddParticipantAsync(ParticipantType.Individual);
        AddBuy(
            buyer,
            company,
            quantity: 100,
            limitPrice: 100m,
            cycle.Id,
            status: OrderStatus.PartiallyFilled,
            filledQuantity: 30);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_070, company.IssuedSharesCount);
        Assert.Equal(70, await IssuedQuantityAsync());
    }

    [Fact]
    public async Task DailyCapRoundsUpToWholeShares()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 101, price: 100m, cycle.Id, heldShares: 100);
        var buyer = await AddParticipantAsync(ParticipantType.Individual);
        AddBuy(buyer, company, quantity: 100, limitPrice: 100m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(127, company.IssuedSharesCount);
        Assert.Equal(26, await IssuedQuantityAsync());
    }

    [Fact]
    public async Task ExistingPrimaryOfferOnTheTradingDayPreventsAnotherIssuance()
    {
        var firstCycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var secondCycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 2);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, firstCycle.Id);
        var buyer = await AddParticipantAsync(ParticipantType.Individual);
        AddBuy(buyer, company, quantity: 100, limitPrice: 100m, secondCycle.Id);
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

        await Service(enabled: true).ProcessForCycleAsync(secondCycle.Id, secondCycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_000, company.IssuedSharesCount);
        Assert.Single(await context.Orders.AsNoTracking().Where(order => order.ParticipantId == null).ToListAsync());
    }

    [Fact]
    public async Task DisabledServiceDoesNotIssue()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var buyer = await AddParticipantAsync(ParticipantType.Individual);
        AddBuy(buyer, company, quantity: 100, limitPrice: 100m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: false).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_000, company.IssuedSharesCount);
        Assert.DoesNotContain(await context.Orders.AsNoTracking().ToListAsync(), order => order.ParticipantId == null);
    }

    [Fact]
    public async Task FloatAtTheScarcityThresholdDoesNotIssue()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id, heldShares: 900);
        var buyer = await AddParticipantAsync(ParticipantType.AIAgent);
        AddBuy(buyer, company, quantity: 100, limitPrice: 100m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_000, company.IssuedSharesCount);
        Assert.DoesNotContain(await context.Orders.AsNoTracking().ToListAsync(), order => order.ParticipantId == null);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task InactiveOrBankruptAutomatedOwnerDoesNotTriggerIssuance(bool isActive, bool isBankrupt)
    {
        var cycle = await AddCycleAsync(dayNumber: 1, cycleNumber: 1);
        var company = await AddScarceCompanyAsync(issuedShares: 1_000, price: 100m, cycle.Id);
        var buyer = await AddParticipantAsync(ParticipantType.Individual, isActive, isBankrupt);
        AddBuy(buyer, company, quantity: 100, limitPrice: 100m, cycle.Id);
        await context.SaveChangesAsync();

        await Service(enabled: true).ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1_000, company.IssuedSharesCount);
        Assert.DoesNotContain(await context.Orders.AsNoTracking().ToListAsync(), order => order.ParticipantId == null);
    }

    [Fact]
    public void ConstructorDoesNotDependOnRandom()
    {
        var parameters = typeof(PrimaryIssuanceService).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(parameters, parameter => typeof(Random).IsAssignableFrom(parameter.ParameterType));
        Assert.DoesNotContain(
            parameters,
            parameter => parameter.ParameterType == typeof(IOptions<RandomChanceRatesOptions>));
    }

    private PrimaryIssuanceService Service(bool enabled) => new(
        context,
        Options.Create(new PrimaryIssuanceOptions
        {
            Enabled = enabled,
            FloatScarcityThresholdPercent = 10m,
            MaximumDailyIssuancePercent = 25m,
        }),
        Options.Create(new VolatilityHaltOptions()));

    private async Task<int> IssuedQuantityAsync() => await context.Orders
        .Where(order => order.IsFloatReplenishment)
        .Select(order => order.Quantity)
        .SingleAsync();

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

    private async Task<Company> AddScarceCompanyAsync(
        int issuedShares,
        decimal price,
        int cycleId,
        int? heldShares = null)
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
        context.PriceBandStates.Add(new PriceBandState
        {
            CompanyId = company.Id,
            State = LuldState.Normal,
            ReferencePrice = price,
            LowerBandPrice = 90m,
            UpperBandPrice = 115m,
            UpdatedInCycleId = cycleId,
        });

        var holder = await AddParticipantAsync(ParticipantType.Individual);
        context.Holdings.Add(new Holding
        {
            ParticipantId = holder.Id,
            CompanyId = company.Id,
            Quantity = heldShares ?? Math.Max(1, issuedShares - Math.Max(1, issuedShares / 20)),
            SettledQuantity = heldShares ?? Math.Max(1, issuedShares - Math.Max(1, issuedShares / 20)),
            AverageCost = price,
        });
        await context.SaveChangesAsync();
        return company;
    }

    private async Task<Participant> AddParticipantAsync(
        ParticipantType type,
        bool isActive = true,
        bool isBankrupt = false)
    {
        var participant = new Participant
        {
            Name = $"Trader {Guid.NewGuid():N}",
            Type = type,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 1_000_000m,
            CurrentBalance = 1_000_000m,
            SettledCashBalance = 1_000_000m,
            IsActive = isActive,
            IsBankrupt = isBankrupt,
        };
        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        return participant;
    }

    private Order AddBuy(
        Participant buyer,
        Company company,
        int quantity,
        decimal limitPrice,
        int cycleId,
        DateTime? createdAt = null,
        OrderStatus status = OrderStatus.Open,
        int filledQuantity = 0)
    {
        var timestamp = createdAt ?? DateTime.UtcNow;
        var order = new Order
        {
            ParticipantId = buyer.Id,
            CompanyId = company.Id,
            Type = OrderType.Buy,
            Status = status,
            Quantity = quantity,
            FilledQuantity = filledQuantity,
            LimitPrice = limitPrice,
            ReservedCashAmount = limitPrice * (quantity - filledQuantity),
            CreatedInCycleId = cycleId,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
        buyer.ReservedBalance += order.ReservedCashAmount;
        context.Orders.Add(order);
        return order;
    }

    private Order AddSell(
        Company company,
        int quantity,
        decimal limitPrice,
        int cycleId,
        Participant? participant = null,
        DateTime? createdAt = null,
        int? relatedLoanId = null)
    {
        var timestamp = createdAt ?? DateTime.UtcNow;
        var order = new Order
        {
            ParticipantId = participant?.Id,
            CompanyId = company.Id,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = quantity,
            LimitPrice = limitPrice,
            RelatedLoanId = relatedLoanId,
            CreatedInCycleId = cycleId,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
        context.Orders.Add(order);
        return order;
    }

    private void AddHolding(Participant participant, Company company, int quantity, decimal averageCost)
    {
        context.Holdings.Add(new Holding
        {
            ParticipantId = participant.Id,
            CompanyId = company.Id,
            Quantity = quantity,
            SettledQuantity = quantity,
            AverageCost = averageCost,
        });
    }

    private async Task<Loan> AddLoanAsync(Participant participant, int cycleId)
    {
        var bank = new Bank { Name = $"Bank {Guid.NewGuid():N}", Balance = 1_000_000m };
        context.Banks.Add(bank);
        await context.SaveChangesAsync();
        var loan = new Loan
        {
            BankId = bank.Id,
            ParticipantId = participant.Id,
            Principal = 1_000m,
            RemainingPrincipal = 1_000m,
            TermCycles = 10,
            ScheduledInstallment = 100m,
            Status = LoanStatus.Open,
            OpenedInCycleId = cycleId,
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
