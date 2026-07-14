using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Exercises the exit hooks inside a real MarketService tick with both the fund and exit services wired. It proves
// the save-fence ordering: a devastated fund member is flagged when its fund closes, that flag is persisted
// before the exit rolls read the database, and the member departs and is replaced within the same RunCycleTickAsync.
public sealed class MarketExitIntegrationTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public MarketExitIntegrationTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task DevastatedMemberIsFlaggedRolledAndReplacedInOneTick()
    {
        var member = await SeedClosingFundWithDevastatedMemberAsync();

        var random = new FixedRandom();
        var loanOptions = Options.Create(new LoanOptions { Enabled = false });
        var fundService = new CollectiveFundService(
            context, Options.Create(new CollectiveFundOptions { Enabled = true }), Options.Create(new RandomChanceRatesOptions()),
            loanOptions, new LoanService(context, loanOptions), random);
        var exitService = new MarketExitService(
            context, Options.Create(new MarketExitOptions { Enabled = true }), Options.Create(new RandomChanceRatesOptions()), random);
        var marketService = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            random,
            collectiveFundService: fundService,
            marketExitService: exitService);

        await marketService.RunCycleTickAsync();

        // The fund unwound and flagged the member; the exit rolls, reading the settled flag, made it depart.
        var fund = await context.CollectiveFunds.AsNoTracking().SingleAsync();
        Assert.Equal(CollectiveFundStatus.Closed, fund.Status);
        Assert.False(await context.Participants.AnyAsync(participant => participant.Id == member.Id));

        var exit = await context.MarketExits.AsNoTracking().SingleAsync();
        Assert.Equal(MarketExitReason.FundLoss, exit.Reason);
        Assert.Equal(member.Id, exit.ParticipantId);

        // The replacement joined this same tick and is the only ordinary trader left standing.
        var replacement = await context.Participants.AsNoTracking().SingleAsync(participant => participant.Type == ParticipantType.Individual);
        Assert.True(replacement.IsActive);
        Assert.True(replacement.JoinedInCycleId > 0);
    }

    private async Task<Participant> SeedClosingFundWithDevastatedMemberAsync()
    {
        var now = DateTime.UtcNow;

        var cycle = new MarketCycle { CycleNumber = 100, Status = CycleStatus.Running, StartedAt = now };
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
            IssuedSharesCount = 100,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Companies.Add(company);

        var fundParticipant = new Participant
        {
            Name = "Collective Fund",
            Type = ParticipantType.CollectiveFund,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 0m,
            CurrentBalance = 0m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(fundParticipant);

        // Personally wealthy so it skips the re-pool draw once released, but it lost its whole fund deposit.
        var member = new Participant
        {
            Name = "Ruined Member",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 600_000m,
            CurrentBalance = 600_000m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(member);
        await context.SaveChangesAsync();

        var fund = new CollectiveFund
        {
            ParticipantId = fundParticipant.Id,
            FoundedByParticipantId = member.Id,
            Status = CollectiveFundStatus.GoingToBeClosed,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        };
        context.CollectiveFunds.Add(fund);
        await context.SaveChangesAsync();

        context.CollectiveFundParticipants.Add(new CollectiveFundParticipant
        {
            CollectiveFundId = fund.Id,
            ParticipantId = member.Id,
            JoinedAt = now,
            JoinedInCycleId = cycle.Id,
            DepositAmount = 10_000m,
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
        return member;
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    // Forces every random branch to its lowest outcome: rolls always clear their chance, and replacement draws
    // take their minimum. Seeded so any unoverridden Random method stays deterministic across runs.
    private sealed class FixedRandom() : Random(1)
    {
        public override double NextDouble() => 0.0;

        public override int Next() => 0;

        public override int Next(int maxValue) => 0;

        public override int Next(int minValue, int maxValue) => minValue;
    }
}
