using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Api;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

// EF Core's SQLite provider compares decimal values (stored as TEXT) through a collation that parses them with
// the running thread's culture. On a machine whose locale uses a non-'.' decimal separator (e.g. en-KZ), that
// parse throws inside the native SQLite callback and crashes the process on any decimal ORDER BY or comparison,
// so the whole app is pinned to the invariant culture up front. This backend serialises numbers as culture-
// independent JSON and sorts names with an explicit ordinal comparer, so nothing depends on the OS locale.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

const string ReactDevelopmentCorsPolicy = "ReactDevelopment";

var builder = WebApplication.CreateBuilder(args);

var connectionString = ResolveSqliteConnectionString(
    builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=App_Data/trader-ai.db",
    builder.Environment.ContentRootPath);

EnsureSqliteDirectory(connectionString);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlite(connectionString);
});

builder.Services.AddSingleton(Random.Shared);
builder.Services.AddScoped<MatchingEngine>();
builder.Services.AddScoped<ITradeSizer, RandomTradeSizer>();
builder.Services.AddScoped<IDecisionEngine, RuleBasedDecisionEngine>();
builder.Services.AddScoped<MarketService>();
builder.Services.AddScoped<MarketImpactService>();
builder.Services.AddScoped<NewsService>();
builder.Services.AddScoped<CrisisService>();
builder.Services.AddScoped<ScienceInvestigationService>();
builder.Services.AddScoped<BankruptcyService>();
builder.Services.AddScoped<CollectiveFundService>();
builder.Services.AddScoped<MarketExitService>();
builder.Services.AddScoped<StockSplitService>();
builder.Services.AddScoped<AuditorService>();
builder.Services.AddScoped<ShareEmissionService>();
builder.Services.AddScoped<PrimaryIssuanceService>();
builder.Services.AddScoped<CompanyLifecycleService>();
builder.Services.AddScoped<LoanService>();
builder.Services.AddScoped<VolatilityHaltService>();
builder.Services.AddScoped<ConcentrationCapService>();
builder.Services.AddScoped<IndustrySentimentService>();
builder.Services.AddScoped<BehaviorAuditService>();
builder.Services.AddScoped<TradingClockService>();
builder.Services.AddScoped<SettlementService>();
builder.Services.AddScoped<MarginService>();
builder.Services.AddScoped<AutomatedBuyOrderPolicy>();
builder.Services.AddSingleton<MarketCycleLock>();
builder.Services.Configure<MarketLoopOptions>(builder.Configuration.GetSection(MarketLoopOptions.SectionName));
builder.Services.Configure<TradingClockOptions>(builder.Configuration.GetSection(TradingClockOptions.SectionName));
builder.Services.Configure<SettlementOptions>(builder.Configuration.GetSection(SettlementOptions.SectionName));
builder.Services.Configure<MarginOptions>(builder.Configuration.GetSection(MarginOptions.SectionName));
builder.Services.AddOptions<AutomatedTradingOptions>()
    .Bind(builder.Configuration.GetSection(AutomatedTradingOptions.SectionName))
    .Validate(options => options.IsValid(), "AutomatedTrading percentages and exposure ranges are invalid.")
    .ValidateOnStart();
