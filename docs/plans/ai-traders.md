# AI Traders Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Let an operator convert an Individual trader into a GLM- or MiniMax-powered AI trader whose provider calls run alongside market cycles, return strict JSON order decisions, and remain fully observable from the trader detail page.

**Architecture:** Store encrypted per-trader provider credentials in SQLite, but keep provider calls outside the serialized market-cycle transaction. A hosted coordinator builds a fresh market snapshot, loads and caches the approved project documentation for five minutes, logs the exact credential-free request, calls the provider, logs the raw response, strictly deserializes the shared decision JSON, and reacquires the existing market lock only while revalidating and placing still-valid orders. The rule-based engine continues to own Individuals and funds; configured AI Agents are owned only by the hosted coordinator.

**Tech Stack:** .NET 10 minimal API, EF Core 10, SQLite, ASP.NET Core Data Protection, `HttpClient`, `System.Text.Json`, React 19, Vite, xUnit, and Node's built-in test runner.

---

## Repository constraints

- Keep this plan file out of every implementation commit.
- Preserve the unrelated collective-fund work already present in the worktree.
- Use `rtk` for shell, launch, migration, and verification commands.
- Use conventional commit messages and never add an AI co-author.
- Run the complete backend suite after backend changes and all existing frontend tests, lint, and build after frontend changes.
- Keep comments focused on why a constraint exists; do not narrate adjacent code.

## Approved behavior

- The trader detail page owns Individual/AI conversion, provider selection, and API-key entry.
- The API key is write-only in the frontend, encrypted in SQLite, never returned, and never logged.
- Provider labels such as `AI · GLM` and `AI · MiniMax` appear in the trader roster and trader detail header. Provider metadata comes from the backend so later providers do not require hard-coded frontend labels.
- Conversion is reversible. Converting to Individual cancels in-flight work, deletes the encrypted configuration, and resumes rule-based decisions.
- Existing unconfigured `AIAgent` rows migrate to `Individual`; market-exit replacements become Individuals only.
- Provider or parsing failures leave the AI trader idle in a visible Error state. There is no rule-based fallback.
- Each trader has at most one provider call in flight. Calls run outside the market lock and do not delay two-second cycles.
- The system prompt is backend-owned. Relevant project documentation is read lazily from an explicit allowlist and cached in application memory for five minutes with absolute expiration.
- Every attempted provider request is inserted into `AiTraderCalls` before the HTTP call. Raw responses, parsed decisions, application outcomes, timing, token usage, and failures update that row afterward.
- AI-call history is retained through provider edits, conversion, pause, restart, and participant departure, but a full market reset deletes it.
- The trader detail page shows call history newest first with server pagination. Full request/response JSON is loaded only when one call is opened.

## Shared AI response contract

The assistant content must be exactly one JSON object with no Markdown fence or surrounding prose:

```json
{
  "summary": "Reduce exposure to a falling company and buy shares in a stronger industry.",
  "orders": [
    {
      "side": "Sell",
      "companyId": 17,
      "quantity": 25,
      "limitPrice": 94.50,
      "reason": "Capitalization and industry sentiment have declined over recent cycles."
    },
    {
      "side": "Buy",
      "companyId": 42,
      "quantity": 10,
      "limitPrice": 121.25,
      "reason": "Positive capitalization trend with sufficient available cash."
    }
  ]
}
```

`orders: []` is a valid decision to wait. Deserialization is strict, but successfully deserialized orders are applied independently: an invalid order does not prevent another valid order in the same response.

## Prompt inputs

The system message contains the cached project rules, the net-worth/growth/risk objective, the no-short-selling and validation constraints, and the JSON schema. The user message contains compact JSON with:

- current cycle, trading day, session, and active crisis;
- participant temperament, risk, balances, buying power, liabilities, worth, holdings, and own open orders;
- the actionable open buy and sell books;
- active companies, industries, price bands, trading status, current ratings, and current capitalization;
- one company-capitalization point per available cycle for the last 30 cycles;
- one industry-sentiment point per available cycle for the last 30 cycles; and
- active fee, settlement, margin, and order limits.

Historical gaps remain gaps. Do not synthesize capitalization or sentiment for cycles that predate those snapshots.

### Task 1: Add AI persistence models and migration

**Files:**

- Create: `TraderAi/TraderAi/Models/AiTraderConfiguration.cs`
- Create: `TraderAi/TraderAi/Models/AiTraderCall.cs`
- Modify: `TraderAi/TraderAi/Models/Enums.cs`
- Modify: `TraderAi/TraderAi/Data/AppDbContext.cs`
- Create: `TraderAi/TraderAi.Tests/AiTraderPersistenceTests.cs`
- Generate: `TraderAi/TraderAi/Migrations/*_AddAiTraders.cs`
- Generate: `TraderAi/TraderAi/Migrations/*_AddAiTraders.Designer.cs`
- Modify: `TraderAi/TraderAi/Migrations/AppDbContextModelSnapshot.cs`

**Step 1: Write failing persistence tests**

Cover these invariants with an in-memory SQLite context:

```csharp
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
        EncryptedApiKey = "ciphertext",
        Revision = 1,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    });
    await context.SaveChangesAsync();

    Assert.Equal(participant.Id, (await context.AiTraderConfigurations.SingleAsync()).ParticipantId);
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
```

The configuration must cascade on participant deletion. The call log deliberately stores only a scalar participant ID and participant name, without a foreign key, so collected history survives an ordinary participant departure.

