using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
