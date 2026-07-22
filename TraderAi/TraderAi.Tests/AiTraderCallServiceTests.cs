using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AiTraderCallServiceTests : IDisposable
{
    private const int MaxOrders = 10;
    private const int MaxPredictions = 10;
    private const int PredictionHorizon = 210;

    private const string ValidDecision =
        "{\"summary\":\"Buy a strong company.\",\"cancelOrderIds\":[],\"bigInvestment\":null,\"orders\":[{\"side\":\"Buy\",\"companyId\":1,\"quantity\":2,\"limitPrice\":3,\"reason\":\"r\"}]}";

    private const string ValidDecisionWithPrediction =
        "{\"summary\":\"Expect strength.\",\"cancelOrderIds\":[],\"bigInvestment\":null,\"orders\":[],\"predictions\":[{\"companyId\":1,\"direction\":\"Up\",\"confidence\":0.72,\"horizonCycles\":210,\"targetPrice\":125.50,\"reason\":\"Demand.\"}]}";

    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly AiTraderCallService service;

    public AiTraderCallServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        service = new AiTraderCallService(context, new MarketCycleLock());
    }

    [Fact]
    public async Task PendingRowIsSavedBeforeSendRuns()
    {
        var pendingVisible = false;

        await service.ExecuteAsync(Descriptor(), MaxOrders, async _ =>
        {
            pendingVisible = await context.AiTraderCalls.AnyAsync(call => call.Status == AiTraderCallStatus.Pending);
            return Ok(ValidDecision, "{\"raw\":1}");
        }, CancellationToken.None);

        Assert.True(pendingVisible);
    }

    [Fact]
    public async Task FailedPendingInsertPreventsSend()
    {
        var sendRan = false;
        connection.Close();

        await Assert.ThrowsAnyAsync<Exception>(() => service.ExecuteAsync(Descriptor(), MaxOrders, _ =>
        {
            sendRan = true;
            return Task.FromResult(Ok(ValidDecision, "{}"));
        }, CancellationToken.None));

        Assert.False(sendRan);
    }

    [Fact]
    public async Task SuccessUpdatesRowWithResponseTokensDecisionAndTiming()
    {
        var execution = await service.ExecuteAsync(Descriptor(), MaxOrders,
            _ => Task.FromResult(Ok(ValidDecision, "{\"raw\":true}")), CancellationToken.None);

        var stored = await context.AiTraderCalls.SingleAsync();
        Assert.Equal(AiTraderCallStatus.Completed, execution.Status);
        Assert.Equal(AiTraderCallStatus.Completed, stored.Status);
        Assert.Equal("{\"raw\":true}", stored.ResponseBody);
        Assert.NotNull(stored.DecisionJson);
        Assert.Equal("Buy a strong company.", stored.Summary);
        Assert.Equal(11, stored.PromptTokens);
        Assert.Equal(33, stored.TotalTokens);
        Assert.NotNull(stored.RespondedAt);
        Assert.NotNull(stored.DurationMilliseconds);
    }

    [Fact]
    public async Task SuccessfulStrictDecisionPersistsPredictionFromExactSnapshot()
    {
        var descriptor = Descriptor() with
        {
            MarketRunId = 7,
            SnapshotTradingDayNumber = 3,
            CompanyPrices = new Dictionary<int, decimal> { [1] = 100m },
        };

        var execution = await service.ExecuteAsync(
            descriptor,
            MaxOrders,
            MaxPredictions,
            PredictionHorizon,
            _ => Task.FromResult(Ok(ValidDecisionWithPrediction, ValidDecisionWithPrediction)),
            CancellationToken.None);

        Assert.Equal(AiTraderCallStatus.Completed, execution.Status);
        var call = await context.AiTraderCalls.SingleAsync();
        var prediction = await context.AiPredictions.SingleAsync();
        Assert.Equal(call.Id, prediction.AiTraderCallId);
        Assert.Equal(7, prediction.MarketRunId);
        Assert.Equal(descriptor.ParticipantId, prediction.ParticipantId);
        Assert.Equal(1, prediction.CompanyId);
        Assert.Equal(descriptor.SnapshotCycleNumber, prediction.SnapshotCycleNumber);
        Assert.Equal(3, prediction.SnapshotTradingDayNumber);
        Assert.Equal(100m, prediction.BaselinePrice);
        Assert.Equal(AiPredictionDirection.Up, prediction.Direction);
        Assert.Equal(0.72m, prediction.Confidence);
        Assert.Equal(210, prediction.HorizonCycles);
        Assert.Equal(125.50m, prediction.TargetPrice);
        Assert.Equal("Demand.", prediction.Reason);
    }

    [Fact]
    public async Task PredictionForCompanyOutsideRequestSnapshotInvalidatesCall()
    {
        var descriptor = Descriptor() with
        {
            MarketRunId = 7,
            SnapshotTradingDayNumber = 3,
            CompanyPrices = new Dictionary<int, decimal>(),
        };

        var execution = await service.ExecuteAsync(
            descriptor,
            MaxOrders,
            MaxPredictions,
            PredictionHorizon,
            _ => Task.FromResult(Ok(ValidDecisionWithPrediction, ValidDecisionWithPrediction)),
            CancellationToken.None);

        Assert.Equal(AiTraderCallStatus.InvalidJson, execution.Status);
        Assert.Empty(await context.AiPredictions.ToListAsync());
        Assert.Null((await context.AiTraderCalls.SingleAsync()).DecisionJson);
    }

    [Fact]
    public async Task HttpErrorBodyIsRetained()
    {
        await service.ExecuteAsync(Descriptor(), MaxOrders,
            _ => Task.FromResult(HttpError(500, "internal error body")), CancellationToken.None);

        var stored = await context.AiTraderCalls.SingleAsync();
        Assert.Equal(AiTraderCallStatus.HttpError, stored.Status);
        Assert.Equal(500, stored.HttpStatusCode);
        Assert.Equal("internal error body", stored.ResponseBody);
        Assert.Null(stored.DecisionJson);
    }

    [Fact]
    public async Task InvalidJsonRetainsRawResponseAndError()
    {
        const string malformed = "{\"summary\":\"x\",\"orders\":}";
        await service.ExecuteAsync(Descriptor(), MaxOrders,
            _ => Task.FromResult(Ok(malformed, malformed)), CancellationToken.None);

        var stored = await context.AiTraderCalls.SingleAsync();
        Assert.Equal(AiTraderCallStatus.InvalidJson, stored.Status);
        Assert.Equal(malformed, stored.ResponseBody);
        Assert.NotNull(stored.Error);
        Assert.Null(stored.DecisionJson);
    }

    [Fact]
    public async Task ProseOnlyReplyIsRejectedByTheStrictFreshResponseContract()
    {
        // The exact GLM reply that previously failed with "'L' is an invalid start of a value": reasoning in prose
        // with no JSON object at all. It must now record a completed no-order decision, never a parse error.
        const string prose = """
            Looking at this portfolio, I need to analyze the situation:

            1. **Cash is 98% of net worth** (~$2B in cash vs ~$35M in holdings) - massive concentration in cash
            2. **Conservative/Low risk profile** - but extreme cash concentration is still a risk
            3. **Open order exists** for company 30 (Mainmast Capital) which is in TradingPause - can't place new orders there
            4. **Key opportunities**: Companies with positive ratings (RaisedExpectations/Extra) in favorable-sentiment sectors, especially those not already held or underweight
            5. **Sector sentiment leaders**: Apparel & Fashion (+29), Banking & Finance (+12), Shipping & Maritime (+12), Forestry & Timber (+11)

            Strategy: Gradually deploy cash into high-quality positions across favorable sectors to reduce cash concentration while maintaining diversification and conservative position sizing. Focus on attractively-priced companies with positive catalysts.
            """;

        var execution = await service.ExecuteAsync(Descriptor(), MaxOrders,
            _ => Task.FromResult(Ok(prose, prose)), CancellationToken.None);

        Assert.Equal(AiTraderCallStatus.InvalidJson, execution.Status);
        Assert.Null(execution.Decision);

        var stored = await context.AiTraderCalls.SingleAsync();
        Assert.Equal(AiTraderCallStatus.InvalidJson, stored.Status);
        Assert.Null(stored.DecisionJson);
        Assert.NotNull(stored.Error);
        Assert.Equal(prose, stored.ResponseBody);
    }

    [Fact]
    public async Task MarkPendingNextDaySetsStatusAndTargetDay()
    {
        var execution = await service.ExecuteAsync(Descriptor(), MaxOrders,
            _ => Task.FromResult(Ok(ValidDecision, ValidDecision)), CancellationToken.None);

        await service.MarkPendingNextDayAsync(execution.CallId, targetDayNumber: 4);

        var stored = await context.AiTraderCalls.SingleAsync();
        Assert.Equal(AiTraderCallStatus.PendingNextDay, stored.Status);
        Assert.Equal(4, stored.NextDayTargetDayNumber);
    }

    [Fact]
    public async Task RecordApplicationSettlesAPendingNextDayCallToCompleted()
    {
        var execution = await service.ExecuteAsync(Descriptor(), MaxOrders,
            _ => Task.FromResult(Ok(ValidDecision, ValidDecision)), CancellationToken.None);
        await service.MarkPendingNextDayAsync(execution.CallId, targetDayNumber: 2);

        await service.RecordApplicationAsync(execution.CallId, "{\"applied\":true}", appliedOrders: 1, rejectedOrders: 0);

        var stored = await context.AiTraderCalls.SingleAsync();
        Assert.Equal(AiTraderCallStatus.Completed, stored.Status);
        Assert.Equal(1, stored.AppliedOrders);
        Assert.NotNull(stored.AppliedAt);
    }

    [Fact]
    public void StoredLegacyDecisionWithoutCancelOrderIdsDefaultsToEmpty()
    {
        const string legacy =
            "{\"summary\":\"Legacy plan.\",\"orders\":[{\"side\":\"Buy\",\"companyId\":1,\"quantity\":2,\"limitPrice\":3,\"reason\":\"r\"}]}";

        var decision = service.DeserializeDecision(legacy);

        Assert.NotNull(decision);
        Assert.Empty(decision!.CancelOrderIds);
        Assert.Null(decision.BigInvestment);
    }

    [Fact]
    public async Task CancellationAndTimeoutReceiveDistinctStatuses()
    {
        await service.ExecuteAsync(Descriptor(1), MaxOrders, _ => Task.FromResult(Cancelled()), CancellationToken.None);
        await service.ExecuteAsync(Descriptor(2), MaxOrders, _ => Task.FromResult(TimedOut()), CancellationToken.None);

        Assert.Equal(AiTraderCallStatus.Cancelled,
            (await context.AiTraderCalls.SingleAsync(call => call.ParticipantId == 1)).Status);
        Assert.Equal(AiTraderCallStatus.TimedOut,
            (await context.AiTraderCalls.SingleAsync(call => call.ParticipantId == 2)).Status);
    }

    [Fact]
    public async Task NoKeyOrAuthorizationValueIsStored()
    {
        await service.ExecuteAsync(Descriptor(), MaxOrders,
            _ => Task.FromResult(Ok(ValidDecision, "{\"choices\":[]}")), CancellationToken.None);
        await service.RecordApplicationAsync(
            (await context.AiTraderCalls.SingleAsync()).Id, "{\"applied\":true}", 1, 0);

        var stored = await context.AiTraderCalls.SingleAsync();
        var text = string.Concat(
            stored.RequestJson, stored.ResponseBody, stored.DecisionJson, stored.ApplicationResultJson,
            stored.Error, stored.Summary);
        Assert.DoesNotContain("Bearer", text);
        Assert.DoesNotContain("secret-key", text);
    }

    [Fact]
    public async Task StartupRecoveryMarksStalePendingAbandoned()
    {
        context.AiTraderCalls.Add(new AiTraderCall
        {
            ParticipantId = 1,
            ParticipantName = "Trader",
            ProviderId = "glm",
            ProviderLabel = "GLM",
            Model = "glm-4.6",
            PromptHash = "hash",
            RequestJson = "{}",
            Status = AiTraderCallStatus.Pending,
            RequestedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var abandoned = await service.AbandonStalePendingCallsAsync();

        Assert.Equal(1, abandoned);
        var stored = await context.AiTraderCalls.SingleAsync();
        Assert.Equal(AiTraderCallStatus.Abandoned, stored.Status);
        Assert.NotNull(stored.Error);
    }

    [Fact]
    public async Task PagedProjectionIsNewestFirstAndBounded()
    {
        for (var index = 0; index < 25; index++)
        {
            await service.ExecuteAsync(Descriptor(7), MaxOrders,
                _ => Task.FromResult(Ok(ValidDecision, "{}")), CancellationToken.None);
        }

        var page = await service.GetPageAsync(7, page: 1, pageSize: 100);

        Assert.Equal(25, page.Total);
        Assert.Equal(AiTraderCallService.MaxPageSize, page.Items.Count);
        var ids = page.Items.Select(item => item.Id).ToList();
        Assert.Equal(ids.OrderByDescending(id => id).ToList(), ids);
    }

    [Fact]
    public async Task DecisionQualitySummaryReportsReliabilityValidityAndExecutedNotional()
    {
        const int participantId = 42;
        AddCall(participantId, AiTraderCallStatus.Completed, applied: 3, rejected: 1);
        AddCall(participantId, AiTraderCallStatus.Completed, applied: 2, rejected: 2);
        AddCall(participantId, AiTraderCallStatus.InvalidJson);
        AddCall(participantId, AiTraderCallStatus.HttpError);
        AddCall(participantId, AiTraderCallStatus.Pending);
        AddCall(participantId + 1, AiTraderCallStatus.Completed, applied: 9, rejected: 9);
        context.ShareTransactions.AddRange(
            Trade(buyerId: participantId, totalCost: 50m),
            Trade(buyerId: participantId, totalCost: 100m),
            Trade(buyerId: participantId + 1, totalCost: 9999m));
        await context.SaveChangesAsync();

        var summary = await service.GetDecisionQualityAsync(participantId);

        Assert.Equal(4, summary.CallAttempts);
        Assert.Equal(2, summary.CompletedCalls);
        Assert.Equal(1, summary.InvalidJsonCalls);
        Assert.Equal(1, summary.OtherFailedCalls);
        Assert.Equal(0.5, summary.CallCompletionRate);
        Assert.Equal(8, summary.ProposedOrders);
        Assert.Equal(5, summary.AppliedOrders);
        Assert.Equal(3, summary.RejectedOrders);
        Assert.Equal(0.625, summary.ProposalAcceptanceRate);
        Assert.Equal(150m, summary.ExecutedBuyNotional);
    }

    [Fact]
    public async Task DecisionQualitySummaryIsZeroedWhenNoActivity()
    {
        var summary = await service.GetDecisionQualityAsync(999);

        Assert.Equal(0, summary.CallAttempts);
        Assert.Equal(0d, summary.CallCompletionRate);
        Assert.Equal(0d, summary.ProposalAcceptanceRate);
        Assert.Equal(0m, summary.ExecutedBuyNotional);
    }

    private void AddCall(int participantId, AiTraderCallStatus status, int applied = 0, int rejected = 0)
        => context.AiTraderCalls.Add(new AiTraderCall
        {
            ParticipantId = participantId,
            ParticipantName = "Trader",
            ProviderId = "glm",
            ProviderLabel = "GLM",
            Model = "glm-4.6",
            PromptHash = "hash",
            RequestJson = "{}",
            Status = status,
            AppliedOrders = applied,
            RejectedOrders = rejected,
            RequestedAt = DateTime.UtcNow,
        });

    private static ShareTransaction Trade(int buyerId, decimal totalCost) => new()
    {
        BuyerId = buyerId,
        CompanyId = 1,
        Quantity = 1,
        Price = totalCost,
        TotalCost = totalCost,
        CreatedInCycleId = 1,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static AiTraderCallDescriptor Descriptor(int participantId = 1) => new(
        participantId,
        "Trader",
        "glm",
        "GLM",
        "glm-4.6",
        ConfigurationRevision: 1,
        SnapshotCycleId: 10,
        SnapshotCycleNumber: 5,
        PromptHash: "hash",
        RequestJson: "{\"prompt\":true}");

    private static AiProviderResponse Ok(string content, string rawBody)
        => new(AiProviderCallOutcome.Success, 200, rawBody, content, 11, 22, 33, null, null);

    private static AiProviderResponse HttpError(int status, string body)
        => new(AiProviderCallOutcome.HttpError, status, body, null, null, null, null, null, "http error");

    private static AiProviderResponse TimedOut()
        => new(AiProviderCallOutcome.TimedOut, null, null, null, null, null, null, null, "timed out");

    private static AiProviderResponse Cancelled()
        => new(AiProviderCallOutcome.Cancelled, null, null, null, null, null, null, null, "cancelled");

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
