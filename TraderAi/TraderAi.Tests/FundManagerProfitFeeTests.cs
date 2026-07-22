using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Exercises the immediate manager profit fee: when a fund fills a sell for a gain over the shares' cost basis,
// its founder is paid a share of that gain, funded by debiting the fund so the ledger stays reconciled. A loss,
// a disabled fee, and a non-fund seller each pay nothing.
public sealed class FundManagerProfitFeeTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public FundManagerProfitFeeTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private MarketService BuildService(bool feeEnabled, decimal feeShare = 0.10m)
    {
        var engine = feeEnabled
            ? new MatchingEngine(
                context,
                collectiveFundOptions: Options.Create(new CollectiveFundOptions
                {
                    ManagerProfitFeeEnabled = true,
                    ManagerProfitFeeShare = feeShare,
                }))
            : new MatchingEngine(context);
        return new MarketService(context, engine, new NoOpDecisionEngine(), new MarketCycleLock(), new Random(1));
    }

    [Fact]
    public async Task ProfitableFundSalePaysManagerAndDebitsTheFund()
    {
        var service = BuildService(feeEnabled: true);
        var seed = await SeedAsync(fundCash: 1000m, managerCash: 500m, buyerCash: 5000m,
            fundShares: 10, holdingCost: 100m, marketPrice: 150m, isFund: true);

        // Both limits sit at 150, so execution is 150: proceeds 1500 over a 1000 cost basis is a 500 gain, and
        // the 10% manager fee is 50.
        await service.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 10, 150m);
        await service.PlaceOrderAsync(seed.Fund.Id, seed.Company.Id, OrderType.Sell, 10, 150m);

        var result = await service.AdvanceCycleAsync();
        Assert.Equal(1, result.FillCount);

        await context.Entry(seed.Fund).ReloadAsync();
        await context.Entry(seed.Manager).ReloadAsync();

        // The fund keeps its full 1500 proceeds minus the 50 fee; the manager receives exactly that 50.
        Assert.Equal(1000m + 1500m - 50m, seed.Fund.CurrentBalance);
        Assert.Equal(1000m - 50m, seed.Fund.SettledCashBalance);
        Assert.Equal(500m + 50m, seed.Manager.CurrentBalance);
        Assert.Equal(500m + 50m, seed.Manager.SettledCashBalance);

        var feePaid = await context.MoneyTransactions
            .SingleAsync(money => money.Type == MoneyTransactionType.CollectiveFundManagerFee);
        Assert.Equal(seed.Fund.Id, feePaid.ParticipantId);
        Assert.Equal(50m, feePaid.Amount);

        var feeReceived = await context.MoneyTransactions
            .SingleAsync(money => money.Type == MoneyTransactionType.CollectiveFundManagerFeeReceived);
        Assert.Equal(seed.Manager.Id, feeReceived.ParticipantId);
        Assert.Equal(seed.Fund.Id, feeReceived.FromWhomId);
        Assert.Equal(50m, feeReceived.Amount);

        // Nothing is created: the debit off the fund equals the credit to the manager.
        Assert.Equal(feePaid.Amount, feeReceived.Amount);

        var transaction = await context.ShareTransactions.SingleAsync();
        Assert.Equal(100m, transaction.SellerAverageCost);
        Assert.Equal(1000m, transaction.SellerCostBasis);
        Assert.Equal(0m, transaction.SellerTradeFee);
        Assert.Equal(50m, transaction.SellerManagerFee);
        Assert.Equal(500m, transaction.SellerGrossRealizedPnl);
        Assert.Equal(450m, transaction.SellerNetRealizedPnl);
    }

    [Fact]
    public async Task LossMakingFundSalePaysNoManagerFee()
    {
        var service = BuildService(feeEnabled: true);
        var seed = await SeedAsync(fundCash: 1000m, managerCash: 500m, buyerCash: 5000m,
            fundShares: 10, holdingCost: 200m, marketPrice: 150m, isFund: true);

        // Proceeds of 1500 sit below the 2000 cost basis, so the fill is a loss and pays the manager nothing.
        await service.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 10, 150m);
        await service.PlaceOrderAsync(seed.Fund.Id, seed.Company.Id, OrderType.Sell, 10, 150m);

        var result = await service.AdvanceCycleAsync();
        Assert.Equal(1, result.FillCount);

        await context.Entry(seed.Fund).ReloadAsync();
        await context.Entry(seed.Manager).ReloadAsync();

        Assert.Equal(1000m + 1500m, seed.Fund.CurrentBalance);
        Assert.Equal(500m, seed.Manager.CurrentBalance);
        Assert.Empty(context.MoneyTransactions.Where(money =>
            money.Type == MoneyTransactionType.CollectiveFundManagerFee
            || money.Type == MoneyTransactionType.CollectiveFundManagerFeeReceived));
    }

    [Fact]
    public async Task DisabledFeeLeavesProceedsWhole()
    {
        var service = BuildService(feeEnabled: false);
        var seed = await SeedAsync(fundCash: 1000m, managerCash: 500m, buyerCash: 5000m,
            fundShares: 10, holdingCost: 100m, marketPrice: 150m, isFund: true);

        await service.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 10, 150m);
        await service.PlaceOrderAsync(seed.Fund.Id, seed.Company.Id, OrderType.Sell, 10, 150m);

        await service.AdvanceCycleAsync();

        await context.Entry(seed.Fund).ReloadAsync();
        await context.Entry(seed.Manager).ReloadAsync();

        Assert.Equal(1000m + 1500m, seed.Fund.CurrentBalance);
        Assert.Equal(500m, seed.Manager.CurrentBalance);
        Assert.Empty(context.MoneyTransactions.Where(money =>
            money.Type == MoneyTransactionType.CollectiveFundManagerFee
            || money.Type == MoneyTransactionType.CollectiveFundManagerFeeReceived));
    }

    [Fact]
    public async Task PartialFillPaysFeeOnFilledQuantityOnly()
    {
        var service = BuildService(feeEnabled: true);
        var seed = await SeedAsync(fundCash: 1000m, managerCash: 500m, buyerCash: 5000m,
            fundShares: 10, holdingCost: 100m, marketPrice: 150m, isFund: true);

        // Only 4 of the 10 offered shares fill: the 200 gain on those four yields a 20 fee.
        await service.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 4, 150m);
        await service.PlaceOrderAsync(seed.Fund.Id, seed.Company.Id, OrderType.Sell, 10, 150m);

        await service.AdvanceCycleAsync();

        await context.Entry(seed.Manager).ReloadAsync();
        Assert.Equal(500m + 20m, seed.Manager.CurrentBalance);

        var feePaid = await context.MoneyTransactions
            .SingleAsync(money => money.Type == MoneyTransactionType.CollectiveFundManagerFee);
        Assert.Equal(20m, feePaid.Amount);
    }

    [Fact]
    public async Task NonFundSellerPaysNoManagerFee()
    {
        var service = BuildService(feeEnabled: true);
        var seed = await SeedAsync(fundCash: 1000m, managerCash: 500m, buyerCash: 5000m,
            fundShares: 10, holdingCost: 100m, marketPrice: 150m, isFund: false);

        await service.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 10, 150m);
        await service.PlaceOrderAsync(seed.Fund.Id, seed.Company.Id, OrderType.Sell, 10, 150m);

        await service.AdvanceCycleAsync();

        Assert.Empty(context.MoneyTransactions.Where(money =>
            money.Type == MoneyTransactionType.CollectiveFundManagerFee
            || money.Type == MoneyTransactionType.CollectiveFundManagerFeeReceived));
    }

    private async Task<SeedResult> SeedAsync(
        decimal fundCash, decimal managerCash, decimal buyerCash,
        int fundShares, decimal holdingCost, decimal marketPrice, bool isFund)
    {
        var now = DateTime.UtcNow;

        var cycle = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);

        var market = new Market { Name = "Test Market", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(market);

        var company = new Company { Name = "Test Co", IssuedSharesCount = fundShares, CreatedAt = now, UpdatedAt = now };
        context.Companies.Add(company);

        // The seller: either a collective fund's trading participant, or an ordinary individual for the
        // non-fund guard. Settled cash mirrors current cash so the affordability check has real settled funds.
        var fundParticipant = new Participant
        {
            Name = "Fund",
            Type = isFund ? ParticipantType.CollectiveFund : ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = fundCash,
            CurrentBalance = fundCash,
            SettledCashBalance = fundCash,
            IsActive = true,
        };
        var manager = new Participant
        {
            Name = "Manager",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = managerCash,
            CurrentBalance = managerCash,
            SettledCashBalance = managerCash,
            IsActive = true,
        };
        var buyer = new Participant
        {
            Name = "Buyer",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Aggressive,
            RiskProfile = RiskProfile.High,
            InitialBalance = buyerCash,
            CurrentBalance = buyerCash,
            SettledCashBalance = buyerCash,
            IsActive = true,
        };
        context.Participants.Add(fundParticipant);
        context.Participants.Add(manager);
        context.Participants.Add(buyer);

        await context.SaveChangesAsync();

        if (isFund)
        {
            context.CollectiveFunds.Add(new CollectiveFund
            {
                ParticipantId = fundParticipant.Id,
                FoundedByParticipantId = manager.Id,
                Status = CollectiveFundStatus.Active,
                CreatedInCycleId = cycle.Id,
                CreatedAt = now,
            });
        }

        context.Holdings.Add(new Holding
        {
            ParticipantId = fundParticipant.Id,
            CompanyId = company.Id,
            Quantity = fundShares,
            AverageCost = holdingCost,
        });

        // A listed company always carries a price snapshot; order entry bounds new orders against it.
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = marketPrice,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });

        market.CurrentCycleId = cycle.Id;
        await context.SaveChangesAsync();

        return new SeedResult(market, company, fundParticipant, manager, buyer);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed record SeedResult(Market Market, Company Company, Participant Fund, Participant Manager, Participant Buyer);
}
