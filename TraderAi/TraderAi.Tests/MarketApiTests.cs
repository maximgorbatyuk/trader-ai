using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TraderAi.Data;
using TraderAi.Models;
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

            // Seed close and the fill are both in the first cycle at the same price, so there is no
            // prior-cycle move to report.
            Assert.Equal(0m, companyAfter.PriceChangePct);

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

            Assert.Equal(100, companies!.Length);
            Assert.Equal(300, participants!.Length);
            Assert.Equal(100, openOrders!.Length);
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
            Assert.Equal(100, openOrders!.Length);
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

    [Fact]
    public async Task ParticipantDetailReturnsBalancesAndOwnership()
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
                quantity = 4,
                limitPrice = companySell.LimitPrice,
            });
            await client.PostAsync("/cycles/tick", null);

            var detail = await client.GetFromJsonAsync<ParticipantDetailDto>($"/participants/{buyer.Id}");

            Assert.Equal(buyer.Id, detail!.Id);
            Assert.True(detail.InitialBalance > 0m);
            Assert.Equal(4, detail.SharesOwned);
            Assert.Equal(detail.CurrentBalance - detail.ReservedBalance, detail.AvailableBalance);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateParticipantProfilePersistsTemperamentAndRisk()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            await client.PostAsync("/market/seed", null);
            var target = (await client.GetFromJsonAsync<ParticipantDto[]>("/participants"))!.First();

            using var putResponse = await client.PutAsJsonAsync(
                $"/participants/{target.Id}/profile",
                new { temperament = "Conservative", riskProfile = "Low" });
            Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

            var updated = await putResponse.Content.ReadFromJsonAsync<ParticipantDetailDto>();
            Assert.Equal("Conservative", updated!.Temperament);
            Assert.Equal("Low", updated.RiskProfile);

            var reloaded = await client.GetFromJsonAsync<ParticipantDetailDto>($"/participants/{target.Id}");
            Assert.Equal("Conservative", reloaded!.Temperament);
            Assert.Equal("Low", reloaded.RiskProfile);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateMissingParticipantReturnsNotFound()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            await client.PostAsync("/market/seed", null);

            using var putResponse = await client.PutAsJsonAsync(
                "/participants/999999/profile",
                new { temperament = "Balanced", riskProfile = "Medium" });
            Assert.Equal(HttpStatusCode.NotFound, putResponse.StatusCode);

            using var getResponse = await client.GetAsync("/participants/999999");
            Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task HoldingsIncludeMarketValueAndCostBasis()
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
            const int quantity = 5;
            var price = companySell.LimitPrice;

            await client.PostAsJsonAsync("/orders", new
            {
                participantId = buyer.Id,
                companyId = companySell.CompanyId,
                type = "Buy",
                quantity,
                limitPrice = price,
            });
            await client.PostAsync("/cycles/tick", null);

            var holding = Assert.Single(
                (await client.GetFromJsonAsync<HoldingDto[]>($"/participants/{buyer.Id}/holdings"))!);

            // The fill executes at the issuer's resting ask, so cost basis and current value both track it.
            Assert.Equal(price, holding.CurrentPrice);
            Assert.Equal(price * quantity, holding.CostBasis);
            Assert.Equal(price * quantity, holding.MarketValue);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ParticipantScopedHistoryFiltersToThatParticipant()
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
            var participants = (await client.GetFromJsonAsync<ParticipantDto[]>("/participants"))!
                .OrderByDescending(participant => participant.CurrentBalance)
                .ToArray();
            var buyer = participants[0];
            var bystander = participants[1];

            await client.PostAsJsonAsync("/orders", new
            {
                participantId = buyer.Id,
                companyId = companySell.CompanyId,
                type = "Buy",
                quantity = 3,
                limitPrice = companySell.LimitPrice,
            });
            await client.PostAsync("/cycles/tick", null);

            var buyerOrder = Assert.Single(
                (await client.GetFromJsonAsync<OrderDto[]>($"/participants/{buyer.Id}/orders"))!);
            Assert.Equal(buyer.Id, buyerOrder.ParticipantId);
            Assert.Empty((await client.GetFromJsonAsync<OrderDto[]>($"/participants/{bystander.Id}/orders"))!);

            var buyerTrade = Assert.Single(
                (await client.GetFromJsonAsync<ShareTransactionDto[]>($"/participants/{buyer.Id}/share-transactions"))!);
            Assert.Equal(buyer.Id, buyerTrade.BuyerId);

            var cashMoves = await client.GetFromJsonAsync<MoneyTransactionDto[]>(
                $"/participants/{buyer.Id}/money-transactions");
            Assert.NotEmpty(cashMoves!);
            Assert.Contains(cashMoves!, move => move.Type == "Debit");
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CompanyDetailReturnsPriceAndOwnershipSplit()
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
            const int quantity = 5;
            var price = companySell.LimitPrice;

            await client.PostAsJsonAsync("/orders", new
            {
                participantId = buyer.Id,
                companyId = companySell.CompanyId,
                type = "Buy",
                quantity,
                limitPrice = price,
            });
            await client.PostAsync("/cycles/tick", null);

            var detail = await client.GetFromJsonAsync<CompanyDetailDto>($"/companies/{companySell.CompanyId}");

            Assert.Equal(companySell.CompanyId, detail!.Id);
            Assert.False(string.IsNullOrWhiteSpace(detail.Name));
            Assert.True(detail.IssuedSharesCount > 0);
            Assert.Equal(price, detail.CurrentPrice);
            Assert.Equal(quantity, detail.SharesOutstanding);
            Assert.Equal(detail.IssuedSharesCount - quantity, detail.SharesHeldByIssuer);
            Assert.Equal(1, detail.ShareholderCount);
            Assert.Equal(price * detail.IssuedSharesCount, detail.MarketCap);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CompanyShareholdersListOwnersAfterPurchase()
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
            const int quantity = 4;
            var price = companySell.LimitPrice;

            await client.PostAsJsonAsync("/orders", new
            {
                participantId = buyer.Id,
                companyId = companySell.CompanyId,
                type = "Buy",
                quantity,
                limitPrice = price,
            });
            await client.PostAsync("/cycles/tick", null);

            var shareholders = await client.GetFromJsonAsync<ShareholderDto[]>(
                $"/companies/{companySell.CompanyId}/shareholders");

            var shareholder = Assert.Single(shareholders!);
            Assert.Equal(buyer.Id, shareholder.OwnerId);
            Assert.Equal(buyer.Name, shareholder.OwnerName);
            Assert.Equal(quantity, shareholder.Shares);
            Assert.Equal(price * quantity, shareholder.MarketValue);
            Assert.Equal(price * quantity, shareholder.CostBasis);
            Assert.True(shareholder.PctOfIssued > 0m);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CompanyScopedHistoryFiltersToThatCompany()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            await client.PostAsync("/market/seed", null);

            var sellOrders = (await client.GetFromJsonAsync<OrderDto[]>("/orders?status=open"))!
                .Where(order => order.Type == "Sell")
                .ToArray();
            var tradedCompanyId = sellOrders[0].CompanyId;
            var untouchedCompanyId = sellOrders[1].CompanyId;
            var buyer = (await client.GetFromJsonAsync<ParticipantDto[]>("/participants"))!
                .OrderByDescending(participant => participant.CurrentBalance).First();

            await client.PostAsJsonAsync("/orders", new
            {
                participantId = buyer.Id,
                companyId = tradedCompanyId,
                type = "Buy",
                quantity = 3,
                limitPrice = sellOrders[0].LimitPrice,
            });
            await client.PostAsync("/cycles/tick", null);

            var companyOrders = await client.GetFromJsonAsync<OrderDto[]>($"/companies/{tradedCompanyId}/orders");
            Assert.Contains(companyOrders!, order => order.Type == "Buy" && order.ParticipantId == buyer.Id);
            Assert.All(companyOrders!, order => Assert.Equal(tradedCompanyId, order.CompanyId));

            var companyTrades = await client.GetFromJsonAsync<ShareTransactionDto[]>(
                $"/companies/{tradedCompanyId}/share-transactions");
            Assert.NotEmpty(companyTrades!);

            Assert.Empty((await client.GetFromJsonAsync<ShareTransactionDto[]>(
                $"/companies/{untouchedCompanyId}/share-transactions"))!);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CompanyDetailReturnsNotFoundForMissingCompany()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            await client.PostAsync("/market/seed", null);

            using var getResponse = await client.GetAsync("/companies/999999");
            Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MarketExitsEndpointMapsCycleNumbers()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var joined = new MarketCycle { CycleNumber = 5, Status = CycleStatus.Completed, StartedAt = now };
                var left = new MarketCycle { CycleNumber = 42, Status = CycleStatus.Running, StartedAt = now };
                dbContext.MarketCycles.AddRange(joined, left);
                await dbContext.SaveChangesAsync();

                dbContext.MarketExits.Add(new MarketExit
                {
                    ParticipantId = 999,
                    Name = "Gone Trader",
                    Reason = MarketExitReason.Starvation,
                    JoinedInCycleId = joined.Id,
                    LeftInCycleId = left.Id,
                    OrdersPlaced = 7,
                    InitialBalance = 100_000m,
                    MaxTotalWorth = 250_000m,
                    QuitBalance = 3_000m,
                    LeftAt = now,
                });
                await dbContext.SaveChangesAsync();
            }

            var exits = await client.GetFromJsonAsync<MarketExitDto[]>("/market-exits");
            var exit = Assert.Single(exits!);
            Assert.Equal("Gone Trader", exit.ParticipantName);
            Assert.Equal("Starvation", exit.Reason);
            Assert.Equal(5, exit.JoinedInCycleNumber);
            Assert.Equal(42, exit.LeftInCycleNumber);
            Assert.Equal(7, exit.OrdersPlaced);
            Assert.Equal(100_000m, exit.InitialBalance);
            Assert.Equal(250_000m, exit.MaxTotalWorth);
            Assert.Equal(3_000m, exit.QuitBalance);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task BankruptciesResolveDepartedTraderNameViaFallback()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            const int departedId = 777;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var cycle = new MarketCycle { CycleNumber = 12, Status = CycleStatus.Completed, StartedAt = now };
                dbContext.MarketCycles.Add(cycle);
                await dbContext.SaveChangesAsync();

                // The bankrupt trader has since left the market: no Participant row survives, but its name is
                // archived on the MarketExit row for the fallback to resolve.
                dbContext.MarketExits.Add(new MarketExit
                {
                    ParticipantId = departedId,
                    Name = "Departed Bankrupt",
                    Reason = MarketExitReason.Starvation,
                    JoinedInCycleId = 0,
                    LeftInCycleId = cycle.Id,
                    OrdersPlaced = 3,
                    InitialBalance = 50_000m,
                    MaxTotalWorth = 60_000m,
                    QuitBalance = 0m,
                    LeftAt = now,
                });
                dbContext.Bankruptcies.Add(new Bankruptcy
                {
                    ParticipantId = departedId,
                    Title = "Collapse",
                    Content = "Wiped out.",
                    CashLost = 10_000m,
                    ShareWorth = 0m,
                    TriggeredInCycleId = cycle.Id,
                    TriggeredAt = now,
                });
                await dbContext.SaveChangesAsync();
            }

            var bankruptcies = await client.GetFromJsonAsync<BankruptcyDto[]>("/bankruptcies");
            var bankruptcy = Assert.Single(bankruptcies!);
            Assert.Equal(departedId, bankruptcy.ParticipantId);
            Assert.Equal("Departed Bankrupt", bankruptcy.ParticipantName);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AuditorsEndpointReturnsSeededAgenciesAndDetail()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            var auditors = await client.GetFromJsonAsync<AuditorDto[]>("/auditors");
            // Five percent of the hundred seeded companies.
            Assert.Equal(5, auditors!.Length);
            Assert.All(auditors, auditor => Assert.False(string.IsNullOrWhiteSpace(auditor.Description)));
            Assert.All(auditors, auditor => Assert.Equal(0, auditor.AuditCount));

            var one = await client.GetFromJsonAsync<AuditorDto>($"/auditors/{auditors[0].Id}");
            Assert.Equal(auditors[0].Id, one!.Id);

            using var missing = await client.GetAsync("/auditors/999999");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AuditorAuditsEndpointPaginatesNewestFirst()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            int auditorId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                auditorId = await dbContext.Auditors.Select(auditor => auditor.Id).FirstAsync();
                var companyId = await dbContext.Companies.Select(company => company.Id).FirstAsync();
                var cycleId = (await dbContext.Markets.FirstAsync()).CurrentCycleId!.Value;
                for (var index = 0; index < 25; index++)
                {
                    dbContext.CompanyRatings.Add(new CompanyRating
                    {
                        CompanyId = companyId,
                        AuditorId = auditorId,
                        Rating = CompanyRiskRating.Low,
                        CreatedInCycleId = cycleId,
                        CreatedAt = DateTime.UtcNow,
                    });
                }

                await dbContext.SaveChangesAsync();
            }

            var page1 = await client.GetFromJsonAsync<PagedAuditsDto>($"/auditors/{auditorId}/audits?page=1&pageSize=20");
            Assert.Equal(25, page1!.Total);
            Assert.Equal(20, page1.Items.Length);
            // Newest first.
            Assert.True(page1.Items[0].Id > page1.Items[^1].Id);
            Assert.All(page1.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.CompanyName)));

            var page2 = await client.GetFromJsonAsync<PagedAuditsDto>($"/auditors/{auditorId}/audits?page=2&pageSize=20");
            Assert.Equal(5, page2!.Items.Length);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CompanyDetailAndRatingsReflectTheLatestVerdicts()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            int companyId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                companyId = await dbContext.Companies.Select(company => company.Id).FirstAsync();
                var auditorId = await dbContext.Auditors.Select(auditor => auditor.Id).FirstAsync();
                var cycleId = (await dbContext.Markets.FirstAsync()).CurrentCycleId!.Value;

                dbContext.CompanyRatings.Add(new CompanyRating
                {
                    CompanyId = companyId, AuditorId = auditorId, Rating = CompanyRiskRating.High,
                    CreatedInCycleId = cycleId, CreatedAt = DateTime.UtcNow,
                });
                dbContext.CompanyRatings.Add(new CompanyRating
                {
                    CompanyId = companyId, AuditorId = auditorId, Rating = CompanyRiskRating.Extra, ImpactPercent = 25m,
                    CreatedInCycleId = cycleId, CreatedAt = DateTime.UtcNow,
                });
                await dbContext.SaveChangesAsync();
            }

            var detail = await client.GetFromJsonAsync<CompanyDetailDto>($"/companies/{companyId}");
            Assert.Equal("Extra", detail!.CurrentRating);
            Assert.Equal("High", detail.PreviousRating);

            var ratings = await client.GetFromJsonAsync<CompanyRatingDto[]>($"/companies/{companyId}/ratings");
            Assert.Equal(2, ratings!.Length);
            Assert.Equal("Extra", ratings[0].Rating);
            Assert.Equal(25m, ratings[0].ImpactPercent);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CompanyEmissionsEndpointReturnsRecords()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            int companyId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                companyId = await dbContext.Companies.Select(company => company.Id).FirstAsync();
                var cycleId = (await dbContext.Markets.FirstAsync()).CurrentCycleId!.Value;
                dbContext.ShareEmissions.Add(new ShareEmission
                {
                    CompanyId = companyId, SharesEmitted = 100, RecipientCount = 2,
                    CreatedInCycleId = cycleId, CreatedAt = DateTime.UtcNow,
                });
                await dbContext.SaveChangesAsync();
            }

            var emissions = await client.GetFromJsonAsync<ShareEmissionDto[]>($"/companies/{companyId}/emissions");
            var emission = Assert.Single(emissions!);
            Assert.Equal(100, emission.SharesEmitted);
            Assert.Equal(2, emission.RecipientCount);
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

            // Auditors fire every cycle under the shared Random and would perturb the exact-count assertions
            // below; they are exercised directly against seeded rating rows instead.
            builder.UseSetting("Auditor:Enabled", "false");

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

    private sealed record CompanyDto(int Id, string Name, decimal? CurrentPrice, decimal PriceChangePct);

    private sealed record CompanyDetailDto(
        int Id,
        string Name,
        int IssuedSharesCount,
        decimal? CurrentPrice,
        decimal PriceChangePct,
        decimal MarketCap,
        int SharesHeldByIssuer,
        int SharesOutstanding,
        int ShareholderCount,
        string? CurrentRating,
        string? PreviousRating);

    private sealed record AuditorDto(int Id, string Name, string Description, int AuditCount);

    private sealed record AuditRowDto(int Id, int CompanyId, string CompanyName, string Rating, decimal? ImpactPercent, int CyclesAgo);

    private sealed record PagedAuditsDto(AuditRowDto[] Items, int Total, int Page, int PageSize);

    private sealed record CompanyRatingDto(int Id, string Rating, decimal? ImpactPercent, string AuditorName, int CyclesAgo);

    private sealed record ShareEmissionDto(int Id, int SharesEmitted, int RecipientCount, int CyclesAgo);

    private sealed record ShareholderDto(
        int OwnerId,
        string OwnerName,
        int Shares,
        decimal MarketValue,
        decimal CostBasis,
        decimal PctOfIssued);

    private sealed record ShareTransactionDto(int Id, int? SellerId, int BuyerId, int Quantity, decimal Price);

    private sealed record MarketExitDto(
        int Id,
        int ParticipantId,
        string ParticipantName,
        string Reason,
        int JoinedInCycleNumber,
        int LeftInCycleNumber,
        int OrdersPlaced,
        decimal InitialBalance,
        decimal MaxTotalWorth,
        decimal QuitBalance,
        DateTime LeftAt);

    private sealed record BankruptcyDto(int Id, int ParticipantId, string ParticipantName);

    private sealed record CycleTickDto(bool Ran, int? CompletedCycleNumber, int OrdersPlaced, int FillCount);

    private sealed record CycleDto(int Id, int CycleNumber);

    private sealed record HoldingDto(
        int CompanyId,
        string CompanyName,
        int Shares,
        decimal CurrentPrice,
        decimal MarketValue,
        decimal CostBasis);

    private sealed record ParticipantDetailDto(
        int Id,
        string Name,
        string Type,
        string Temperament,
        string RiskProfile,
        decimal InitialBalance,
        decimal CurrentBalance,
        decimal ReservedBalance,
        decimal AvailableBalance,
        int SharesOwned,
        bool IsActive);

    private sealed record MoneyTransactionDto(int Id, string Type, decimal Amount, int CreatedInCycleId);

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
