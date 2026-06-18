using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TraderAi.Tests;

public sealed class MarketApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public MarketApiTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task SeedPlaceOrdersAndAdvanceSettlesTheTrade()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            using var seedResponse = await client.PostAsync("/market/seed", null);
            Assert.Equal(HttpStatusCode.OK, seedResponse.StatusCode);

            var participants = await client.GetFromJsonAsync<ParticipantDto[]>("/participants");
            var companies = await client.GetFromJsonAsync<CompanyDto[]>("/companies");

            var seller = participants!.Single(participant => participant.SharesOwned > 0);
            var buyer = participants!.Single(participant => participant.SharesOwned == 0);
            var company = companies!.Single();

            using var sellResponse = await client.PostAsJsonAsync("/orders", new
            {
                participantId = seller.Id,
                companyId = company.Id,
                type = "Sell",
                quantity = 5,
                limitPrice = 100m,
            });
            Assert.Equal(HttpStatusCode.OK, sellResponse.StatusCode);

            using var buyResponse = await client.PostAsJsonAsync("/orders", new
            {
                participantId = buyer.Id,
                companyId = company.Id,
                type = "Buy",
                quantity = 5,
                limitPrice = 100m,
            });
            Assert.Equal(HttpStatusCode.OK, buyResponse.StatusCode);

            var advance = await (await client.PostAsync("/cycles/advance", null)).Content.ReadFromJsonAsync<AdvanceDto>();
            Assert.Equal(1, advance!.FillCount);

            var transactions = await client.GetFromJsonAsync<ShareTransactionDto[]>("/transactions/shares");
            var transaction = Assert.Single(transactions!);
            Assert.Equal(5, transaction.Quantity);
            Assert.Equal(100m, transaction.Price);

            var companiesAfter = await client.GetFromJsonAsync<CompanyDto[]>("/companies");
            Assert.Equal(100m, companiesAfter!.Single().CurrentPrice);

            var participantsAfter = await client.GetFromJsonAsync<ParticipantDto[]>("/participants");
            var buyerAfter = participantsAfter!.Single(participant => participant.Id == buyer.Id);
            Assert.Equal(5, buyerAfter.SharesOwned);
            Assert.Equal(0m, buyerAfter.ReservedBalance);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SeedReturnsConflictWhenMarketAlreadyExists()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            using var firstSeed = await client.PostAsync("/market/seed", null);
            Assert.Equal(HttpStatusCode.OK, firstSeed.StatusCode);

            using var secondSeed = await client.PostAsync("/market/seed", null);
            Assert.Equal(HttpStatusCode.Conflict, secondSeed.StatusCode);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task OrdersStatusFilterReturnsOnlyOpenOrders()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            await client.PostAsync("/market/seed", null);

            var participants = await client.GetFromJsonAsync<ParticipantDto[]>("/participants");
            var company = (await client.GetFromJsonAsync<CompanyDto[]>("/companies"))!.Single();
            var seller = participants!.Single(participant => participant.SharesOwned > 0);
            var buyer = participants!.Single(participant => participant.SharesOwned == 0);

            await client.PostAsJsonAsync("/orders", new { participantId = seller.Id, companyId = company.Id, type = "Sell", quantity = 5, limitPrice = 100m });
            await client.PostAsJsonAsync("/orders", new { participantId = buyer.Id, companyId = company.Id, type = "Buy", quantity = 5, limitPrice = 100m });

            Assert.Equal(2, (await client.GetFromJsonAsync<OrderDto[]>("/orders?status=open"))!.Length);

            await client.PostAsync("/cycles/advance", null);

            Assert.Empty(await client.GetFromJsonAsync<OrderDto[]>("/orders?status=open"));
            Assert.Equal(2, (await client.GetFromJsonAsync<OrderDto[]>("/orders"))!.Length);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PauseAndStartToggleMarketStatus()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            using var seedResponse = await client.PostAsync("/market/seed", null);
            Assert.Equal(HttpStatusCode.OK, seedResponse.StatusCode);

            var paused = await (await client.PostAsync("/market/pause", null)).Content.ReadFromJsonAsync<MarketDto>();
            Assert.Equal("Paused", paused!.Status);

            var started = await (await client.PostAsync("/market/start", null)).Content.ReadFromJsonAsync<MarketDto>();
            Assert.Equal("Running", started!.Status);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory(string databasePath)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={databasePath}");
        });
    }

    private sealed record ParticipantDto(int Id, string Name, decimal CurrentBalance, decimal ReservedBalance, int SharesOwned);

    private sealed record CompanyDto(int Id, string Name, decimal? CurrentPrice);

    private sealed record ShareTransactionDto(int Id, int Quantity, decimal Price);

    private sealed record AdvanceDto(int? CompletedCycleNumber, int FillCount);

    private sealed record MarketDto(int Id, string Name, string Status, int? CurrentCycleId);

    private sealed record OrderDto(int Id, string Type, string Status, int Quantity, int FilledQuantity);
}
