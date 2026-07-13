using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Tests;

// Builds a small, fixed market fixture (one company, one share-holding seller, one cash buyer) so the
// decision and loop tests can assert exact outcomes independently of the demo seed's generated data.
internal static class TestMarketSeed
{
    public static async Task SeedClassicScenarioAsync(AppDbContext context)
    {
        var now = DateTime.UtcNow;

        var cycle = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);

        var market = new Market { Name = "Demo Market", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(market);

        var industry = new Industry { Name = "Software Development" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();

        var company = new Company
        {
            Name = "Acme Corp",
            IndustryId = industry.Id,
            IssuedSharesCount = 10,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Companies.Add(company);

        var seller = new Participant
        {
            Name = "Alice",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 1000m,
            CurrentBalance = 1000m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        var buyer = new Participant
        {
            Name = "Bob",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Aggressive,
            RiskProfile = RiskProfile.High,
            InitialBalance = 5000m,
            CurrentBalance = 5000m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(seller);
        context.Participants.Add(buyer);

        await context.SaveChangesAsync();

        context.Holdings.Add(new Holding
        {
            ParticipantId = seller.Id,
            CompanyId = company.Id,
            Quantity = 10,
            AverageCost = 100m,
        });

        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = 100m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });

        market.CurrentCycleId = cycle.Id;
        await context.SaveChangesAsync();
    }

    public static async Task<AccountingMarketSeed> SeedAccountingScenarioAsync(AppDbContext context)
    {
        var now = DateTime.UtcNow;
        var day = new TradingDay
        {
            DayNumber = 1,
            State = TradingSessionState.Trading,
            OpenedInCycleId = 0,
        };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();

        var cycle = new MarketCycle
        {
            CycleNumber = 1,
            TradingDayId = day.Id,
            TradingCycleNumber = 1,
            Status = CycleStatus.Running,
            StartedAt = now,
        };
        var market = new Market
        {
            Name = "Accounting Market",
            Status = MarketStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var industry = new Industry { Name = "Accounting Industry" };
        context.AddRange(cycle, market, industry);
        await context.SaveChangesAsync();

        var company = new Company
        {
            Name = "Accounting Company",
            IndustryId = industry.Id,
            IssuedSharesCount = 100,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var seller = Participant("Accounting Seller", 1_000m, ParticipantType.Individual);
        var buyer = Participant("Accounting Buyer", 5_000m, ParticipantType.Player);
        var bank = new Bank
        {
            Name = "National bank",
            InterestRatePerCycle = 0.001m,
        };
        context.AddRange(company, seller, buyer, bank);
        await context.SaveChangesAsync();

        context.Holdings.Add(new Holding
        {
            ParticipantId = seller.Id,
            CompanyId = company.Id,
            Quantity = 20,
            SettledQuantity = 20,
            AverageCost = 100m,
        });
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = 100m,
            Capitalization = 10_000m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });
        day.OpenedInCycleId = cycle.Id;
        market.CurrentCycleId = cycle.Id;
        market.CurrentTradingDayId = day.Id;
        await context.SaveChangesAsync();

        return new AccountingMarketSeed(market, day, cycle, company, seller, buyer, bank);
    }

    private static Participant Participant(string name, decimal cash, ParticipantType type) =>
        new()
        {
            Name = name,
            Type = type,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = cash,
            CurrentBalance = cash,
            SettledCashBalance = cash,
            IsActive = true,
        };
}

internal sealed record AccountingMarketSeed(
    Market Market,
    TradingDay Day,
    MarketCycle Cycle,
    Company Company,
    Participant Seller,
    Participant Buyer,
    Bank Bank);
