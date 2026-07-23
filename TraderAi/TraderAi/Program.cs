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

// The default AI system prompt is too large to keep readable in appsettings.json, so its single source of truth is
// AiTradingOptions and it is injected here as the configuration default the game-settings catalog seeds from.
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["AiTrading:SystemPromptTemplate"] = AiTradingOptions.DefaultSystemPromptTemplate,
    ["AiTrading:FinalDecisionInstruction"] = AiTradingOptions.DefaultFinalDecisionInstruction,
});

var connectionString = ResolveSqliteConnectionString(
    builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=App_Data/trader-ai.db",
    builder.Environment.ContentRootPath);

EnsureSqliteDirectory(connectionString);

builder.Services.AddDbContextFactory<AppDbContext>(options =>
{
    options.UseSqlite(connectionString);
});
builder.Services.AddSingleton<GameSettingsService>();

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
builder.Services.AddScoped<CompanyFinancialScorer>();
builder.Services.AddScoped<CompanyFinancialService>();
builder.Services.AddScoped<ShareEmissionService>();
builder.Services.AddScoped<PrimaryIssuanceService>();
builder.Services.AddScoped<BigInvestmentService>();
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
builder.Services.AddOptions<AuditorOptions>()
    .Bind(builder.Configuration.GetSection(AuditorOptions.SectionName))
    .Validate(
        options => options.IsValid(),
        "Auditor interval, metric boundaries, factor directions, score clamps, status thresholds, and decision pulls must form valid ordered ranges.")
    .ValidateOnStart();
builder.Services.AddOptions<CompanyFinancialOptions>()
    .Bind(builder.Configuration.GetSection(CompanyFinancialOptions.SectionName))
    .Validate(options => options.IsValid(), "CompanyFinancial windows, weights, score levels, invariants, and dividend rules are invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<TradingSignalOptions>()
    .Bind(builder.Configuration.GetSection(TradingSignalOptions.SectionName))
    .Validate(
        options => options.IsValid(),
        "TradingSignal component and blend weights, wait weight, and personality response factors are invalid.")
    .ValidateOnStart();
builder.Services.Configure<ShareEmissionOptions>(builder.Configuration.GetSection(ShareEmissionOptions.SectionName));
builder.Services.Configure<BigInvestmentOptions>(builder.Configuration.GetSection(BigInvestmentOptions.SectionName));
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
builder.Services.AddScoped<AiPredictionEvaluationService>();
builder.Services.AddHostedService<AiTraderCoordinator>();
builder.Services.AddOptions<RandomChanceRatesOptions>()
    .Bind(builder.Configuration.GetSection(RandomChanceRatesOptions.SectionName))
    .Validate(options => options.IsValid(), "Random probabilities, seed ranges, update magnitudes, and passive-price offsets are invalid.")
    .ValidateOnStart();
builder.Services.AddHostedService<MarketLoopService>();

AddGameSettingsOptions<MarketLoopOptions>(builder.Services, MarketLoopOptions.SectionName);
AddGameSettingsOptions<TradingClockOptions>(builder.Services, TradingClockOptions.SectionName);
AddGameSettingsOptions<SettlementOptions>(builder.Services, SettlementOptions.SectionName);
AddGameSettingsOptions<MarginOptions>(builder.Services, MarginOptions.SectionName);
AddGameSettingsOptions<AutomatedTradingOptions>(builder.Services, AutomatedTradingOptions.SectionName);
AddGameSettingsOptions<NewsOptions>(builder.Services, NewsOptions.SectionName);
AddGameSettingsOptions<CrisisOptions>(builder.Services, CrisisOptions.SectionName);
AddGameSettingsOptions<ScienceInvestigationOptions>(builder.Services, ScienceInvestigationOptions.SectionName);
AddGameSettingsOptions<BankruptcyOptions>(builder.Services, BankruptcyOptions.SectionName);
AddGameSettingsOptions<CollectiveFundOptions>(builder.Services, CollectiveFundOptions.SectionName);
AddGameSettingsOptions<MarketExitOptions>(builder.Services, MarketExitOptions.SectionName);
AddGameSettingsOptions<StockSplitOptions>(builder.Services, StockSplitOptions.SectionName);
AddGameSettingsOptions<AuditorOptions>(builder.Services, AuditorOptions.SectionName);
AddGameSettingsOptions<CompanyFinancialOptions>(builder.Services, CompanyFinancialOptions.SectionName);
AddGameSettingsOptions<TradingSignalOptions>(builder.Services, TradingSignalOptions.SectionName);
AddGameSettingsOptions<ShareEmissionOptions>(builder.Services, ShareEmissionOptions.SectionName);
AddGameSettingsOptions<BigInvestmentOptions>(builder.Services, BigInvestmentOptions.SectionName);
AddGameSettingsOptions<PrimaryIssuanceOptions>(builder.Services, PrimaryIssuanceOptions.SectionName);
AddGameSettingsOptions<CompanyLifecycleOptions>(builder.Services, CompanyLifecycleOptions.SectionName);
AddGameSettingsOptions<LoanOptions>(builder.Services, LoanOptions.SectionName);
AddGameSettingsOptions<TradeFeeOptions>(builder.Services, TradeFeeOptions.SectionName);
AddGameSettingsOptions<VolatilityHaltOptions>(builder.Services, VolatilityHaltOptions.SectionName);
AddGameSettingsOptions<ConcentrationCapOptions>(builder.Services, ConcentrationCapOptions.SectionName);
AddGameSettingsOptions<IndustrySentimentOptions>(builder.Services, IndustrySentimentOptions.SectionName);
AddGameSettingsOptions<AiTradingOptions>(builder.Services, AiTradingOptions.SectionName);
AddGameSettingsOptions<RandomChanceRatesOptions>(builder.Services, RandomChanceRatesOptions.SectionName);

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

ValidateDefaultAuditorOptions(builder.Configuration);
ValidateDefaultCompanyFinancialOptions(builder.Configuration);
ValidateDefaultTradingSignalOptions(builder.Configuration);
ValidateDefaultRandomChanceRates(builder.Configuration);

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
    await app.Services.GetRequiredService<GameSettingsService>().InitializeAsync();

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
app.MapSettingsEndpoints();

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
        || options.MaxResponseTokens < 0
        || options.MaxInvalidJsonRetries < 0
        || options.MaxTransportRetries < 0
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
            || provider.RequestTimeoutSeconds is <= 0
            || provider.MaxResponseTokens is < 0
            || provider.MaxInvalidJsonRetries is < 0
            || provider.MaxTransportRetries is < 0
            || !Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }
    }

    return true;
}

