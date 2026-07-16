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
            ("RandomChanceRates:EventTriggerChances:NoSellOrderBuyChance", "0.80"),
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

        Assert.Contains(
            definitions,
            definition => definition.Key == "AutomatedTrading:PassiveBuyPremiumMinPercent"
                && definition.ValueType == GameSettingValueType.Decimal);
        Assert.Contains(
            definitions,
            definition => definition.Key == "AutomatedTrading:PassiveBuyPremiumMaxPercent"
                && definition.ValueType == GameSettingValueType.Decimal);

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
            ("AiTrading:Providers:glm:ApiKey", "provider-secret"),
            ("Margin:ConnectionString", "Data Source=secret.db"));

        var keys = GameSettingsCatalog.Create(configuration).Select(definition => definition.Key).ToHashSet();

        Assert.DoesNotContain("Logging:LogLevel:Default", keys);
        Assert.DoesNotContain("ConnectionStrings:DefaultConnection", keys);
        Assert.DoesNotContain("Archive:Enabled", keys);
        Assert.DoesNotContain("AiTrading:DocumentationRoot", keys);
        Assert.DoesNotContain("AiTrading:ApiKey", keys);
        Assert.DoesNotContain("AiTrading:Providers:glm:ApiKey", keys);
        Assert.DoesNotContain("Margin:ConnectionString", keys);
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
