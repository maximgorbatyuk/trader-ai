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
}