builder.Services.Configure<NewsOptions>(builder.Configuration.GetSection(NewsOptions.SectionName));
builder.Services.Configure<CrisisOptions>(builder.Configuration.GetSection(CrisisOptions.SectionName));
builder.Services.Configure<ScienceInvestigationOptions>(builder.Configuration.GetSection(ScienceInvestigationOptions.SectionName));
builder.Services.Configure<BankruptcyOptions>(builder.Configuration.GetSection(BankruptcyOptions.SectionName));
builder.Services.Configure<CollectiveFundOptions>(builder.Configuration.GetSection(CollectiveFundOptions.SectionName));
builder.Services.Configure<MarketExitOptions>(builder.Configuration.GetSection(MarketExitOptions.SectionName));
builder.Services.Configure<StockSplitOptions>(builder.Configuration.GetSection(StockSplitOptions.SectionName));
builder.Services.Configure<AuditorOptions>(builder.Configuration.GetSection(AuditorOptions.SectionName));
builder.Services.Configure<ShareEmissionOptions>(builder.Configuration.GetSection(ShareEmissionOptions.SectionName));
builder.Services.AddOptions<PrimaryIssuanceOptions>()
    .Bind(builder.Configuration.GetSection(PrimaryIssuanceOptions.SectionName))
    .Validate(options => !options.Enabled
        || (options.FloatScarcityThresholdPercent is > 0m and <= 100m
            && options.MaximumDailyIssuancePercent is > 0m and <= 100m),
        "Enabled PrimaryIssuance percentages must be greater than 0 and no more than 100.")
    .ValidateOnStart();
