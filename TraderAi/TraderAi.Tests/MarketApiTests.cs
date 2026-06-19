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

            // Every company starts with one company-originated sell order (no participant seller).
            var companySell = (await client.GetFromJsonAsync<OrderDto[]>("/orders?status=open"))!
                .First(order => order.Type == "Sell");
            Assert.Null(companySell.ParticipantId);

            var participants = await client.GetFromJsonAsync<ParticipantDto[]>("/participants");
            var buyer = participants!.OrderByDescending(participant => participant.CurrentBalance).First();
            var price = companySell.LimitPrice;
            const int quantity = 5;

            using var buyResponse = await client.PostAsJsonAsync("/orders", new
            {
                participantId = buyer.Id,
                companyId = companySell.CompanyId,
                type = "Buy",
                quantity,
                limitPrice = price,
            });
            Assert.Equal(HttpStatusCode.OK, buyResponse.StatusCode);

            var advance = await (await client.PostAsync("/cycles/advance", null)).Content.ReadFromJsonAsync<AdvanceDto>();
            Assert.Equal(1, advance!.FillCount);

            var transactions = await client.GetFromJsonAsync<ShareTransactionDto[]>("/transactions/shares");
            var transaction = Assert.Single(transactions!);
            Assert.Equal(quantity, transaction.Quantity);
            Assert.Equal(price, transaction.Price);
            Assert.Null(transaction.SellerId);

            var companiesAfter = await client.GetFromJsonAsync<CompanyDto[]>("/companies");
            var companyAfter = companiesAfter!.Single(company => company.Id == companySell.CompanyId);
            Assert.Equal(price, companyAfter.CurrentPrice);

            var participantsAfter = await client.GetFromJsonAsync<ParticipantDto[]>("/participants");
            var buyerAfter = participantsAfter!.Single(participant => participant.Id == buyer.Id);
            Assert.Equal(quantity, buyerAfter.SharesOwned);
            Assert.Equal(0m, buyerAfter.ReservedBalance);
            Assert.Equal(buyer.CurrentBalance - (price * quantity), buyerAfter.CurrentBalance);
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

            // The seed lists one open sell order per company and nothing is filled yet.
            var openAfterSeed = await client.GetFromJsonAsync<OrderDto[]>("/orders?status=open");
            var allAfterSeed = await client.GetFromJsonAsync<OrderDto[]>("/orders");
            Assert.Equal(allAfterSeed!.Length, openAfterSeed!.Length);
            Assert.All(openAfterSeed, order => Assert.Equal("Sell", order.Type));

            var companySell = openAfterSeed.First(order => order.Type == "Sell");
            var buyer = (await client.GetFromJsonAsync<ParticipantDto[]>("/participants"))!
                .OrderByDescending(participant => participant.CurrentBalance).First();

            await client.PostAsJsonAsync("/orders", new
            {
                participantId = buyer.Id,
                companyId = companySell.CompanyId,
                type = "Buy",
                quantity = 5,
                limitPrice = companySell.LimitPrice,
            });

            Assert.Equal(allAfterSeed.Length + 1, (await client.GetFromJsonAsync<OrderDto[]>("/orders?status=open"))!.Length);

            await client.PostAsync("/cycles/advance", null);

            // The buy fully fills and leaves the open list; the partially filled company sell stays open.
            var openAfterAdvance = await client.GetFromJsonAsync<OrderDto[]>("/orders?status=open");
            var allAfterAdvance = await client.GetFromJsonAsync<OrderDto[]>("/orders");
            Assert.Equal(allAfterSeed.Length, openAfterAdvance!.Length);
            Assert.Equal(allAfterSeed.Length + 1, allAfterAdvance!.Length);
            Assert.All(openAfterAdvance, order => Assert.True(order.Status is "Open" or "PartiallyFilled"));
            Assert.Contains(allAfterAdvance, order => order.Type == "Buy" && order.Status == "Filled");
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

    private sealed record ShareTransactionDto(int Id, int? SellerId, int BuyerId, int Quantity, decimal Price);

    private sealed record AdvanceDto(int? CompletedCycleNumber, int FillCount);

    private sealed record MarketDto(int Id, string Name, string Status, int? CurrentCycleId);

    private sealed record OrderDto(
        int Id,
        int? ParticipantId,
        int CompanyId,
        string Type,
        string Status,
        int Quantity,
        int FilledQuantity,
        decimal LimitPrice);
}
