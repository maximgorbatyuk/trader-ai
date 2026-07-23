using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class GameSettingsServiceTests
{
    [Fact]
    public void CatalogIncludesGameAndProviderSettingsWithHumanReadableMetadata()
    {
        var configuration = Configuration(
            ("Margin:InitialMarginRate", "0.50"),
            ("Auditor:AuditIntervalTradingDays", "2"),
            ("CompanyFinancial:StabilityWindowSnapshots", "6"),
            ("CompanyFinancial:ProfitabilityNetMarginWeight", "0.30"),
            ("RandomChanceRates:EventTriggerChances:NoSellOrderBuyChance", "0.80"),
            ("RandomChanceRates:EventTriggerChances:FinancialMetricChange", "0.45"),
            ("RandomChanceRates:RandomMagnitudeBands:FinancialSeedAssetsToMarketCapMin", "0.60"),
            ("AutomatedTrading:PassiveBuyPremiumMinPercent", "1"),
            ("AutomatedTrading:PassiveBuyPremiumMaxPercent", "15"),
            ("AiTrading:Providers:glm:DisplayName", "GLM"),
            ("AiTrading:Providers:glm:Endpoint", "https://api.example.com/chat"),
            ("AiTrading:Providers:glm:Models:0", "glm-4.6"));

        var definitions = GameSettingsCatalog.Create(configuration);

        var margin = Assert.Single(definitions, definition => definition.Key == "Margin:InitialMarginRate");
        Assert.Equal("Initial margin rate", margin.Name);
        Assert.NotEmpty(margin.Description);
        Assert.Equal(GameSettingValueType.Decimal, margin.ValueType);

        var noSellOrderBuyChance = Assert.Single(
            definitions,
            definition => definition.Key == "RandomChanceRates:EventTriggerChances:NoSellOrderBuyChance");
        Assert.Equal(GameSettingValueType.Decimal, noSellOrderBuyChance.ValueType);
        Assert.Contains("sell", noSellOrderBuyChance.Description, StringComparison.OrdinalIgnoreCase);

        var auditInterval = Assert.Single(
            definitions,
            definition => definition.Key == "Auditor:AuditIntervalTradingDays");
        Assert.Equal(GameSettingValueType.Integer, auditInterval.ValueType);
        Assert.Contains("trading days", auditInterval.Description, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            definitions,
            definition => definition.Key == "AutomatedTrading:PassiveBuyPremiumMinPercent"
                && definition.ValueType == GameSettingValueType.Decimal);
        Assert.Contains(
            definitions,
            definition => definition.Key == "AutomatedTrading:PassiveBuyPremiumMaxPercent"
                && definition.ValueType == GameSettingValueType.Decimal);

        var stabilityWindow = Assert.Single(
            definitions,
            definition => definition.Key == "CompanyFinancial:StabilityWindowSnapshots");
        Assert.Equal(GameSettingValueType.Integer, stabilityWindow.ValueType);
        Assert.Contains("history", stabilityWindow.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            definitions,
            definition => definition.Key == "CompanyFinancial:ProfitabilityNetMarginWeight"
                && definition.ValueType == GameSettingValueType.Decimal
                && !string.IsNullOrWhiteSpace(definition.Description));
        Assert.Contains(
            definitions,
            definition => definition.Key == "RandomChanceRates:EventTriggerChances:FinancialMetricChange"
                && definition.ValueType == GameSettingValueType.Decimal
                && !string.IsNullOrWhiteSpace(definition.Description));
        Assert.Contains(
            definitions,
            definition => definition.Key == "RandomChanceRates:RandomMagnitudeBands:FinancialSeedAssetsToMarketCapMin"
                && definition.ValueType == GameSettingValueType.Decimal
                && !string.IsNullOrWhiteSpace(definition.Description));

        var models = Assert.Single(definitions, definition => definition.Key == "AiTrading:Providers:glm:Models");
        Assert.Equal("GLM models", models.Name);
        Assert.Equal(GameSettingValueType.StringList, models.ValueType);
    }

    [Fact]
    public void CatalogExcludesInfrastructureSettings()
    {
        var configuration = Configuration(
            ("Logging:LogLevel:Default", "Warning"),
            ("ConnectionStrings:DefaultConnection", "Data Source=test.db"),
            ("Archive:Enabled", "true"),
            ("AiTrading:DocumentationRoot", "../../docs"),
            ("AiTrading:ApiKey", "secret"),
            ("Margin:ConnectionString", "Data Source=secret.db"));

        var keys = GameSettingsCatalog.Create(configuration).Select(definition => definition.Key).ToHashSet();

        Assert.DoesNotContain("Logging:LogLevel:Default", keys);
        Assert.DoesNotContain("ConnectionStrings:DefaultConnection", keys);
        Assert.DoesNotContain("Archive:Enabled", keys);
        Assert.DoesNotContain("AiTrading:DocumentationRoot", keys);
        Assert.DoesNotContain("AiTrading:ApiKey", keys);
        Assert.DoesNotContain("Margin:ConnectionString", keys);
    }

    [Fact]
    public void CatalogSurfacesProviderApiKeyAsSecret()
    {
        var configuration = Configuration(
            ("AiTrading:Providers:glm:DisplayName", "GLM"),
            ("AiTrading:Providers:glm:Endpoint", "https://api.example.com/chat"),
            ("AiTrading:Providers:glm:ApiKey", ""),
            ("AiTrading:Providers:glm:Models:0", "glm-4.6"));

        var apiKey = Assert.Single(
            GameSettingsCatalog.Create(configuration),
            definition => definition.Key == "AiTrading:Providers:glm:ApiKey");
        Assert.Equal(GameSettingValueType.Secret, apiKey.ValueType);
        Assert.Equal("Providers", apiKey.Subsection);
    }

    [Fact]
    public async Task StoresSettingByConfigurationKey()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();

        context.GameSettings.Add(new GameSetting
        {
            Key = "Margin:InitialMarginRate",
            ValueJson = "0.50",
        });
        await context.SaveChangesAsync();

        var stored = await context.GameSettings.SingleAsync();
        Assert.Equal("Margin:InitialMarginRate", stored.Key);
        Assert.Equal("0.50", stored.ValueJson);
    }

    [Fact]
    public async Task InitializationSeedsMissingDefaultsAndPreservesSavedValues()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);

        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
            context.GameSettings.Add(new GameSetting
            {
                Key = "Margin:InitialMarginRate",
                ValueJson = "0.25",
            });
            await context.SaveChangesAsync();
        }

        var service = new GameSettingsService(factory, Configuration(
            ("Margin:Enabled", "true"),
            ("Margin:InitialMarginRate", "0.50"),
            ("Logging:LogLevel:Default", "Warning")));

        await service.InitializeAsync();

        await using var verification = await factory.CreateDbContextAsync();
        var stored = await verification.GameSettings.OrderBy(setting => setting.Key).ToListAsync();
        Assert.Collection(
            stored,
            setting =>
            {
                Assert.Equal("Margin:Enabled", setting.Key);
                Assert.Equal("true", setting.ValueJson);
            },
            setting =>
            {
                Assert.Equal("Margin:InitialMarginRate", setting.Key);
                Assert.Equal("0.25", setting.ValueJson);
            });
    }

    [Fact]
    public async Task CachedOptionsChangeOnlyAfterSettingsServiceUpdatesTheDatabase()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);
        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var service = new GameSettingsService(factory, Configuration(
            ("Margin:Enabled", "true"),
            ("Margin:InitialMarginRate", "0.50")));
        await service.InitializeAsync();

        await using (var context = await factory.CreateDbContextAsync())
        {
            var row = await context.GameSettings.SingleAsync(setting => setting.Key == "Margin:InitialMarginRate");
            row.ValueJson = "0.40";
            await context.SaveChangesAsync();
        }

        Assert.Equal(0.50m, service.GetOptions<MarginOptions>(MarginOptions.SectionName).InitialMarginRate);

        using var updatedValue = JsonDocument.Parse("0.30");
        await service.UpdateAsync(new Dictionary<string, JsonElement>
        {
            ["Margin:InitialMarginRate"] = updatedValue.RootElement.Clone(),
        });

        Assert.Equal(0.30m, service.GetOptions<MarginOptions>(MarginOptions.SectionName).InitialMarginRate);
    }

    [Fact]
    public async Task OptionsAdapterReadsTheCurrentCachedSection()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(dbOptions);
        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var service = new GameSettingsService(factory, Configuration(("Margin:InitialMarginRate", "0.50")));
        await service.InitializeAsync();
        IOptions<MarginOptions> options = new GameSettingsOptions<MarginOptions>(service, MarginOptions.SectionName);

        Assert.Equal(0.50m, options.Value.InitialMarginRate);
    }

    [Fact]
    public async Task AutomatedPassiveBuyChanceChangesOnlyThroughDatabaseBackedSettingsUpdate()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(dbOptions);
        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var service = new GameSettingsService(factory, Configuration(
            ("RandomChanceRates:EventTriggerChances:NoSellOrderBuyChance", "0.80")));
        await service.InitializeAsync();
        IOptions<RandomChanceRatesOptions> options = new GameSettingsOptions<RandomChanceRatesOptions>(
            service,
            RandomChanceRatesOptions.SectionName);

        using var updatedChance = JsonDocument.Parse("0.65");
        await service.UpdateAsync(new Dictionary<string, JsonElement>
        {
            ["RandomChanceRates:EventTriggerChances:NoSellOrderBuyChance"] = updatedChance.RootElement.Clone(),
        });

        Assert.Equal(0.65, options.Value.EventTriggerChances.NoSellOrderBuyChance);
    }

    [Fact]
    public async Task InvalidFinancialSettingsAreRejectedWithoutPartialWrites()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(dbOptions);
        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var service = new GameSettingsService(factory, Configuration(
            ("CompanyFinancial:Enabled", "true"),
            ("CompanyFinancial:StabilityWindowSnapshots", "6"),
            ("CompanyFinancial:LowLevelMaximumScore", "34"),
            ("CompanyFinancial:HighLevelMinimumScore", "67")));
        await service.InitializeAsync();

        using var invalidHighBoundary = JsonDocument.Parse("20");
        using var validWindow = JsonDocument.Parse("8");
        var exception = await Assert.ThrowsAsync<GameSettingsValidationException>(() =>
            service.UpdateAsync(new Dictionary<string, JsonElement>
            {
                ["CompanyFinancial:HighLevelMinimumScore"] = invalidHighBoundary.RootElement.Clone(),
                ["CompanyFinancial:StabilityWindowSnapshots"] = validWindow.RootElement.Clone(),
            }));

        Assert.True(exception.Errors.ContainsKey("CompanyFinancial"));
        Assert.Equal(6, service.GetOptions<CompanyFinancialOptions>(
            CompanyFinancialOptions.SectionName).StabilityWindowSnapshots);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(
            "6",
            (await verification.GameSettings.SingleAsync(
                setting => setting.Key == "CompanyFinancial:StabilityWindowSnapshots")).ValueJson);
    }

    [Fact]
    public async Task ProviderCatalogUsesProviderValuesSavedAfterItWasCreated()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(dbOptions);
        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var service = new GameSettingsService(factory, Configuration(
            ("AiTrading:Enabled", "true"),
            ("AiTrading:Providers:glm:DisplayName", "GLM"),
            ("AiTrading:Providers:glm:Endpoint", "https://api.example.com/chat"),
            ("AiTrading:Providers:glm:Models:0", "glm-4.6")));
        await service.InitializeAsync();
        var options = new GameSettingsOptions<AiTradingOptions>(service, AiTradingOptions.SectionName);
        var catalog = new AiProviderCatalog(options);

        using var updatedName = JsonDocument.Parse("\"Z.AI\"");
        await service.UpdateAsync(new Dictionary<string, JsonElement>
        {
            ["AiTrading:Providers:glm:DisplayName"] = updatedName.RootElement.Clone(),
        });

        Assert.Equal("Z.AI", Assert.Single(catalog.All).Label);
    }

    [Fact]
    public void CatalogExposesAiSystemPromptSettingsAsMultilineText()
    {
        var configuration = Configuration(
            ("AiTrading:SystemPromptTemplate", "You are a trader. Include at most {maxOrders} orders."),
            ("AiTrading:FinalDecisionInstruction", "This is your final decision of the day."));

        var definitions = GameSettingsCatalog.Create(configuration);

        var template = Assert.Single(definitions, definition => definition.Key == "AiTrading:SystemPromptTemplate");
        Assert.Equal(GameSettingValueType.MultilineText, template.ValueType);
        Assert.NotEmpty(template.Description);
        var instruction = Assert.Single(
            definitions,
            definition => definition.Key == "AiTrading:FinalDecisionInstruction");
        Assert.Equal(GameSettingValueType.MultilineText, instruction.ValueType);
    }

    [Fact]
    public async Task AiSystemPromptSeedsFromDefaultAndUpdatesThroughDatabase()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(dbOptions);
        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var service = new GameSettingsService(factory, Configuration(
            ("AiTrading:Enabled", "true"),
            ("AiTrading:SystemPromptTemplate", "Seeded prompt with at most {maxOrders} orders."),
            ("AiTrading:FinalDecisionInstruction", "Seeded end-of-day guidance."),
            ("AiTrading:Providers:glm:DisplayName", "GLM"),
            ("AiTrading:Providers:glm:Endpoint", "https://api.example.com/chat"),
            ("AiTrading:Providers:glm:Models:0", "glm-4.6")));
        await service.InitializeAsync();
        var options = new GameSettingsOptions<AiTradingOptions>(service, AiTradingOptions.SectionName);

        Assert.Equal("Seeded prompt with at most {maxOrders} orders.", options.Value.SystemPromptTemplate);

        using var updated = JsonDocument.Parse("\"Deploy the whole balance in {maxOrders} large orders.\"");
        await service.UpdateAsync(new Dictionary<string, JsonElement>
        {
            ["AiTrading:SystemPromptTemplate"] = updated.RootElement.Clone(),
        });

        Assert.Equal("Deploy the whole balance in {maxOrders} large orders.", options.Value.SystemPromptTemplate);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(value => value.Key, value => (string?)value.Value))
            .Build();

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
