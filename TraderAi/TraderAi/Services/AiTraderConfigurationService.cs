using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Conversion contract shared by the configuration service and the API layer. Model is chosen per trader; the
// key is write-only and may be blank when an existing provider's key should be retained.
public sealed record UpdateParticipantAutomationRequest(
    ParticipantType Type,
    string? ProviderId,
    string? Model,
    string? ApiKey);

public sealed record UpdateAutomationResult(bool Success, string? Error, bool ParticipantNotFound)
{
    public static UpdateAutomationResult Ok() => new(true, null, false);

    public static UpdateAutomationResult Fail(string error) => new(false, error, false);

    public static UpdateAutomationResult NotFound() => new(false, "Participant not found.", true);
}

// Converts a participant between rule-based Individual and provider-backed AI Agent. It holds the market lock
// only around the database mutation so it serialises against a running cycle, and cancels the participant's
// in-flight provider call through the runtime state after saving.
public sealed class AiTraderConfigurationService(
    AppDbContext dbContext,
    AiProviderCatalog catalog,
    AiTraderRuntimeState runtimeState,
    MarketCycleLock cycleLock)
{
    public async Task<UpdateAutomationResult> UpdateAutomationAsync(
        int participantId,
        UpdateParticipantAutomationRequest request)
    {
        await cycleLock.Semaphore.WaitAsync();
        try
        {
            var participant = await dbContext.Participants
                .FirstOrDefaultAsync(candidate => candidate.Id == participantId);
            if (participant is null)
            {
                return UpdateAutomationResult.NotFound();
            }

            if (participant.Type is not (ParticipantType.Individual or ParticipantType.AIAgent))
            {
                return UpdateAutomationResult.Fail("Only individual traders can use AI automation.");
            }

            if (!participant.IsActive)
            {
                return UpdateAutomationResult.Fail("Inactive participants cannot be converted.");
            }

            if (participant.IsBankrupt)
            {
                return UpdateAutomationResult.Fail("Bankrupt participants cannot be converted.");
            }

            var existing = await dbContext.AiTraderConfigurations
                .FirstOrDefaultAsync(configuration => configuration.ParticipantId == participantId);

            return request.Type switch
            {
                ParticipantType.AIAgent => await ConvertToAiAsync(participant, existing, request),
                ParticipantType.Individual => await ConvertToIndividualAsync(participant, existing),
                _ => UpdateAutomationResult.Fail("Automation can only target Individual or AI Agent."),
            };
        }
        finally
        {
            cycleLock.Semaphore.Release();
        }
    }

    private async Task<UpdateAutomationResult> ConvertToAiAsync(
        Participant participant,
        AiTraderConfiguration? existing,
        UpdateParticipantAutomationRequest request)
    {
        if (!catalog.TryNormalizeProvider(request.ProviderId, out var providerId))
        {
            return UpdateAutomationResult.Fail("Unknown AI provider.");
        }

        var model = request.Model?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            return UpdateAutomationResult.Fail("A model name is required.");
        }

        var providerChanged = existing is null || existing.ProviderId != providerId;
        string apiKey;
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            apiKey = request.ApiKey.Trim();
        }
        else if (existing is not null && !providerChanged)
        {
            // Retaining the same provider with a blank key keeps the stored key.
            apiKey = existing.ApiKey;
        }
        else
        {
            return UpdateAutomationResult.Fail("An API key is required.");
        }

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            dbContext.AiTraderConfigurations.Add(new AiTraderConfiguration
            {
                ParticipantId = participant.Id,
                ProviderId = providerId,
                Model = model,
                ApiKey = apiKey,
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else if (existing.ProviderId != providerId || existing.Model != model || existing.ApiKey != apiKey)
        {
            existing.ProviderId = providerId;
            existing.Model = model;
            existing.ApiKey = apiKey;
            existing.Revision += 1;
            existing.UpdatedAt = now;
        }

        participant.Type = ParticipantType.AIAgent;
        await dbContext.SaveChangesAsync();
        runtimeState.Cancel(participant.Id);
        return UpdateAutomationResult.Ok();
    }

    private async Task<UpdateAutomationResult> ConvertToIndividualAsync(
        Participant participant,
        AiTraderConfiguration? existing)
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        var currentCycleId = market?.CurrentCycleId ?? 0;

        var openOrders = await dbContext.Orders
            .Where(order => order.ParticipantId == participant.Id
                && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
            .ToListAsync();
        foreach (var order in openOrders)
        {
            OrderCancellation.Cancel(dbContext, order, participant, currentCycleId);
        }

        if (existing is not null)
        {
            dbContext.AiTraderConfigurations.Remove(existing);
        }

        participant.Type = ParticipantType.Individual;
        await dbContext.SaveChangesAsync();
        runtimeState.Cancel(participant.Id);
        return UpdateAutomationResult.Ok();
    }
}
