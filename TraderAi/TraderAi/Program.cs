using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Api;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

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
builder.Services.AddSingleton<MarketCycleLock>();
builder.Services.Configure<MarketLoopOptions>(builder.Configuration.GetSection(MarketLoopOptions.SectionName));
builder.Services.Configure<NewsOptions>(builder.Configuration.GetSection(NewsOptions.SectionName));
builder.Services.Configure<CrisisOptions>(builder.Configuration.GetSection(CrisisOptions.SectionName));
builder.Services.Configure<ScienceInvestigationOptions>(builder.Configuration.GetSection(ScienceInvestigationOptions.SectionName));
builder.Services.Configure<BankruptcyOptions>(builder.Configuration.GetSection(BankruptcyOptions.SectionName));
builder.Services.Configure<CollectiveFundOptions>(builder.Configuration.GetSection(CollectiveFundOptions.SectionName));
builder.Services.Configure<MarketExitOptions>(builder.Configuration.GetSection(MarketExitOptions.SectionName));
builder.Services.Configure<StockSplitOptions>(builder.Configuration.GetSection(StockSplitOptions.SectionName));
builder.Services.AddHostedService<MarketLoopService>();

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

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
