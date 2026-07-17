using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AiTraderConfigurationTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly AiTraderRuntimeState runtimeState = new();
    private readonly AiTraderConfigurationService service;

    public AiTraderConfigurationTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();

        var options = Options.Create(new AiTradingOptions
        {
            Enabled = true,
            Providers = new Dictionary<string, AiProviderOptions>
            {
                ["glm"] = new() { DisplayName = "GLM", Endpoint = "https://glm.test/v1", Models = { "glm-4.6", "glm-4.5" } },
                ["minimax"] = new() { DisplayName = "MiniMax", Endpoint = "https://minimax.test/v1", Models = { "MiniMax-M2" } },
            },
        });
        service = new AiTraderConfigurationService(
            context, new AiProviderCatalog(options), runtimeState, new MarketCycleLock(),
            Options.Create(new TradingClockOptions()));
    }

    [Fact]
    public async Task ConvertingIndividualToAiStoresNormalizedProviderAndModel()
    {
        var participant = await SeedParticipantAsync(ParticipantType.Individual);

        var result = await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "GLM", "glm-4.6"));

        Assert.True(result.Success);
        var config = await context.AiTraderConfigurations.SingleAsync();
        Assert.Equal("glm", config.ProviderId);
        Assert.Equal("glm-4.6", config.Model);
        Assert.Equal(1, config.Revision);
        Assert.Equal(ParticipantType.AIAgent, (await context.Participants.SingleAsync()).Type);
    }

    [Fact]
    public async Task UnknownProviderIsRejected()
    {
        var participant = await SeedParticipantAsync(ParticipantType.Individual);

        var result = await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "future-provider", "any"));

        Assert.False(result.Success);
        Assert.False(await context.AiTraderConfigurations.AnyAsync());
    }

    [Fact]
    public async Task ArbitraryModelNameIsAcceptedForKnownProvider()
    {
        var participant = await SeedParticipantAsync(ParticipantType.Individual);

        var result = await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "glm-4.6-custom-tuned"));

        Assert.True(result.Success);
        Assert.Equal("glm-4.6-custom-tuned", (await context.AiTraderConfigurations.SingleAsync()).Model);
    }

    [Fact]
    public async Task BlankModelIsRejected()
    {
        var participant = await SeedParticipantAsync(ParticipantType.Individual);

        var result = await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "   "));

        Assert.False(result.Success);
        Assert.False(await context.AiTraderConfigurations.AnyAsync());
    }

    [Fact]
    public async Task EditingModelOnSameProviderBumpsRevision()
    {
        var participant = await SeedParticipantAsync(ParticipantType.Individual);
        await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "glm-4.6"));

        var result = await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "glm-4.5"));

        Assert.True(result.Success);
        var config = await context.AiTraderConfigurations.SingleAsync();
        Assert.Equal("glm-4.5", config.Model);
        Assert.Equal(2, config.Revision);
    }

    [Fact]
    public async Task ChangingProviderBumpsRevision()
    {
        var participant = await SeedParticipantAsync(ParticipantType.Individual);
        await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "glm-4.6"));

        var result = await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "minimax", "MiniMax-M2"));

        Assert.True(result.Success);
        var config = await context.AiTraderConfigurations.SingleAsync();
        Assert.Equal("minimax", config.ProviderId);
        Assert.Equal(2, config.Revision);
    }

    [Fact]
    public async Task ConvertingBackDeletesConfigCancelsOrdersAndSignalsCancellation()
    {
        var participant = await SeedParticipantAsync(ParticipantType.Individual);
        await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "glm-4.6"));

        context.Markets.Add(new Market { Name = "Market", Status = MarketStatus.Running, CurrentCycleId = 1 });
        participant.ReservedBalance = 100m;
        context.Orders.Add(new Order
        {
            ParticipantId = participant.Id,
            CompanyId = 1,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 1,
            LimitPrice = 100m,
            ReservedCashAmount = 100m,
            CreatedInCycleId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var token = runtimeState.BeginCall(participant.Id);

        var result = await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.Individual, null, null));

        Assert.True(result.Success);
        Assert.False(await context.AiTraderConfigurations.AnyAsync());
        Assert.Equal(ParticipantType.Individual, (await context.Participants.SingleAsync()).Type);
        Assert.Equal(OrderStatus.Cancelled, (await context.Orders.SingleAsync()).Status);
        Assert.Equal(0m, (await context.Participants.SingleAsync()).ReservedBalance);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task ConversionDefaultsMaxDecisionsPerDayToThree()
    {
        var participant = await SeedParticipantAsync(ParticipantType.Individual);

        await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "glm-4.6"));

        Assert.Equal(3, (await context.AiTraderConfigurations.SingleAsync()).MaxDecisionsPerDay);
    }

    [Fact]
    public async Task EditingOnlyMaxDecisionsPerDayPersistsWithoutBumpingRevision()
    {
        var participant = await SeedParticipantAsync(ParticipantType.Individual);
        await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "glm-4.6"));

        var result = await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "glm-4.6", MaxDecisionsPerDay: 5));

        Assert.True(result.Success);
        var config = await context.AiTraderConfigurations.SingleAsync();
        Assert.Equal(5, config.MaxDecisionsPerDay);
        Assert.Equal(1, config.Revision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(211)]
    public async Task OutOfRangeMaxDecisionsPerDayIsRejected(int value)
    {
        var participant = await SeedParticipantAsync(ParticipantType.Individual);

        var result = await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "glm-4.6", MaxDecisionsPerDay: value));

        Assert.False(result.Success);
        Assert.False(await context.AiTraderConfigurations.AnyAsync());
    }

    [Theory]
    [InlineData(ParticipantType.Player)]
    [InlineData(ParticipantType.CollectiveFund)]
    [InlineData(ParticipantType.Company)]
    public async Task IneligibleParticipantTypesCannotConvert(ParticipantType type)
    {
        var participant = await SeedParticipantAsync(type);

        var result = await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "glm-4.6"));

        Assert.False(result.Success);
        Assert.False(result.ParticipantNotFound);
    }

    [Fact]
    public async Task InactiveParticipantCannotConvert()
    {
        var participant = await SeedParticipantAsync(ParticipantType.Individual, isActive: false);

        var result = await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "glm-4.6"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BankruptParticipantCannotConvert()
    {
        var participant = await SeedParticipantAsync(ParticipantType.Individual);
        participant.IsBankrupt = true;
        await context.SaveChangesAsync();

        var result = await service.UpdateAutomationAsync(
            participant.Id,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "glm-4.6"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task MissingParticipantReturnsNotFound()
    {
        var result = await service.UpdateAutomationAsync(
            9999,
            new UpdateParticipantAutomationRequest(ParticipantType.AIAgent, "glm", "glm-4.6"));

        Assert.False(result.Success);
        Assert.True(result.ParticipantNotFound);
    }

    private async Task<Participant> SeedParticipantAsync(ParticipantType type, bool isActive = true)
    {
        var participant = new Participant
        {
            Name = "Trader",
            Type = type,
            IsActive = isActive,
            InitialBalance = 10_000m,
            CurrentBalance = 10_000m,
            SettledCashBalance = 10_000m,
        };
        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        return participant;
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