**Step 2: Run the tests and verify the missing model fails**

Run:

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AiTraderPersistenceTests
```

Expected: compilation fails because the AI persistence types and `DbSet` properties do not exist.

**Step 3: Add the persistence types**

Use these minimum shapes:

```csharp
public sealed class AiTraderConfiguration
{
    public int ParticipantId { get; set; }
    public required string ProviderId { get; set; }
    public required string EncryptedApiKey { get; set; }
    public int Revision { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum AiTraderCallStatus
{
    Pending,
    Completed,
    HttpError,
    TimedOut,
    InvalidJson,
    Cancelled,
    Abandoned,
}

public sealed class AiTraderCall
{
    public long Id { get; set; }
    public int ParticipantId { get; set; }
    public required string ParticipantName { get; set; }
    public required string ProviderId { get; set; }
    public required string ProviderLabel { get; set; }
    public required string Model { get; set; }
    public int ConfigurationRevision { get; set; }
    public int SnapshotCycleId { get; set; }
    public int SnapshotCycleNumber { get; set; }
    public required string PromptHash { get; set; }
    public required string RequestJson { get; set; }
    public string? ResponseBody { get; set; }
    public string? DecisionJson { get; set; }
    public string? ApplicationResultJson { get; set; }
    public AiTraderCallStatus Status { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? Error { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
    public long? DurationMilliseconds { get; set; }
}
```

Configure:

- `AiTraderConfiguration.ParticipantId` as both primary key and required one-to-one foreign key to `Participant`, with cascade delete.
- maximum lengths for provider ID, provider label, model, prompt hash, and error fields;
- `AiTraderCall` without a participant relationship; and
- an index on `{ ParticipantId, Id }` for newest-first history seeks.

**Step 4: Generate and inspect the migration**

Run:

```bash
rtk dotnet ef migrations add AddAiTraders --project TraderAi/TraderAi/TraderAi.csproj --startup-project TraderAi/TraderAi/TraderAi.csproj
```

Add an explicit data migration after table creation:

```csharp
migrationBuilder.Sql("UPDATE Participants SET Type = 0 WHERE Type = 2;");
```

The enum values are currently `Individual = 0` and `AIAgent = 2`. The migration converts placeholder AI rows before the new invariant is enforced by application behavior.

**Step 5: Run focused tests**

Run:

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AiTraderPersistenceTests
```

Expected: PASS.

**Step 6: Commit production and test files, excluding this plan**

```bash
rtk git add TraderAi/TraderAi/Models/AiTraderConfiguration.cs TraderAi/TraderAi/Models/AiTraderCall.cs TraderAi/TraderAi/Models/Enums.cs TraderAi/TraderAi/Data/AppDbContext.cs TraderAi/TraderAi/Migrations TraderAi/TraderAi.Tests/AiTraderPersistenceTests.cs
rtk git commit -m "feat: add AI trader persistence"
```

### Task 2: Add provider catalog, encrypted credentials, and reversible conversion

**Files:**

- Create: `TraderAi/TraderAi/Services/AiTradingOptions.cs`
- Create: `TraderAi/TraderAi/Services/AiProviderCatalog.cs`
- Create: `TraderAi/TraderAi/Services/AiApiKeyProtector.cs`
- Create: `TraderAi/TraderAi/Services/AiTraderRuntimeState.cs`
- Create: `TraderAi/TraderAi/Services/AiTraderConfigurationService.cs`
- Modify: `TraderAi/TraderAi/Program.cs`
- Modify: `TraderAi/TraderAi/appsettings.json`
- Create: `TraderAi/TraderAi.Tests/AiTraderConfigurationTests.cs`

**Step 1: Write failing configuration tests**

Test:

- converting an active Individual to AI requires a known provider and nonblank API key;
- the database contains ciphertext, never the original key;
- the provider ID is normalized to the catalog ID;
- editing the same provider with an empty key retains the existing encrypted key;
- changing provider requires a new key;
- each effective edit increments `Revision`;
- converting back to Individual deletes the configuration and signals cancellation through runtime state;
- Player, CollectiveFund, inactive, and bankrupt participants cannot be converted; and
- an unknown future provider ID is rejected until it exists in the backend catalog.

Use an ephemeral Data Protection provider in tests:

```csharp
var dataProtection = new EphemeralDataProtectionProvider();
var protector = new AiApiKeyProtector(dataProtection);
```

**Step 2: Run the focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AiTraderConfigurationTests
```

Expected: compilation fails because the catalog, protector, runtime state, and configuration service do not exist.

**Step 3: Add provider and coordinator options**

Bind this shape from `AiTrading`:

```json
{
  "AiTrading": {
    "Enabled": true,
    "DocumentationRoot": "../../docs",
    "ScanIntervalMilliseconds": 500,
    "RequestTimeoutSeconds": 120,
    "MaxConcurrentRequests": 4,
    "MaxOrdersPerDecision": 10,
    "HistoryCycles": 30,
    "Providers": {
      "glm": {
        "DisplayName": "GLM",
        "Endpoint": "https://api.z.ai/api/paas/v4/chat/completions",
        "Model": "glm-5.1"
      },
      "minimax": {
        "DisplayName": "MiniMax",
        "Endpoint": "https://api.minimax.io/v1/chat/completions",
        "Model": "MiniMax-M2.7"
      }
    }
  }
}
```

Do not place API keys in configuration. Validate positive timing/concurrency/order limits and require provider endpoints to be HTTPS.

**Step 4: Implement write-only credential protection**

`AiApiKeyProtector` must create a purpose-specific protector such as `TraderAi.AiTraderApiKeys.v1`. It exposes only `Protect(string)` and `Unprotect(string)` and never logs arguments or failures containing ciphertext.

Register Data Protection with a stable application name:

```csharp
builder.Services.AddDataProtection().SetApplicationName("TraderAi");
```

Use the normal per-user Data Protection key ring. Do not commit key-ring material to the repository.

**Step 5: Implement conversion under the existing cycle lock**

Use a scoped `AiTraderConfigurationService` that acquires `MarketCycleLock.Semaphore` only around the database mutation. Its input contract is:

```csharp
public sealed record UpdateParticipantAutomationRequest(
    ParticipantType Type,
    string? ProviderId,
    string? ApiKey);
```

For `AIAgent`, validate and encrypt before saving. For `Individual`, remove the configuration. After saving, cancel the participant's current runtime cancellation token. The configuration revision remains the final stale-response guard if cancellation races with a completed HTTP call.

**Step 6: Run focused tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AiTraderConfigurationTests
```

Expected: PASS.

**Step 7: Commit, excluding this plan**

```bash
rtk git add TraderAi/TraderAi/Services/AiTradingOptions.cs TraderAi/TraderAi/Services/AiProviderCatalog.cs TraderAi/TraderAi/Services/AiApiKeyProtector.cs TraderAi/TraderAi/Services/AiTraderRuntimeState.cs TraderAi/TraderAi/Services/AiTraderConfigurationService.cs TraderAi/TraderAi/Program.cs TraderAi/TraderAi/appsettings.json TraderAi/TraderAi.Tests/AiTraderConfigurationTests.cs
rtk git commit -m "feat: configure encrypted AI traders"
```

### Task 3: Load prompt documentation with a five-minute in-memory cache

**Files:**

- Create: `TraderAi/TraderAi/Services/AiPromptDocumentationProvider.cs`
- Create: `TraderAi/TraderAi.Tests/AiPromptDocumentationProviderTests.cs`
- Modify: `TraderAi/TraderAi/Program.cs`

**Step 1: Write failing cache tests**

Use a temporary documentation root and a small test `TimeProvider`. Prove:

- the first request reads a required file;
- changing the file before five minutes still returns cached text;
- advancing the clock to five minutes causes the next request to reload;
- frequent reads do not extend expiration;
- fund documents are added only when the snapshot indicates fund membership;
- `AGENTS.md`, `docs/plans`, `docs/prompts`, and arbitrary paths cannot be requested; and
- a missing required document fails prompt construction before any provider call.

**Step 2: Run the focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AiPromptDocumentationProviderTests
```

Expected: compilation fails because the provider does not exist.

**Step 3: Implement the singleton cache**

Use an in-memory dictionary keyed by normalized allowlisted relative path. Store content plus the absolute expiration returned from `TimeProvider.GetUtcNow() + TimeSpan.FromMinutes(5)`. Protect reloads with a small lock so concurrent first requests do not repeatedly read the same file.

The core allowlist is:

```text
docs/roles/ai-agent.md
docs/roles/individual.md
docs/rules/share-price-formation.md
docs/rules/trading-days.md
docs/rules/luld.md
docs/logic/settlement.md
docs/logic/margin.md
docs/logic/bank-loans.md
docs/logic/sector-sentiment.md
```

Conditionally include:

```text
docs/roles/fund-member.md
docs/roles/collective-fund.md
```

Resolve `AiTrading:DocumentationRoot` against `IHostEnvironment.ContentRootPath`, call `Path.GetFullPath`, and verify each resolved file remains beneath the configured documentation root.

**Step 4: Register the provider and rerun tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AiPromptDocumentationProviderTests
```

Expected: PASS.

**Step 5: Commit, excluding this plan**

```bash
rtk git add TraderAi/TraderAi/Services/AiPromptDocumentationProvider.cs TraderAi/TraderAi/Program.cs TraderAi/TraderAi.Tests/AiPromptDocumentationProviderTests.cs
rtk git commit -m "feat: cache AI prompt documentation"
```

### Task 4: Define strict decision JSON and provider HTTP handling

**Files:**

- Create: `TraderAi/TraderAi/Services/AiTradingContracts.cs`
- Create: `TraderAi/TraderAi/Services/AiDecisionJson.cs`
- Create: `TraderAi/TraderAi/Services/AiProviderClient.cs`
- Create: `TraderAi/TraderAi.Tests/AiDecisionJsonTests.cs`
- Create: `TraderAi/TraderAi.Tests/AiProviderClientTests.cs`
- Modify: `TraderAi/TraderAi/Program.cs`

**Step 1: Write failing strict-JSON tests**

Use the approved JSON contract and assert:

- exact valid JSON deserializes;
- `orders: []` succeeds;
- Markdown fences or surrounding prose fail;
- missing or blank summary fails;
- unknown properties fail;
- side values other than `Buy` and `Sell` fail;
- quantity and price must be positive;
- company IDs must be positive;
- reasons must be nonblank; and
- more than `MaxOrdersPerDecision` fails the response.

Define the DTOs with explicit JSON property names:

```csharp
public sealed record AiTradeDecision(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("orders")] AiTradeOrderDecision[] Orders);

public sealed record AiTradeOrderDecision(
    [property: JsonPropertyName("side")] OrderType Side,
    [property: JsonPropertyName("companyId")] int CompanyId,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("limitPrice")] decimal LimitPrice,
    [property: JsonPropertyName("reason")] string Reason);
```

Use a dedicated `JsonSerializerOptions` with string enums and unmapped-member rejection. Do not relax it globally for ordinary API contracts.

**Step 2: Write failing provider-client tests**

With a fake `HttpMessageHandler`, verify for GLM and MiniMax:

- endpoint and model come from the catalog;
- the API key is present only in the `Authorization: Bearer` header;
- the prepared request JSON contains no key or ciphertext;
- system and user messages are sent without conversation history;
- provider-specific thinking is disabled or separated from assistant content;
- raw response bodies are returned unchanged for logging;
- assistant content is extracted without returning hidden reasoning;
- HTTP status and token usage are parsed when present; and
- timeout, cancellation, non-success HTTP responses, and malformed provider envelopes remain distinguishable outcomes.

**Step 3: Run focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~AiDecisionJsonTests|FullyQualifiedName~AiProviderClientTests"
```

Expected: compilation fails because the contracts and provider client do not exist.

**Step 4: Implement one client with provider-specific request shaping**

Use `IHttpClientFactory`; do not add an OpenAI SDK package. Split preparation from sending so the exact request body can be committed before the HTTP call:

```csharp
public sealed record PreparedAiProviderRequest(
    string ProviderId,
    string ProviderLabel,
    string Model,
    Uri Endpoint,
    string RequestJson);

public sealed record AiProviderResponse(
    int HttpStatusCode,
    string RawBody,
    string? AssistantContent,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens);
```

Keep the provider switch limited to payload fields and response-envelope differences. Both providers produce the same `AssistantContent`, which is passed unchanged to strict decision deserialization.

**Step 5: Rerun focused tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~AiDecisionJsonTests|FullyQualifiedName~AiProviderClientTests"
```

Expected: PASS.

**Step 6: Commit, excluding this plan**

```bash
rtk git add TraderAi/TraderAi/Services/AiTradingContracts.cs TraderAi/TraderAi/Services/AiDecisionJson.cs TraderAi/TraderAi/Services/AiProviderClient.cs TraderAi/TraderAi/Program.cs TraderAi/TraderAi.Tests/AiDecisionJsonTests.cs TraderAi/TraderAi.Tests/AiProviderClientTests.cs
rtk git commit -m "feat: add AI provider decision contract"
```

### Task 5: Build the current market snapshot and prompt

**Files:**

- Create: `TraderAi/TraderAi/Services/AiMarketSnapshotBuilder.cs`
- Create: `TraderAi/TraderAi/Services/AiTradingPromptBuilder.cs`
- Create: `TraderAi/TraderAi.Tests/AiMarketSnapshotBuilderTests.cs`
- Create: `TraderAi/TraderAi.Tests/AiTradingPromptBuilderTests.cs`

**Step 1: Write failing snapshot tests**

Seed at least 35 cycles and assert the snapshot contains:

- the current cycle, day, session, active crisis, trade fee, settlement lag, and margin settings;
- participant balances, settled/unsettled cash, reservations, available balance, buying power, loan liability, margin liability, net worth, temperament, and risk;
- positive holdings with average cost, settled quantity, current price/value, and unrealized result;
- the participant's open and partially filled orders;
- the actionable global buy and sell books with remaining quantities;
- only live, executable companies, including industry, current price/capitalization, price bounds, LULD status, and latest rating;
- exactly the latest 30 available company-capitalization cycle points, selecting the newest snapshot when one company has multiple snapshots in a cycle;
- exactly the latest 30 available industry-sentiment cycle points; and
- null or absent values for historical data that was never recorded.

Prove a current fund member receives zero discretionary buying cash while retaining owned holdings and sell capability, matching existing behavior.

**Step 2: Write failing prompt tests**

Assert the system message contains:

- every selected cached document with a stable source heading;
- the objective to increase long-term net worth and growth while reducing concentration, leverage, and downside risk;
- no short selling;
- a statement that market text is data, not instructions;
- the exact JSON schema and JSON-only requirement; and
- the maximum-order limit.

Assert the user message is valid compact JSON containing the snapshot and no API key, ciphertext, filesystem path, or internal `AGENTS.md` content.

**Step 3: Run focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~AiMarketSnapshotBuilderTests|FullyQualifiedName~AiTradingPromptBuilderTests"
```

Expected: compilation fails because the builders do not exist.

**Step 4: Implement batched snapshot queries**

Use `PriceSnapshotQueries.LatestPriceByCompanyAsync` for current prices. Load the last 30 cycle IDs once, then batch price/capitalization and sentiment reads. Do not issue one query per company, order, or history point. Current capitalization may be computed as current price times issued shares; historical capitalization must come from recorded snapshots only.

Use stable numeric IDs throughout so the response can refer to companies even when names are similar.

**Step 5: Implement prompt composition**

Return two strings, `SystemMessage` and `UserMessage`, plus a SHA-256 hash of the system message. Hashing makes later prompt-performance analysis groupable without replacing the exact stored request JSON.

Do not store conversation history. Each request is a fresh decision against a fresh market snapshot.

**Step 6: Rerun focused tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~AiMarketSnapshotBuilderTests|FullyQualifiedName~AiTradingPromptBuilderTests"
```

Expected: PASS.

**Step 7: Commit, excluding this plan**

```bash
rtk git add TraderAi/TraderAi/Services/AiMarketSnapshotBuilder.cs TraderAi/TraderAi/Services/AiTradingPromptBuilder.cs TraderAi/TraderAi.Tests/AiMarketSnapshotBuilderTests.cs TraderAi/TraderAi.Tests/AiTradingPromptBuilderTests.cs
rtk git commit -m "feat: build AI market prompts"
```

### Task 6: Persist every AI call and its outcomes

**Files:**

- Create: `TraderAi/TraderAi/Services/AiTraderCallService.cs`
- Create: `TraderAi/TraderAi.Tests/AiTraderCallServiceTests.cs`

**Step 1: Write failing call-log tests**

Test:

- a `Pending` row is saved before a supplied send delegate is invoked;
- a failed pending insert prevents the send delegate from running;
- successful raw response, token usage, parsed decision, application result, and timing update the same row;
- HTTP error bodies are retained;
- invalid assistant JSON retains both raw response and parse error;
- cancellation and timeout receive different statuses;
- no key or `Authorization` value appears in any stored text field;
- startup recovery marks stale `Pending` rows `Abandoned`; and
- paged summary projection orders by descending ID and does not select large request/response columns.

**Step 2: Run focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AiTraderCallServiceTests
```

Expected: compilation fails because the call service does not exist.

**Step 3: Implement short serialized writes**

Use a scoped service and the existing `MarketCycleLock` for each short insert/update. Never hold the lock while waiting for the provider. If a cycle is already running, the audit write waits; it must not interrupt or roll back that cycle.

Serialize parsed decisions and application results with the same camel-case JSON configuration used by their API responses.

**Step 4: Rerun focused tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AiTraderCallServiceTests
```

Expected: PASS.

**Step 5: Commit, excluding this plan**

```bash
rtk git add TraderAi/TraderAi/Services/AiTraderCallService.cs TraderAi/TraderAi.Tests/AiTraderCallServiceTests.cs
rtk git commit -m "feat: log AI trader calls"
```

### Task 7: Revalidate and apply AI orders through the existing market rules

**Files:**

- Modify: `TraderAi/TraderAi/Services/MarketService.cs`
- Create: `TraderAi/TraderAi.Tests/AiDecisionApplicationTests.cs`

**Step 1: Write failing application tests**

Cover:

- a matching configuration revision applies a valid buy and sell;
- a stale revision, changed provider, converted Individual, inactive participant, or bankrupt participant applies nothing;
- one invalid order does not roll back another valid order;
- current market break, delisting, halt, allowed price range, buying power, cash reservation, conflicting side, and owned-share checks are enforced at application time;
- the stored AI reason never bypasses validation and is not copied into order accounting records;
- orders are placed but not matched until the normal cycle advances; and
- the result contains one `Applied` or `Rejected` outcome per requested order with a safe reason.

Use this result shape:

```csharp
public sealed record AiOrderApplicationResult(
    int Index,
    OrderType Side,
    int CompanyId,
    int Quantity,
    decimal LimitPrice,
    string Reason,
    bool Applied,
    int? CreatedOrderId,
    string? RejectionReason);

public sealed record AiDecisionApplicationResult(
    bool ConfigurationStillCurrent,
    AiOrderApplicationResult[] Orders);
```

**Step 2: Run focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AiDecisionApplicationTests
```

Expected: compilation fails because the application entry point does not exist.

**Step 3: Add a narrow public application entry point**

Add `MarketService.ApplyAiDecisionAsync(participantId, configurationRevision, decision)`. It must acquire the existing process-wide lock, reload participant/configuration/current market state, and call the existing `PlaceOrderCoreAsync` for each order.

Use ordinary non-deferred placement so each order receives current LULD and price validation and commits independently. Do not call matching or cycle advancement from this path.

**Step 4: Rerun focused tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AiDecisionApplicationTests
```

Expected: PASS.

**Step 5: Run existing decision and order tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~DecisionFlowTests|FullyQualifiedName~OrderPriceBoundsTests|FullyQualifiedName~MatchingTests"
```

Expected: PASS.

**Step 6: Commit, excluding this plan**

```bash
rtk git add TraderAi/TraderAi/Services/MarketService.cs TraderAi/TraderAi.Tests/AiDecisionApplicationTests.cs
rtk git commit -m "feat: apply AI trader decisions"
```

### Task 8: Run AI thinking beside market cycles

**Files:**

- Create: `TraderAi/TraderAi/Services/AiTraderCoordinator.cs`
- Create: `TraderAi/TraderAi.Tests/AiTraderCoordinatorTests.cs`
- Modify: `TraderAi/TraderAi/Program.cs`

**Step 1: Write failing coordinator tests**

Use a fake provider backed by `TaskCompletionSource` and prove:

- a blocked provider response does not prevent `MarketService.RunCycleTickAsync` from completing;
- each participant has at most one in-flight request;
- global concurrent requests never exceed `MaxConcurrentRequests`;
- no request starts while the market is paused, stopped, in a trading break, or has no current cycle;
- after apply completes, a new request starts immediately only when a newer cycle exists;
- conversion/provider edit cancels the request and stale responses cannot apply;
- provider error sets Error state and creates no rule-based order;
- transient errors back off exponentially and honor `Retry-After`;
- authentication errors use the longer retry window and remain visibly idle;
- request logging completes before the fake provider is entered;
- raw response logging completes even when decision JSON is invalid; and
- startup marks orphaned pending calls `Abandoned`.

**Step 2: Run focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AiTraderCoordinatorTests
```

Expected: compilation fails because the coordinator does not exist.

**Step 3: Implement the hosted coordinator**

Use `BackgroundService`, `IServiceScopeFactory`, a `PeriodicTimer`, a per-participant task/cancellation map, and one global `SemaphoreSlim`. For each eligible configuration:

1. create a scope and snapshot;
2. load cached documents and build the prompt;
3. prepare the credential-free provider request;
4. save the pending audit row;
5. decrypt the key only immediately before sending;
6. send outside the market lock;
7. save raw response and parse outcome;
8. apply valid JSON through `MarketService`;
9. save application results; and
10. update in-memory runtime status.

Do not retain decrypted keys in runtime state, closures that outlive the call, logs, or exception messages.

Expose runtime status through a singleton service:

```csharp
public enum AiTraderRuntimeStatus
{
    Waiting,
    Thinking,
    Applying,
    Error,
}
```

Store safe status message, current call ID, snapshot cycle, started/completed timestamps, and next retry time. State resets to Waiting after an application restart.

**Step 4: Register the hosted service and typed dependencies**

Register `HttpClientFactory`, the scoped builders/client/log service, singleton runtime state and documentation provider, and `AddHostedService<AiTraderCoordinator>()`.

**Step 5: Rerun focused tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AiTraderCoordinatorTests
```

Expected: PASS.

**Step 6: Run market-loop integration tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~MarketLoopTests
```

Expected: PASS, including the new pending-provider cycle test.

**Step 7: Commit, excluding this plan**

```bash
rtk git add TraderAi/TraderAi/Services/AiTraderCoordinator.cs TraderAi/TraderAi/Program.cs TraderAi/TraderAi.Tests/AiTraderCoordinatorTests.cs TraderAi/TraderAi.Tests/MarketLoopTests.cs
rtk git commit -m "feat: run AI traders in background"
```

### Task 9: Separate rule-based and provider-backed participants and reset AI data correctly

**Files:**

- Modify: `TraderAi/TraderAi/Services/MarketService.cs`
- Modify: `TraderAi/TraderAi/Services/MarketExitService.cs`
- Modify: `TraderAi/TraderAi.Tests/DecisionFlowTests.cs`
- Modify: `TraderAi/TraderAi.Tests/MarketExitServiceTests.cs`
- Modify: `TraderAi/TraderAi.Tests/MarketLoopTests.cs`
- Modify: `TraderAi/TraderAi.Tests/MarketApiTests.cs`

**Step 1: Add failing lifecycle tests**

Assert:

- `GenerateDecisionsAsync` sends Individuals and ordinary funds only to `IDecisionEngine`;
- configured and unconfigured `AIAgent` rows never reach the rule-based engine;
- a market-exit replacement is always `Individual` and no longer consumes a random type draw;
- deleting a departed AI participant cascades its configuration but retains its call rows;
- reset deletes `AiTraderCalls` first, then configurations, then participants; and
- reset clears both AI table sequences so a seeded market starts cleanly.

Update scripted random queues and the market-exit draw-discipline comment because removing the replacement type draw intentionally changes the sequence.

**Step 2: Run focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~DecisionFlowTests|FullyQualifiedName~MarketExitServiceTests|FullyQualifiedName~MarketLoopTests|FullyQualifiedName~MarketApiTests"
```

Expected: at least the new separation/reset assertions fail.

**Step 3: Implement the lifecycle rules**

Change the synchronous trader query to include only Individuals and Collective Funds. Keep all non-decision lifecycle behavior that already treats Individuals and AI Agents alike, including bankruptcy, fund membership, emissions, and exits.

Create replacement participants as `Individual` without drawing a type. Add both AI tables to reset in dependency order and include their sequence names in the SQLite sequence cleanup.

**Step 4: Rerun focused tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~DecisionFlowTests|FullyQualifiedName~MarketExitServiceTests|FullyQualifiedName~MarketLoopTests|FullyQualifiedName~MarketApiTests"
```

Expected: PASS.

**Step 5: Commit, excluding this plan**

```bash
rtk git add TraderAi/TraderAi/Services/MarketService.cs TraderAi/TraderAi/Services/MarketExitService.cs TraderAi/TraderAi.Tests/DecisionFlowTests.cs TraderAi/TraderAi.Tests/MarketExitServiceTests.cs TraderAi/TraderAi.Tests/MarketLoopTests.cs TraderAi/TraderAi.Tests/MarketApiTests.cs
rtk git commit -m "fix: separate AI and rule-based trading"
```

### Task 10: Add AI configuration, status, provider, and call-history APIs

**Files:**

- Modify: `TraderAi/TraderAi/Api/MarketEndpoints.cs`
- Modify: `TraderAi/TraderAi.Tests/MarketApiTests.cs`

**Step 1: Write failing API tests**

Cover:

- `GET /ai/providers` returns stable ID and display label for GLM and MiniMax;
- `PUT /participants/{id}/automation` converts an Individual with provider/key;
- the same endpoint converts AI back to Individual;
- validation errors return 400 and missing participants return 404;
- participant roster and detail responses contain provider ID, provider label, `hasAiApiKey`, runtime status, safe status message, and current call ID as appropriate;
- no response contains plaintext or encrypted API-key material;
- `GET /participants/{id}/ai-calls?page=1&pageSize=20` is newest first and returns metadata only;
- page size is bounded and total/page metadata is correct;
- `GET /participants/{id}/ai-calls/{callId}` returns the exact request JSON, raw response, parsed decision, and application result only when the call belongs to that participant; and
- converted-back Individuals can still read their call history.

**Step 2: Run API tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~MarketApiTests
```

Expected: new endpoint tests return 404 or fail to compile against missing response fields.

**Step 3: Add endpoint contracts**

Use:

```csharp
public sealed record AiProviderResponse(string Id, string Label);

public sealed record UpdateParticipantAutomationRequest(
    ParticipantType Type,
    string? ProviderId,
    string? ApiKey);

public sealed record AiTraderCallSummaryResponse(
    long Id,
    string ProviderId,
    string ProviderLabel,
    string Model,
    string Status,
    int SnapshotCycleNumber,
    string? Summary,
    int AppliedOrders,
    int RejectedOrders,
    long? DurationMilliseconds,
    DateTime RequestedAt,
    DateTime? RespondedAt,
    DateTime? AppliedAt);
```

The list response must project summary columns in SQL and must not materialize `RequestJson`, `ResponseBody`, `DecisionJson`, or `ApplicationResultJson`.

**Step 4: Extend participant responses without exposing secrets**

Add nullable provider/runtime fields to `ParticipantResponse` and `ParticipantDetailResponse`. Batch-load configurations once for roster responses; do not query one configuration per participant. The provider label comes from `AiProviderCatalog`, not from frontend mapping.

`hasAiApiKey` is true when an AI configuration exists. It does not inspect or expose the encrypted value.

**Step 5: Rerun API tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~MarketApiTests
```

Expected: PASS.

**Step 6: Commit, excluding this plan**

```bash
rtk git add TraderAi/TraderAi/Api/MarketEndpoints.cs TraderAi/TraderAi.Tests/MarketApiTests.cs
rtk git commit -m "feat: expose AI trader APIs"
```

### Task 11: Add frontend API helpers and pure automation-model tests

**Files:**

- Modify: `frontend/src/api.js`
- Create: `frontend/src/aiTraderModel.js`
- Create: `frontend/src/aiTraderModel.test.js`

**Step 1: Write failing model tests**

Test pure helpers for:

- formatting `AI · GLM`, `AI · MiniMax`, and future backend labels;
- requiring provider and key for first conversion;
- allowing an empty key when retaining the same configured provider;
- requiring a new key when provider changes;
- building the Individual conversion payload without key/provider; and
- formatting stored JSON for display without evaluating or rendering it as HTML.

Example expectation:

```js
assert.deepEqual(
  automationPayload({ type: 'AIAgent', providerId: 'glm', apiKey: 'secret', originalProviderId: null }),
  { type: 'AIAgent', providerId: 'glm', apiKey: 'secret' },
)
```

**Step 2: Run the new test and verify failure**

```bash
rtk node --test frontend/src/aiTraderModel.test.js
```

Expected: module exports are missing.

**Step 3: Implement helpers and API methods**

Add:

```js
getAiProviders: () => get('/ai/providers'),
updateParticipantAutomation: (participantId, payload) => put(`/participants/${participantId}/automation`, payload),
getParticipantAiCalls: (participantId, page = 1, pageSize = 20) =>
  get(`/participants/${participantId}/ai-calls${toQuery({ page, pageSize })}`),
getParticipantAiCall: (participantId, callId) => get(`/participants/${participantId}/ai-calls/${callId}`),
```

Keep key validation in the helper for immediate feedback, while treating backend validation as authoritative.

**Step 4: Run all frontend model tests**

```bash
rtk node --test frontend/src/*.test.js
```

Expected: PASS.

**Step 5: Commit, excluding this plan**

```bash
rtk git add frontend/src/api.js frontend/src/aiTraderModel.js frontend/src/aiTraderModel.test.js
rtk git commit -m "feat: add AI trader frontend model"
```

### Task 12: Add AI controls, labels, and paginated call history to the trader UI

**Files:**

- Create: `frontend/src/AiTraderAutomationPanel.jsx`
- Create: `frontend/src/AiTraderCallsPanel.jsx`
- Create: `frontend/src/AiTraderCallModal.jsx`
- Modify: `frontend/src/ParticipantDetail.jsx`
- Modify: `frontend/src/TradersTable.jsx`
- Modify: `frontend/src/App.css`

**Step 1: Add the detail-page automation panel**

Place `AiTraderAutomationPanel` alongside the existing temperament/risk profile area. It must:

- show a type selector limited to Individual and AI Agent for eligible participants;
- load provider options from the backend;
- reveal provider and password inputs only for AI Agent;
- use `type="password"` and never populate the key input from server data;
- show `API key configured. Enter a new key only to replace it.` when `hasAiApiKey` is true;
- require a new key when provider changes;
- show Waiting, Thinking, Applying, or Error as text, not color alone;
- convert back to Individual through the same Save action; and
- preserve unsaved edits while the detail page polls.

**Step 2: Add provider labels**

In `TradersTable`, render the backend label next to AI traders as `AI · {providerLabel}`. In the participant detail identity header, render the same provider tag plus a separate runtime-status tag. Do not label an unconfigured legacy AI as a functioning provider-backed trader.

**Step 3: Add the paginated AI calls panel**

The panel owns its page state and loads 20 summaries newest first. Reuse `Panel` and `Pager`. Display:

- requested time and snapshot cycle;
- provider/model;
- call status;
- duration;
- decision summary;
- applied/rejected counts; and
- a button to open full details.

Show the panel for current AI traders and for converted-back Individuals that have call history.

**Step 4: Add lazy full-call inspection**

`AiTraderCallModal` fetches one call only when opened and renders request, raw response, parsed decision, and application results in labeled `<pre>` regions. Render text only; never use `dangerouslySetInnerHTML`. Include loading, empty, and error states, keyboard-close behavior, focus management, and a visible close button.

**Step 5: Style within existing tokens**

Use the existing light terminal palette, tag, panel, table, button, focus, and modal patterns. Add responsive wrapping for long provider/model names and horizontally scroll JSON blocks. Preserve WCAG 2.1 AA contrast and reduced-motion behavior.

**Step 6: Run frontend verification**

```bash
rtk node --test frontend/src/*.test.js
rtk npm --prefix frontend run lint
rtk npm --prefix frontend run build
```

Expected: all commands succeed.

**Step 7: Manually verify the local flow**

Run:

```bash
rtk ./start-dev.sh
```

Verify:

1. select an Individual from the Traders page;
2. convert it to GLM or MiniMax with a test key;
3. confirm the key disappears after save while the provider label remains;
4. confirm the roster and detail header show the provider;
5. observe Thinking and either Applying or Error without market-cycle delay;
6. open the AI calls panel and page newest-first history;
7. open one call and inspect all stored JSON; and
8. convert back to Individual and confirm history remains but autonomous provider work stops.

Stop the development processes after verification.

**Step 8: Commit, excluding this plan**

```bash
rtk git add frontend/src/AiTraderAutomationPanel.jsx frontend/src/AiTraderCallsPanel.jsx frontend/src/AiTraderCallModal.jsx frontend/src/ParticipantDetail.jsx frontend/src/TradersTable.jsx frontend/src/App.css
rtk git commit -m "feat: manage AI traders from details"
```

### Task 13: Document the implemented AI-trader behavior

**Files:**

- Modify: `README.md`
- Modify: `PRODUCT.md`
- Modify: `docs/architecture.md`
- Modify: `docs/domain.md`
- Modify: `docs/participant-rules.md`
- Modify: `docs/roles/ai-agent.md`
- Modify: `docs/roles/individual.md`

**Step 1: Update durable architecture documentation**

Document that provider inference runs outside the market transaction, while application uses the shared market lock and ordinary validation. Explain encrypted configuration, one in-flight request per trader, five-minute documentation caching, strict JSON decisions, and database call auditing.

**Step 2: Update role and domain documentation**

Replace the placeholder AI Agent description with GLM/MiniMax behavior, failure-idle semantics, conversion rules, no short selling, lifecycle parity with Individuals, and replacement behavior. Clarify that Individuals remain rule-based.

**Step 3: Update product and README descriptions**

Change the planned LLM-backed engine language to implemented behavior. Mention provider labels, the write-only key UI, and paginated request/response observability without documenting internal agent guidance or secrets.

Do not duplicate the full market-rule list across documents. Keep focused behavior in `docs/roles/ai-agent.md` and link from broader pages using stable section paths.

**Step 4: Check documentation links and formatting**

```bash
rtk rg -n "AI Agent|GLM|MiniMax|AI trader" README.md PRODUCT.md docs
rtk git diff --check
```

Expected: references describe the implemented feature consistently and diff check reports no errors.

**Step 5: Commit documentation, excluding this plan**

```bash
rtk git add README.md PRODUCT.md docs/architecture.md docs/domain.md docs/participant-rules.md docs/roles/ai-agent.md docs/roles/individual.md
rtk git commit -m "docs: explain AI trader integration"
```

### Task 14: Run final verification and review the migration/security boundary

**Files:**

- Verify all modified production, test, migration, frontend, and documentation files.
- Do not stage or commit `docs/plans/ai-traders.md`.

**Step 1: Run the complete backend suite**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj
```

Expected: PASS with zero failed tests.

**Step 2: Run all frontend tests, lint, and production build**

```bash
rtk node --test frontend/src/*.test.js
rtk npm --prefix frontend run lint
rtk npm --prefix frontend run build
```

Expected: every command succeeds.

**Step 3: Validate migration and database behavior on a disposable database**

Use a temporary connection string or copied disposable database. Apply migrations and verify:

- preexisting type value `AIAgent` becomes `Individual`;
- AI configuration cascades on ordinary participant deletion;
- AI call history remains after ordinary participant deletion;
- full market reset removes calls and configurations; and
- no plaintext test key appears in SQLite text searches.

Do not run destructive migration checks against the user's active database.

**Step 4: Review request and response secrecy**

Search the diff for accidental credential exposure:

```bash
rtk rg -n "ApiKey|EncryptedApiKey|Authorization|Bearer" TraderAi frontend/src
```

Confirm every occurrence is a write-only request, encryption boundary, transient HTTP header, or negative test. Confirm call logs and API responses cannot serialize either key representation.

**Step 5: Run final repository checks**

```bash
rtk git diff --check
rtk git status --short
```

Expected: no whitespace errors; only intended feature files plus the uncommitted plan and the user's preexisting collective-fund changes are present.

**Step 6: Request code review before integration**

Use `@requesting-code-review` against the complete feature diff. Resolve actionable findings, rerun the directly affected tests, then rerun the complete verification commands before reporting completion.

