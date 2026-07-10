using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Exercises the trade transaction fee: a participant seller's proceeds are skimmed by FeeRate into the bank
// balance, company-float sells are exempt, and a disabled fee leaves proceeds whole.
public sealed class TradeFeeTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public TradeFeeTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private MarketService BuildService(bool feeEnabled, decimal feeRate = 0.01m)
    {
        var feeOptions = Options.Create(new TradeFeeOptions { Enabled = feeEnabled, FeeRate = feeRate });
        return new MarketService(
            context,
            new MatchingEngine(context, feeOptions),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1));
    }

    [Fact]
    public async Task FeeIsDeductedFromSellerProceedsAndAccruesToBank()
    {
        var service = BuildService(feeEnabled: true, feeRate: 0.01m);
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        // Both limits sit at 100, so execution is 100 and the trade value is 1000; the 1% fee is 10.
        await service.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 10, 100m);
        await service.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 10, 100m);

        var result = await service.AdvanceCycleAsync();
        Assert.Equal(1, result.FillCount);

        await context.Entry(seed.Seller).ReloadAsync();
        await context.Entry(seed.Buyer).ReloadAsync();

        // Seller keeps 1000 sale proceeds minus the 10 fee; the buyer is untouched by the fee.
        Assert.Equal(1990m, seed.Seller.CurrentBalance);
        Assert.Equal(4000m, seed.Buyer.CurrentBalance);

        var bank = await context.Banks.SingleAsync();
        Assert.Equal(10m, bank.Balance);

        var credit = await context.MoneyTransactions
            .SingleAsync(money => money.ParticipantId == seed.Seller.Id && money.Type == MoneyTransactionType.Credit);
        Assert.Equal(1000m, credit.Amount);

        var fee = await context.MoneyTransactions
            .SingleAsync(money => money.Type == MoneyTransactionType.TradeFee);
        Assert.Equal(seed.Seller.Id, fee.ParticipantId);
        Assert.Equal(10m, fee.Amount);
    }

    [Fact]
    public async Task DisabledFeeLeavesProceedsWholeAndTouchesNoBank()
    {
        var service = BuildService(feeEnabled: false);
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 10, sharePrice: 100m);

        await service.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 10, 100m);
        await service.PlaceOrderAsync(seed.Seller.Id, seed.Company.Id, OrderType.Sell, 10, 100m);

        await service.AdvanceCycleAsync();

        await context.Entry(seed.Seller).ReloadAsync();
        Assert.Equal(2000m, seed.Seller.CurrentBalance);
        Assert.Empty(context.Banks);
        Assert.Equal(0, await context.MoneyTransactions.CountAsync(money => money.Type == MoneyTransactionType.TradeFee));
    }

    [Fact]
    public async Task CompanyFloatSellCarriesNoFee()
    {
        var service = BuildService(feeEnabled: true, feeRate: 0.01m);
        var seed = await SeedAsync(sellerCash: 1000m, buyerCash: 5000m, sellerShares: 0, sharePrice: 100m);

        // A company-originated (null-participant) offer lists the issuer float; primary issuance pays no fee.
        seed.Company.IssuedSharesCount = 10;
        context.Orders.Add(new Order
        {
            ParticipantId = null,
            CompanyId = seed.Company.Id,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = 10,
            LimitPrice = 100m,
            CreatedInCycleId = seed.Market.CurrentCycleId!.Value,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        await service.PlaceOrderAsync(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 10, 100m);

        var result = await service.AdvanceCycleAsync();
        Assert.Equal(1, result.FillCount);

        await context.Entry(seed.Buyer).ReloadAsync();
        Assert.Equal(4000m, seed.Buyer.CurrentBalance);
        Assert.Empty(context.Banks);
        Assert.Equal(0, await context.MoneyTransactions.CountAsync(money => money.Type == MoneyTransactionType.TradeFee));
    }

    private async Task<SeedResult> SeedAsync(decimal sellerCash, decimal buyerCash, int sellerShares, decimal sharePrice)
    {
        var now = DateTime.UtcNow;

        var cycle = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);

        var market = new Market { Name = "Test Market", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(market);

        var company = new Company { Name = "Test Co", IssuedSharesCount = sellerShares, CreatedAt = now, UpdatedAt = now };
        context.Companies.Add(company);

        var seller = new Participant
        {
            Name = "Seller",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = sellerCash,
            CurrentBalance = sellerCash,
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
            IsActive = true,
        };
        context.Participants.Add(seller);
        context.Participants.Add(buyer);

        await context.SaveChangesAsync();

        if (sellerShares > 0)
        {
            context.Holdings.Add(new Holding
            {
                ParticipantId = seller.Id,
                CompanyId = company.Id,
                Quantity = sellerShares,
                AverageCost = sharePrice,
            });
        }

        market.CurrentCycleId = cycle.Id;
        await context.SaveChangesAsync();

        return new SeedResult(market, company, seller, buyer);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed record SeedResult(Market Market, Company Company, Participant Seller, Participant Buyer);
}