static void AddGameSettingsOptions<TOptions>(IServiceCollection services, string sectionName)
    where TOptions : class, new()
{
    services.AddSingleton<IOptions<TOptions>>(serviceProvider =>
        new GameSettingsOptions<TOptions>(
            serviceProvider.GetRequiredService<GameSettingsService>(),
            sectionName));
}

static void ValidateDefaultRandomChanceRates(IConfiguration configuration)
{
    var options = configuration.GetSection(RandomChanceRatesOptions.SectionName)
        .Get<RandomChanceRatesOptions>() ?? new RandomChanceRatesOptions();
    if (options.IsValid())
    {
        return;
    }

    throw new OptionsValidationException(
        Options.DefaultName,
        typeof(RandomChanceRatesOptions),
        ["Random probabilities, seed ranges, update magnitudes, and passive-price offsets are invalid."]);
}

static void ValidateDefaultTradingSignalOptions(IConfiguration configuration)
{
    var options = configuration.GetSection(TradingSignalOptions.SectionName)
        .Get<TradingSignalOptions>() ?? new TradingSignalOptions();
    if (options.IsValid())
    {
        return;
    }

    throw new OptionsValidationException(
        Options.DefaultName,
        typeof(TradingSignalOptions),
        ["TradingSignal component and blend weights, wait weight, and personality response factors are invalid."]);
}

static void ValidateDefaultCompanyFinancialOptions(IConfiguration configuration)
{
    var options = configuration.GetSection(CompanyFinancialOptions.SectionName)
        .Get<CompanyFinancialOptions>() ?? new CompanyFinancialOptions();
    if (options.IsValid())
    {
        return;
    }

    throw new OptionsValidationException(
        Options.DefaultName,
        typeof(CompanyFinancialOptions),
        ["CompanyFinancial windows, weights, score levels, invariants, and dividend rules are invalid."]);
}

static void ValidateDefaultAuditorOptions(IConfiguration configuration)
{
    var options = configuration.GetSection(AuditorOptions.SectionName)
        .Get<AuditorOptions>() ?? new AuditorOptions();
    if (options.IsValid())
    {
        return;
    }

    throw new OptionsValidationException(
        Options.DefaultName,
        typeof(AuditorOptions),
        ["Auditor interval, metric boundaries, factor directions, score clamps, status thresholds, and decision pulls must form valid ordered ranges."]);
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
