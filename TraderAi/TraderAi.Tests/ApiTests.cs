using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Tests;

public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task CompaniesReturnsEmptyArray()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var configuredClient = configuredFactory.CreateClient();

            using var response = await configuredClient.GetAsync("/companies");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);

            Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
            Assert.Equal(0, document.RootElement.GetArrayLength());
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task HealthReturnsTrueAfterStartupCreatesDatabase()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var configuredClient = configuredFactory.CreateClient();

            using var response = await configuredClient.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);

            Assert.True(document.RootElement.GetProperty("result").GetBoolean());
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartupCreatesSqliteDatabaseWhenMissing()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var configuredClient = configuredFactory.CreateClient();

            using var response = await configuredClient.GetAsync("/companies");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(File.Exists(databasePath));
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsReturnsGameMetadataAndDatabaseValues()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var configuredClient = configuredFactory.CreateClient();

            var settings = await configuredClient.GetFromJsonAsync<JsonElement[]>("/settings");

            var margin = Assert.Single(settings!, setting =>
                setting.GetProperty("key").GetString() == "Margin:InitialMarginRate");
            Assert.Equal("Initial margin rate", margin.GetProperty("name").GetString());
            Assert.NotEmpty(margin.GetProperty("description").GetString()!);
            Assert.Equal("Decimal", margin.GetProperty("valueType").GetString());
            Assert.Equal(0.50m, margin.GetProperty("value").GetDecimal());
            Assert.DoesNotContain(settings!, setting =>
                setting.GetProperty("key").GetString() == "Archive:RetentionCycles");
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsUpdatePersistsAndRefreshesTheRuntimeValue()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var configuredClient = configuredFactory.CreateClient();

            using var response = await configuredClient.PutAsJsonAsync("/settings", new
            {
                values = new Dictionary<string, object>
                {
                    ["Margin:InitialMarginRate"] = 0.40m,
                },
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var scope = configuredFactory.Services.CreateScope();
            var runtimeOptions = scope.ServiceProvider.GetRequiredService<IOptions<TraderAi.Services.MarginOptions>>();
            Assert.Equal(0.40m, runtimeOptions.Value.InitialMarginRate);

            var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GameSettings
                .SingleAsync(setting => setting.Key == "Margin:InitialMarginRate");
            Assert.Equal("0.40", stored.ValueJson);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsUpdateRejectsInvalidValuesWithoutPartialWrites()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var configuredClient = configuredFactory.CreateClient();

            using var response = await configuredClient.PutAsJsonAsync("/settings", new
            {
                values = new Dictionary<string, object>
                {
                    ["Margin:InitialMarginRate"] = "not-a-number",
                    ["Margin:MaintenanceMarginRate"] = 0.20m,
                },
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var scope = configuredFactory.Services.CreateScope();
            var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GameSettings
                .Where(setting => setting.Key.StartsWith("Margin:"))
                .ToDictionaryAsync(setting => setting.Key, setting => setting.ValueJson);
            Assert.Equal("0.50", rows["Margin:InitialMarginRate"]);
            Assert.Equal("0.25", rows["Margin:MaintenanceMarginRate"]);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsUpdateRejectsUnknownKeysAndInvalidProviderData()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var configuredClient = configuredFactory.CreateClient();

            using var response = await configuredClient.PutAsJsonAsync("/settings", new
            {
                values = new Dictionary<string, object>
                {
                    ["AiTrading:ApiKey"] = "secret",
                    ["AiTrading:Providers:glm:Endpoint"] = "http://insecure.example.com/chat",
                },
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var errors = body.GetProperty("errors");
            Assert.True(errors.TryGetProperty("AiTrading:ApiKey", out _));
            Assert.True(errors.TryGetProperty("AiTrading:Providers:glm:Endpoint", out _));

            using var nullResponse = await configuredClient.PutAsJsonAsync("/settings", new { values = (object?)null });
            Assert.Equal(HttpStatusCode.BadRequest, nullResponse.StatusCode);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsUpdateRejectsAuditorExtraOutcomeChanceAboveHalfWithALowMultiplier()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var configuredClient = configuredFactory.CreateClient();

            using var response = await configuredClient.PutAsJsonAsync("/settings", new
            {
                values = new Dictionary<string, object>
                {
                    ["RandomChanceRates:EventTriggerChances:AuditorIssueOnBigMove"] = 0.75,
                    ["RandomChanceRates:ChanceModifiers:CrisisAuditorIssueMultiplier"] = 0.5,
                },
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("errors").TryGetProperty("RandomChanceRates", out _));
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsUpdateRejectsBigInvestmentMaximumAboveHalf()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var configuredClient = configuredFactory.CreateClient();

            using var response = await configuredClient.PutAsJsonAsync("/settings", new
            {
                values = new Dictionary<string, object>
                {
                    ["RandomChanceRates:EventTriggerChances:BigInvestmentMax"] = 0.75,
                },
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("errors").TryGetProperty("RandomChanceRates", out _));
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsUpdateRejectsAnIncompatibleMarketLoopCadence()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var configuredClient = configuredFactory.CreateClient();

            using var response = await configuredClient.PutAsJsonAsync("/settings", new
            {
                values = new Dictionary<string, object>
                {
                    ["MarketLoop:IntervalSeconds"] = 3,
                },
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var scope = configuredFactory.Services.CreateScope();
            var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GameSettings
                .SingleAsync(setting => setting.Key == "MarketLoop:IntervalSeconds");
            Assert.Equal("2", stored.ValueJson);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsUpdateRejectsAnInvalidGameplayFraction()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var configuredClient = configuredFactory.CreateClient();

            using var response = await configuredClient.PutAsJsonAsync("/settings", new
            {
                values = new Dictionary<string, object>
                {
                    ["CollectiveFund:CashBufferFraction"] = 1.10m,
                },
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public void StartupRejectsAuditorExtraOutcomeChanceAboveFiftyPercent()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={databasePath}");
                builder.UseSetting("RandomChanceRates:EventTriggerChances:AuditorIssueOnBigMove", "0.20");
                builder.UseSetting("RandomChanceRates:ChanceModifiers:CrisisAuditorIssueMultiplier", "3.0");
            });

            var exception = Assert.Throws<OptionsValidationException>(() => configuredFactory.CreateClient());
            Assert.Contains("50%", exception.Message);
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CompaniesReturnsStoredCompanies()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var configuredClient = configuredFactory.CreateClient();

            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                dbContext.Companies.Add(new Company { Name = "Acme Markets" });
                await dbContext.SaveChangesAsync();
            }

            using var response = await configuredClient.GetAsync("/companies");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);

            var companies = document.RootElement;
            Assert.Equal(JsonValueKind.Array, companies.ValueKind);
            Assert.Equal(1, companies.GetArrayLength());
            Assert.Equal("Acme Markets", companies[0].GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task NewsThemesFilterToFinanceThemesForScopedNewsAndKeepWhimsicalThemesByDefault()
    {
        using var client = factory.CreateClient();

        var allThemes = await client.GetFromJsonAsync<NewsThemeDto[]>("/news/themes");
        var companyThemes = await client.GetFromJsonAsync<NewsThemeDto[]>("/news/themes?scope=Company");
        var industryThemes = await client.GetFromJsonAsync<NewsThemeDto[]>("/news/themes?scope=Industries");

        Assert.Contains(allThemes!, theme => theme.Key == "ufo");
        Assert.DoesNotContain(allThemes!, theme => theme.Key == "market-sentiment");
        Assert.All([companyThemes, industryThemes], themes =>
        {
            var scoped = Assert.Single(themes!);
            Assert.Equal("market-sentiment", scoped.Key);
        });
    }

    private WebApplicationFactory<Program> CreateFactory(string databasePath)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={databasePath}");
        });
    }

    private sealed record NewsThemeDto(string Key, string Label);
}
