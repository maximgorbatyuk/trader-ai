using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TraderAi.Services;

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

            var tick = await (await client.PostAsync("/cycles/tick", null)).Content.ReadFromJsonAsync<CycleTickDto>();
            Assert.Equal(1, tick!.FillCount);

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
    public async Task ResetSeedsMarketWhenDatabaseIsEmpty()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            using var resetResponse = await client.PostAsync("/market/reset", null);
            Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);

            var market = await resetResponse.Content.ReadFromJsonAsync<MarketDto>();
            Assert.Equal(1, market!.Id);
            Assert.Equal("NotStarted", market.Status);
            Assert.Equal(1, market.CurrentCycleId);

            var companies = await client.GetFromJsonAsync<CompanyDto[]>("/companies");
            var participants = await client.GetFromJsonAsync<ParticipantDto[]>("/participants");
            var openOrders = await client.GetFromJsonAsync<OrderDto[]>("/orders?status=open");

            Assert.Equal(40, companies!.Length);
            Assert.Equal(20, participants!.Length);
            Assert.Equal(40, openOrders!.Length);
            Assert.All(openOrders, order =>
            {
                Assert.Null(order.ParticipantId);
                Assert.Equal("Sell", order.Type);
            });
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ResetRecreatesSeededMarketAndClearsTradingState()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            await client.PostAsync("/market/seed", null);

            var companySell = (await client.GetFromJsonAsync<OrderDto[]>("/orders?status=open"))!
                .First(order => order.Type == "Sell");
            var buyer = (await client.GetFromJsonAsync<ParticipantDto[]>("/participants"))!
                .OrderByDescending(participant => participant.CurrentBalance)
                .First();

            using var buyResponse = await client.PostAsJsonAsync("/orders", new
            {
                participantId = buyer.Id,
                companyId = companySell.CompanyId,
                type = "Buy",
                quantity = 3,
                limitPrice = companySell.LimitPrice,
            });
            Assert.Equal(HttpStatusCode.OK, buyResponse.StatusCode);

            await client.PostAsync("/cycles/tick", null);
            Assert.NotEmpty((await client.GetFromJsonAsync<ShareTransactionDto[]>("/transactions/shares"))!);

            using var resetResponse = await client.PostAsync("/market/reset", null);
            Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);

            var market = await resetResponse.Content.ReadFromJsonAsync<MarketDto>();
            Assert.Equal(1, market!.Id);
            Assert.Equal("NotStarted", market.Status);
            Assert.Equal(1, market.CurrentCycleId);

            var cycles = await client.GetFromJsonAsync<CycleDto[]>("/cycles");
            var transactions = await client.GetFromJsonAsync<ShareTransactionDto[]>("/transactions/shares");
            var activity = await client.GetFromJsonAsync<ActivityDto[]>("/cycles/activity");
            var openOrders = await client.GetFromJsonAsync<OrderDto[]>("/orders?status=open");

            var cycle = Assert.Single(cycles!);
            Assert.Equal(1, cycle.Id);
            Assert.Equal(1, cycle.CycleNumber);
            Assert.Empty(transactions!);
            Assert.Single(activity!);
            Assert.Equal(40, openOrders!.Length);
            Assert.All(openOrders, order =>
            {
                Assert.Null(order.ParticipantId);
                Assert.Equal("Sell", order.Type);
            });
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

            await client.PostAsync("/cycles/tick", null);

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

    [Fact]
    public async Task HoldingsReturnPurchasedSharesGroupedByCompany()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            await client.PostAsync("/market/seed", null);

            var companySell = (await client.GetFromJsonAsync<OrderDto[]>("/orders?status=open"))!
                .First(order => order.Type == "Sell");
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
            await client.PostAsync("/cycles/tick", null);

            var holdings = await client.GetFromJsonAsync<HoldingDto[]>($"/participants/{buyer.Id}/holdings");

            var holding = Assert.Single(holdings!);
            Assert.Equal(companySell.CompanyId, holding.CompanyId);
            Assert.Equal(5, holding.Shares);
            Assert.False(string.IsNullOrWhiteSpace(holding.CompanyName));
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CycleActivityCountsParticipantOrdersAndExcludesIssuerOrders()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            await client.PostAsync("/market/seed", null);

            // The seed's sell orders all belong to the issuer, so the first cycle reports no activity.
            var afterSeed = await client.GetFromJsonAsync<ActivityDto[]>("/cycles/activity");
            Assert.All(afterSeed!, point => Assert.Equal(0, point.OrdersPlaced));

            var companySell = (await client.GetFromJsonAsync<OrderDto[]>("/orders?status=open"))!
                .First(order => order.Type == "Sell");
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

            var afterBuy = await client.GetFromJsonAsync<ActivityDto[]>("/cycles/activity");
            Assert.Contains(afterBuy!, point => point.OrdersPlaced == 1);
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

            // A manual tick decides then matches; the no-op engine removes generated trades so these
            // tests settle only the order they place by hand and can assert exact counts.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDecisionEngine>();
                services.AddScoped<IDecisionEngine, NoOpDecisionEngine>();
            });
        });
    }

    private sealed record ParticipantDto(int Id, string Name, decimal CurrentBalance, decimal ReservedBalance, int SharesOwned);

    private sealed record CompanyDto(int Id, string Name, decimal? CurrentPrice);

    private sealed record ShareTransactionDto(int Id, int? SellerId, int BuyerId, int Quantity, decimal Price);

    private sealed record CycleTickDto(bool Ran, int? CompletedCycleNumber, int OrdersPlaced, int FillCount);

    private sealed record CycleDto(int Id, int CycleNumber);

    private sealed record HoldingDto(int CompanyId, string CompanyName, int Shares);

    private sealed record ActivityDto(int CycleNumber, int OrdersPlaced);

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