builder.Services.Configure<CompanyLifecycleOptions>(builder.Configuration.GetSection(CompanyLifecycleOptions.SectionName));
builder.Services.Configure<LoanOptions>(builder.Configuration.GetSection(LoanOptions.SectionName));
builder.Services.Configure<TradeFeeOptions>(builder.Configuration.GetSection(TradeFeeOptions.SectionName));
builder.Services.Configure<VolatilityHaltOptions>(builder.Configuration.GetSection(VolatilityHaltOptions.SectionName));
builder.Services.Configure<ConcentrationCapOptions>(builder.Configuration.GetSection(ConcentrationCapOptions.SectionName));
builder.Services.Configure<ArchiveOptions>(builder.Configuration.GetSection(ArchiveOptions.SectionName));
builder.Services.Configure<IndustrySentimentOptions>(builder.Configuration.GetSection(IndustrySentimentOptions.SectionName));
builder.Services.AddOptions<AiTradingOptions>()
    .Bind(builder.Configuration.GetSection(AiTradingOptions.SectionName))
    .Validate(ValidateAiTrading, "AiTrading configuration is invalid: timing, concurrency, order, and retry limits must be positive, every provider endpoint must be HTTPS, and each provider must list at least one model.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AiProviderCatalog>();
builder.Services.AddSingleton<AiTraderRuntimeState>();
builder.Services.AddSingleton<AiPromptDocumentationProvider>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<IAiProviderClient, AiProviderClient>();
builder.Services.AddScoped<AiMarketSnapshotBuilder>();
builder.Services.AddScoped<AiTradingPromptBuilder>();
builder.Services.AddScoped<AiTraderCallService>();
builder.Services.AddScoped<AiTraderConfigurationService>();
builder.Services.AddHostedService<AiTraderCoordinator>();
builder.Services.AddOptions<RandomChanceRatesOptions>()
    .Bind(builder.Configuration.GetSection(RandomChanceRatesOptions.SectionName))
    .Validate(options =>
    {
        var stableChance = options.EventTriggerChances.AuditorIssueOnStable;
        var bigMoveChance = options.EventTriggerChances.AuditorIssueOnBigMove;
        var crisisMultiplier = options.ChanceModifiers.CrisisAuditorIssueMultiplier;
        return double.IsFinite(stableChance)
            && double.IsFinite(bigMoveChance)
            && double.IsFinite(crisisMultiplier)
            && stableChance is >= 0d and <= 0.5d
            && bigMoveChance is >= 0d and <= 0.5d
            && crisisMultiplier >= 0d
            && stableChance * crisisMultiplier <= 0.5d
            && bigMoveChance * crisisMultiplier <= 0.5d;
    }, "Auditor Extra outcome chances, including crisis adjustment, must remain between 0% and 50% to preserve symmetry.")
    .ValidateOnStart();
builder.Services.AddHostedService<MarketLoopService>();

var loopIntervalSeconds = builder.Configuration.GetValue<int>($"{MarketLoopOptions.SectionName}:IntervalSeconds");
var tradingCycleSeconds = builder.Configuration.GetValue<int>($"{TradingClockOptions.SectionName}:TradingCycleSeconds");
var breakDurationSeconds = builder.Configuration.GetValue<int>($"{TradingClockOptions.SectionName}:BreakDurationSeconds");
if (loopIntervalSeconds != tradingCycleSeconds)
{
    throw new InvalidOperationException("MarketLoop interval must match the trading-cycle duration.");
}
if (tradingCycleSeconds <= 0 || breakDurationSeconds <= 0 || breakDurationSeconds % tradingCycleSeconds != 0)
{
    throw new InvalidOperationException("The trading break must be a positive whole number of trading-cycle ticks.");
}

builder.Services.AddCors(options =>
{
    options.AddPolicy(ReactDevelopmentCorsPolicy, policy =>
    {
        policy
            .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

// Validate before database initialization so invalid probability configuration cannot mutate persistent state.
_ = app.Services.GetRequiredService<IOptions<RandomChanceRatesOptions>>().Value;

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // SQLite bakes auto_vacuum into the database header when the first table is written and cannot enable
    // it afterwards without a full VACUUM, so a brand-new file is configured on the same connection before migrating.
    if (!SqliteDatabaseExists(connectionString))
    {
        dbContext.Database.OpenConnection();
        using var configurePragma = dbContext.Database.GetDbConnection().CreateCommand();
        configurePragma.CommandText = "PRAGMA auto_vacuum = FULL;";
        configurePragma.ExecuteNonQuery();
    }

    dbContext.Database.Migrate();

    // The loop must never resume on its own across restarts: a running market is paused on boot so
    // it only advances after an explicit start.
    var market = await dbContext.Markets.FirstOrDefaultAsync();
    if (market is { Status: MarketStatus.Running })
    {
        market.Status = MarketStatus.Paused;
        market.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
    }
}

app.UseCors(ReactDevelopmentCorsPolicy);

app.MapGet("/health", () => Results.Ok(new { Result = SqliteDatabaseExists(connectionString) }));
app.MapMarketEndpoints();

app.Run();

static string ResolveSqliteConnectionString(string connectionString, string contentRootPath)
{
    var builder = new SqliteConnectionStringBuilder(connectionString);

    if (string.IsNullOrWhiteSpace(builder.DataSource) ||
        builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
    {
        return builder.ToString();
    }

    if (!Path.IsPathRooted(builder.DataSource))
    {
        builder.DataSource = Path.Combine(contentRootPath, builder.DataSource);
    }

    return builder.ToString();
}

static void EnsureSqliteDirectory(string connectionString)
{
    var builder = new SqliteConnectionStringBuilder(connectionString);

    if (string.IsNullOrWhiteSpace(builder.DataSource) ||
        builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));

    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }
}

static bool ValidateAiTrading(AiTradingOptions options)
{
    if (options.ScanIntervalMilliseconds <= 0
        || options.RequestTimeoutSeconds <= 0
        || options.MaxConcurrentRequests <= 0
        || options.MaxOrdersPerDecision <= 0
        || options.HistoryCycles <= 0
        || options.RetryBaseDelaySeconds <= 0
        || options.RetryMaxDelaySeconds < options.RetryBaseDelaySeconds
        || options.AuthErrorRetrySeconds <= 0)
    {
        return false;
    }

    foreach (var provider in options.Providers.Values)
    {
        if (string.IsNullOrWhiteSpace(provider.DisplayName)
            || provider.Models.Count == 0
            || !Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }
    }

    return true;
}

static bool SqliteDatabaseExists(string connectionString)
{
    var builder = new SqliteConnectionStringBuilder(connectionString);

    if (string.IsNullOrWhiteSpace(builder.DataSource) ||
        builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return File.Exists(Path.GetFullPath(builder.DataSource));
}

public partial class Program;
