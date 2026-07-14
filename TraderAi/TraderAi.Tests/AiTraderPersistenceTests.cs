using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Tests;

public sealed class AiTraderPersistenceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public AiTraderPersistenceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task ConfigurationBelongsToExactlyOneParticipant()
    {
        var participant = TestParticipant(ParticipantType.AIAgent);
        context.Participants.Add(participant);
        await context.SaveChangesAsync();

        context.AiTraderConfigurations.Add(new AiTraderConfiguration
        {
            ParticipantId = participant.Id,
            ProviderId = "glm",
            Model = "glm-4.6",
            ApiKey = "test-key",
            Revision = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        Assert.Equal(participant.Id, (await context.AiTraderConfigurations.SingleAsync()).ParticipantId);
    }

    [Fact]
    public async Task ConfigurationCascadesWhenParticipantIsDeleted()
    {
        var participant = TestParticipant(ParticipantType.AIAgent);
        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        context.AiTraderConfigurations.Add(new AiTraderConfiguration
        {
            ParticipantId = participant.Id,
            ProviderId = "glm",
            Model = "glm-4.6",
            ApiKey = "test-key",
            Revision = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        context.Participants.Remove(participant);
        await context.SaveChangesAsync();

        Assert.False(await context.AiTraderConfigurations.AnyAsync());
    }

    [Fact]
    public async Task CallHistoryDoesNotCascadeWhenParticipantIsDeleted()
    {
        var participant = TestParticipant(ParticipantType.AIAgent);
        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        context.AiTraderCalls.Add(AiCall(participant.Id));
        await context.SaveChangesAsync();

        context.Participants.Remove(participant);
        await context.SaveChangesAsync();

        Assert.Equal(participant.Id, (await context.AiTraderCalls.SingleAsync()).ParticipantId);
    }

    private static Participant TestParticipant(ParticipantType type) => new()
    {
        Name = "Test Trader",
        Type = type,
        IsActive = true,
        InitialBalance = 10_000m,
        CurrentBalance = 10_000m,
        SettledCashBalance = 10_000m,
    };

    private static AiTraderCall AiCall(int participantId) => new()
    {
        ParticipantId = participantId,
        ParticipantName = "Test Trader",
        ProviderId = "glm",
        ProviderLabel = "GLM",
        Model = "glm-4.6",
        ConfigurationRevision = 1,
        SnapshotCycleId = 1,
        SnapshotCycleNumber = 1,
        PromptHash = "hash",
        RequestJson = "{}",
        Status = AiTraderCallStatus.Pending,
        RequestedAt = DateTime.UtcNow,
    };

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
