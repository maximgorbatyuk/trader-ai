using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task PlayerCanMarkAndUnmarkAFavoriteCompany()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databaseDirectory);
        try
        {
            using var configuredFactory = CreateFactory(Path.Combine(databaseDirectory, "app.db"));
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);
            await client.PostAsJsonAsync("/player", new { name = "Ada" });

            using var initialCompanies = await client.GetFromJsonAsync<JsonDocument>("/companies");
            var initialCompany = initialCompanies!.RootElement.EnumerateArray().First();
            var companyId = initialCompany.GetProperty("id").GetInt32();
            Assert.False(initialCompany.GetProperty("isFavorite").GetBoolean());

            using var marked = await client.PutAsync($"/player/favorite-companies/{companyId}", null);
            Assert.Equal(HttpStatusCode.NoContent, marked.StatusCode);

            using var favoriteCompanies = await client.GetFromJsonAsync<JsonDocument>("/companies");
            var favoriteCompany = favoriteCompanies!.RootElement.EnumerateArray()
                .Single(company => company.GetProperty("id").GetInt32() == companyId);
            Assert.True(favoriteCompany.GetProperty("isFavorite").GetBoolean());

            using var favoriteDetail = await client.GetFromJsonAsync<JsonDocument>($"/companies/{companyId}");
            Assert.True(favoriteDetail!.RootElement.GetProperty("isFavorite").GetBoolean());

            using var unmarked = await client.DeleteAsync($"/player/favorite-companies/{companyId}");
            Assert.Equal(HttpStatusCode.NoContent, unmarked.StatusCode);

            using var ordinaryDetail = await client.GetFromJsonAsync<JsonDocument>($"/companies/{companyId}");
            Assert.False(ordinaryDetail!.RootElement.GetProperty("isFavorite").GetBoolean());
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FavoriteCompanyMutationRequiresAPlayerAndKnownCompany()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databaseDirectory);
        try
        {
            using var configuredFactory = CreateFactory(Path.Combine(databaseDirectory, "app.db"));
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            using var companies = await client.GetFromJsonAsync<JsonDocument>("/companies");
            var companyId = companies!.RootElement.EnumerateArray().First().GetProperty("id").GetInt32();

            using var withoutPlayer = await client.PutAsync($"/player/favorite-companies/{companyId}", null);
            Assert.Equal(HttpStatusCode.NotFound, withoutPlayer.StatusCode);
            Assert.Contains("No player exists.", await withoutPlayer.Content.ReadAsStringAsync());

            await client.PostAsJsonAsync("/player", new { name = "Ada" });
            using var missingCompany = await client.PutAsync("/player/favorite-companies/999999", null);
            Assert.Equal(HttpStatusCode.NotFound, missingCompany.StatusCode);
            Assert.Contains("Company not found.", await missingCompany.Content.ReadAsStringAsync());
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AccountingContractsShareValuationAndPendingSettlementSummary()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databaseDirectory);
        try
        {
            using var configuredFactory = CreateFactory(Path.Combine(databaseDirectory, "app.db"));
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);
            await client.PostAsJsonAsync("/player", new { name = "Ada" });
            using var openedFund = await client.PostAsJsonAsync("/player/fund", new { seedAmount = 500m, name = "Ada Fund" });
            using var openedFundJson = await JsonDocument.ParseAsync(await openedFund.Content.ReadAsStreamAsync());
            var fundId = openedFundJson.RootElement.GetProperty("fundParticipantId").GetInt32();

            int playerId;
            decimal expectedPlayerWorth;
            decimal expectedFundWorth;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var player = await db.Participants.SingleAsync(participant => participant.Type == ParticipantType.Player);
                var fund = await db.Participants.SingleAsync(participant => participant.Id == fundId);
                var company = await db.Companies.OrderBy(company => company.Id).FirstAsync();
                var cycle = await db.MarketCycles.OrderByDescending(cycle => cycle.Id).FirstAsync();
                var bank = await db.Banks.OrderBy(bank => bank.Id).FirstAsync();
                var price = await db.PriceSnapshots
                    .Where(snapshot => snapshot.CompanyId == company.Id)
                    .OrderByDescending(snapshot => snapshot.Id)
                    .Select(snapshot => snapshot.Price)
                    .FirstAsync();

                playerId = player.Id;
                db.Holdings.Add(new Holding
                {
                    ParticipantId = player.Id,
                    CompanyId = company.Id,
                    Quantity = 10,
                    SettledQuantity = 8,
                    AverageCost = price,
                });
                db.Loans.Add(new Loan
                {
                    BankId = bank.Id,
                    ParticipantId = player.Id,
                    Principal = 100m,
                    RemainingPrincipal = 100m,
                    PastDueInterest = 5m,
                    AccruedFees = 2m,
                    InterestRatePerCycle = bank.InterestRatePerCycle,
                    TermCycles = 10,
                    ScheduledInstallment = 10m,
                    Status = LoanStatus.Open,
                    OpenedInCycleId = cycle.Id,
                    CreatedAt = DateTime.UtcNow,
                });
                db.MarginAccounts.AddRange(
                    new MarginAccount
                    {
                        ParticipantId = player.Id,
                        DebitBalance = 50m,
                        AccruedInterest = 3m,
                        InitialMarginRate = 0.50m,
                        MaintenanceMarginRate = 0.25m,
                        Status = MarginAccountStatus.Active,
                    },
                    new MarginAccount
                    {
                        ParticipantId = fund.Id,
                        DebitBalance = 25m,
                        AccruedInterest = 1m,
                        InitialMarginRate = 0.50m,
                        MaintenanceMarginRate = 0.25m,
                        Status = MarginAccountStatus.Active,
                    });

                var trades = new[]
                {
                    new ShareTransaction { BuyerId = player.Id, CompanyId = company.Id, Quantity = 1, Price = price, TotalCost = price, CreatedInCycleId = cycle.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                    new ShareTransaction { BuyerId = player.Id, CompanyId = company.Id, Quantity = 1, Price = price, TotalCost = price, CreatedInCycleId = cycle.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                };
                db.ShareTransactions.AddRange(trades);
                await db.SaveChangesAsync();
                db.SettlementInstructions.AddRange(
                    new SettlementInstruction { ShareTransactionId = trades[0].Id, BuyerId = player.Id, CompanyId = company.Id, Quantity = 1, CashAmount = price, TradeDayNumber = 1, DueDayNumber = 3, Status = SettlementStatus.Pending, CreatedInCycleId = cycle.Id, CreatedAt = DateTime.UtcNow },
                    new SettlementInstruction { ShareTransactionId = trades[1].Id, BuyerId = player.Id, CompanyId = company.Id, Quantity = 1, CashAmount = price, TradeDayNumber = 1, DueDayNumber = 2, Status = SettlementStatus.Pending, CreatedInCycleId = cycle.Id, CreatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();

                expectedPlayerWorth = player.CurrentBalance + (10 * price) - 107m - 53m;
                expectedFundWorth = fund.CurrentBalance - 26m;
            }

            using var playerJson = await client.GetFromJsonAsync<JsonDocument>("/player");
            Assert.Equal(expectedPlayerWorth, playerJson!.RootElement.GetProperty("totalWorth").GetDecimal());
            Assert.Equal(2, playerJson.RootElement.GetProperty("pendingSettlementCount").GetInt32());
            Assert.Equal(2, playerJson.RootElement.GetProperty("nextSettlementDueDayNumber").GetInt32());
            Assert.Equal(expectedFundWorth, playerJson.RootElement.GetProperty("fundTotalWorth").GetDecimal());

            using var detailJson = await client.GetFromJsonAsync<JsonDocument>($"/participants/{playerId}");
            Assert.Equal(expectedPlayerWorth, detailJson!.RootElement.GetProperty("totalWorth").GetDecimal());
            Assert.True(detailJson.RootElement.GetProperty("holdingsValue").GetDecimal() > 0m);
            Assert.Equal(2, detailJson.RootElement.GetProperty("pendingSettlementCount").GetInt32());
            Assert.Equal(2, detailJson.RootElement.GetProperty("nextSettlementDueDayNumber").GetInt32());

            using var participantsJson = await client.GetFromJsonAsync<JsonDocument>("/participants");
            var playerRow = participantsJson!.RootElement.EnumerateArray().Single(row => row.GetProperty("id").GetInt32() == playerId);
            var fundRow = participantsJson.RootElement.EnumerateArray().Single(row => row.GetProperty("id").GetInt32() == fundId);
            Assert.Equal(expectedPlayerWorth, playerRow.GetProperty("totalWorth").GetDecimal());
            Assert.Equal(expectedFundWorth, fundRow.GetProperty("totalWorth").GetDecimal());
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MarketAndCompanyContractsExposeLuldStateAndActiveCount()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databaseDirectory);
        try
        {
            using var configuredFactory = CreateFactory(Path.Combine(databaseDirectory, "app.db"));
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            int companyId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var company = await db.Companies.OrderBy(company => company.Id).FirstAsync();
                var cycle = await db.MarketCycles.OrderByDescending(cycle => cycle.Id).FirstAsync();
                companyId = company.Id;
                var state = await db.PriceBandStates.FirstOrDefaultAsync(candidate => candidate.CompanyId == company.Id);
                if (state is null)
                {
                    state = new PriceBandState { CompanyId = company.Id };
                    db.PriceBandStates.Add(state);
                }
                state.State = LuldState.TradingPause;
                state.LimitDirection = PriceLimitDirection.Upper;
                state.ReferencePrice = 100m;
                state.LowerBandPrice = 95m;
                state.UpperBandPrice = 105m;
                state.LimitStateStartedCycleNumber = 7;
                state.PauseUntilCycleNumber = 157;
                state.UpdatedInCycleId = cycle.Id;
                await db.SaveChangesAsync();
            }

            using var companies = await client.GetFromJsonAsync<JsonDocument>("/companies");
            var companyRow = companies!.RootElement.EnumerateArray().Single(row => row.GetProperty("id").GetInt32() == companyId);
            Assert.Equal("TradingPause", companyRow.GetProperty("luldState").GetString());
            Assert.Equal(95m, companyRow.GetProperty("lowerBandPrice").GetDecimal());
            Assert.Equal(105m, companyRow.GetProperty("upperBandPrice").GetDecimal());

            using var detail = await client.GetFromJsonAsync<JsonDocument>($"/companies/{companyId}");
            Assert.Equal("Upper", detail!.RootElement.GetProperty("limitDirection").GetString());
            Assert.Equal(100m, detail.RootElement.GetProperty("referencePrice").GetDecimal());
            Assert.Equal(7, detail.RootElement.GetProperty("limitStateStartedCycleNumber").GetInt32());
            Assert.Equal(157, detail.RootElement.GetProperty("pauseUntilCycleNumber").GetInt32());

            using var market = await client.GetFromJsonAsync<JsonDocument>("/market");
            Assert.Equal(1, market!.RootElement.GetProperty("luldAffectedCount").GetInt32());
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CompanyContractsExposeExecutableBandAndAllowedOrderRange()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databaseDirectory);
        try
        {
            using var configuredFactory = CreateFactory(Path.Combine(databaseDirectory, "app.db"));
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            int companyId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var company = await db.Companies.OrderBy(company => company.Id).FirstAsync();
                var cycle = await db.MarketCycles.OrderByDescending(cycle => cycle.Id).FirstAsync();
                companyId = company.Id;
                var state = await db.PriceBandStates.FirstOrDefaultAsync(candidate => candidate.CompanyId == company.Id);
                if (state is null)
                {
                    state = new PriceBandState { CompanyId = company.Id };
                    db.PriceBandStates.Add(state);
                }
                state.State = LuldState.Normal;
                state.ReferencePrice = 100m;
                state.LowerBandPrice = 85m;
                state.UpperBandPrice = 110m;
                state.UpdatedInCycleId = cycle.Id;
                await db.SaveChangesAsync();
            }

            using var companies = await client.GetFromJsonAsync<JsonDocument>("/companies");
            var companyRow = companies!.RootElement.EnumerateArray().Single(row => row.GetProperty("id").GetInt32() == companyId);
            Assert.Equal(85m, companyRow.GetProperty("lowerBandPrice").GetDecimal());
            Assert.Equal(110m, companyRow.GetProperty("upperBandPrice").GetDecimal());
            Assert.Equal(75m, companyRow.GetProperty("minimumOrderPrice").GetDecimal());
            Assert.Equal(125m, companyRow.GetProperty("maximumOrderPrice").GetDecimal());

            using var detail = await client.GetFromJsonAsync<JsonDocument>($"/companies/{companyId}");
            Assert.Equal(85m, detail!.RootElement.GetProperty("lowerBandPrice").GetDecimal());
            Assert.Equal(110m, detail.RootElement.GetProperty("upperBandPrice").GetDecimal());
            Assert.Equal(75m, detail.RootElement.GetProperty("minimumOrderPrice").GetDecimal());
            Assert.Equal(125m, detail.RootElement.GetProperty("maximumOrderPrice").GetDecimal());
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AccountingDetailEndpointsPageFilterAndKeepMarginSeparateFromLoans()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databaseDirectory);
        try
        {
            using var configuredFactory = CreateFactory(Path.Combine(databaseDirectory, "app.db"));
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            int companyId;
            int participantId;
            int bankId;
            int cycleNumber;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var company = await db.Companies.OrderBy(company => company.Id).FirstAsync();
                var participant = await db.Participants.OrderBy(participant => participant.Id).FirstAsync();
                var cycle = await db.MarketCycles.OrderByDescending(cycle => cycle.Id).FirstAsync();
                var bank = await db.Banks.OrderBy(bank => bank.Id).FirstAsync();
                companyId = company.Id;
                participantId = participant.Id;
                bankId = bank.Id;
                cycleNumber = cycle.CycleNumber;
                bank.Balance = 1_005m;
                db.Loans.Add(new Loan { BankId = bank.Id, ParticipantId = participant.Id, Principal = 100m, RemainingPrincipal = 100m, InterestRatePerCycle = bank.InterestRatePerCycle, TermCycles = 10, ScheduledInstallment = 10m, Status = LoanStatus.Open, OpenedInCycleId = cycle.Id, CreatedAt = DateTime.UtcNow });
                db.MarginAccounts.Add(new MarginAccount { ParticipantId = participant.Id, DebitBalance = 200m, AccruedInterest = 5m, InitialMarginRate = 0.50m, MaintenanceMarginRate = 0.25m, Status = MarginAccountStatus.Active });
                var corporateCashCreatedAt = DateTime.UtcNow;
                db.CorporateCashTransactions.AddRange(
                    new CorporateCashTransaction { CompanyId = company.Id, Type = CorporateCashTransactionType.PrimaryIssuance, Amount = 10m, CreatedInCycleId = cycle.Id, CreatedAt = corporateCashCreatedAt.AddSeconds(-3) },
                    new CorporateCashTransaction { CompanyId = company.Id, Type = CorporateCashTransactionType.DividendDeclared, Amount = 20m, CreatedInCycleId = cycle.Id, CreatedAt = corporateCashCreatedAt.AddSeconds(-2) },
                    new CorporateCashTransaction { CompanyId = company.Id, Type = CorporateCashTransactionType.ClosureDistribution, Amount = 30m, CreatedInCycleId = cycle.Id, CreatedAt = corporateCashCreatedAt.AddSeconds(-1) },
                    new CorporateCashTransaction { CompanyId = company.Id, Type = CorporateCashTransactionType.OperatingIncome, Amount = 40m, CreatedInCycleId = cycle.Id, CreatedAt = corporateCashCreatedAt });
                var trades = Enumerable.Range(0, 3).Select(_ => new ShareTransaction { BuyerId = participant.Id, CompanyId = company.Id, Quantity = 1, Price = 10m, TotalCost = 10m, CreatedInCycleId = cycle.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }).ToArray();
                db.ShareTransactions.AddRange(trades);
                await db.SaveChangesAsync();
                db.SettlementInstructions.AddRange(
                    new SettlementInstruction { ShareTransactionId = trades[0].Id, BuyerId = participant.Id, CompanyId = company.Id, Quantity = 1, CashAmount = 10m, TradeDayNumber = 1, DueDayNumber = 2, Status = SettlementStatus.Pending, CreatedInCycleId = cycle.Id, CreatedAt = DateTime.UtcNow },
                    new SettlementInstruction { ShareTransactionId = trades[1].Id, BuyerId = participant.Id, CompanyId = company.Id, Quantity = 1, CashAmount = 10m, TradeDayNumber = 1, DueDayNumber = 3, Status = SettlementStatus.Pending, CreatedInCycleId = cycle.Id, CreatedAt = DateTime.UtcNow },
                    new SettlementInstruction { ShareTransactionId = trades[2].Id, BuyerId = participant.Id, CompanyId = company.Id, Quantity = 1, CashAmount = 10m, TradeDayNumber = 1, DueDayNumber = 1, Status = SettlementStatus.Settled, CreatedInCycleId = cycle.Id, CreatedAt = DateTime.UtcNow, SettledAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }

            var corporatePage = await client.GetFromJsonAsync<PagedCorporateCashMovementsDto>($"/companies/{companyId}/corporate-cash-movements?page=1&pageSize=2");
            Assert.Equal(4, corporatePage!.Total);
            Assert.Equal(2, corporatePage.Items.Length);
            Assert.Equal("OperatingIncome", corporatePage.Items[0].Type);
            Assert.Equal(40m, corporatePage.Items[0].Amount);
            Assert.True(corporatePage.Items[0].Amount > 0m);
            Assert.Equal(cycleNumber, corporatePage.Items[0].CreatedInCycleNumber);
            Assert.Equal(1, corporatePage.Page);
            Assert.Equal(2, corporatePage.PageSize);

            var pendingPage = await client.GetFromJsonAsync<PagedSettlementsDto>($"/participants/{participantId}/settlements?status=pending&page=1&pageSize=1");
            Assert.Equal(2, pendingPage!.Total);
            Assert.Single(pendingPage.Items);
            Assert.Equal(2, pendingPage.Items[0].DueDayNumber);
            var settledPage = await client.GetFromJsonAsync<PagedSettlementsDto>($"/participants/{participantId}/settlements?status=settled&page=1&pageSize=10");
            Assert.Equal(1, settledPage!.Total);
            var allPage = await client.GetFromJsonAsync<PagedSettlementsDto>($"/participants/{participantId}/settlements?status=all&page=1&pageSize=10");
            Assert.Equal(3, allPage!.Total);

            using var banks = await client.GetFromJsonAsync<JsonDocument>("/banks");
            var bankRow = banks!.RootElement.EnumerateArray().Single(row => row.GetProperty("id").GetInt32() == bankId);
            Assert.Equal(1_005m, bankRow.GetProperty("balance").GetDecimal());
            Assert.Equal(100m, bankRow.GetProperty("outstandingPrincipal").GetDecimal());
            Assert.Equal(1, bankRow.GetProperty("openLoanCount").GetInt32());
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MarketShareTransactionsArePagedNewestFirst()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databaseDirectory);
        try
        {
            using var configuredFactory = CreateFactory(Path.Combine(databaseDirectory, "app.db"));
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            using (var scope = configuredFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var participants = await db.Participants.OrderBy(participant => participant.Id).Take(2).ToArrayAsync();
                var company = await db.Companies.OrderBy(company => company.Id).FirstAsync();
                var cycle = await db.MarketCycles.OrderByDescending(marketCycle => marketCycle.Id).FirstAsync();
                var createdAt = DateTime.UtcNow;
                var trades = Enumerable.Range(1, 3)
                    .Select(quantity => new ShareTransaction
                    {
                        SellerId = participants[0].Id,
                        BuyerId = participants[1].Id,
                        CompanyId = company.Id,
                        Quantity = quantity,
                        Price = 10m,
                        TotalCost = quantity * 10m,
                        CreatedInCycleId = cycle.Id,
                        CreatedAt = createdAt.AddSeconds(quantity),
                        UpdatedAt = createdAt.AddSeconds(quantity),
                    })
                    .ToArray();
                db.ShareTransactions.AddRange(trades);
                await db.SaveChangesAsync();
                db.SettlementInstructions.AddRange(
                    new SettlementInstruction { ShareTransactionId = trades[0].Id, BuyerId = participants[1].Id, SellerId = participants[0].Id, CompanyId = company.Id, Quantity = 1, CashAmount = 10m, TradeDayNumber = 1, DueDayNumber = 2, Status = SettlementStatus.Pending, CreatedInCycleId = cycle.Id, CreatedAt = createdAt.AddSeconds(1) },
                    new SettlementInstruction { ShareTransactionId = trades[1].Id, BuyerId = participants[1].Id, SellerId = participants[0].Id, CompanyId = company.Id, Quantity = 2, CashAmount = 20m, TradeDayNumber = 1, DueDayNumber = 2, Status = SettlementStatus.Pending, CreatedInCycleId = cycle.Id, CreatedAt = createdAt.AddSeconds(2) },
                    new SettlementInstruction { ShareTransactionId = trades[2].Id, BuyerId = participants[1].Id, SellerId = participants[0].Id, CompanyId = company.Id, Quantity = 3, CashAmount = 30m, TradeDayNumber = 1, DueDayNumber = 2, Status = SettlementStatus.Settled, CreatedInCycleId = cycle.Id, CreatedAt = createdAt.AddSeconds(3), SettledAt = createdAt.AddDays(1) });
                await db.SaveChangesAsync();
            }

            var firstPage = await client.GetFromJsonAsync<PagedShareTransactionsDto>(
                "/transactions/shares/paged?page=1&pageSize=2");
            Assert.Equal(3, firstPage!.Total);
            Assert.Equal(1, firstPage.Page);
            Assert.Equal(2, firstPage.PageSize);
            Assert.Equal([3, 2], firstPage.Items.Select(transaction => transaction.Quantity));
            Assert.Equal("Settled", firstPage.Items[0].SettlementStatus);

            var secondPage = await client.GetFromJsonAsync<PagedShareTransactionsDto>(
                "/transactions/shares/paged?page=2&pageSize=2");
            Assert.Single(secondPage!.Items);
            Assert.Equal(1, secondPage.Items[0].Quantity);

            var boundedPage = await client.GetFromJsonAsync<PagedShareTransactionsDto>(
                "/transactions/shares/paged?page=0&pageSize=999");
            Assert.Equal(1, boundedPage!.Page);
            Assert.Equal(100, boundedPage.PageSize);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PlayerAndParticipantContractsExposeAccountLevelMargin()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databaseDirectory);
        try
        {
            using var configuredFactory = CreateFactory(Path.Combine(databaseDirectory, "app.db"));
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);
            await client.PostAsJsonAsync("/player", new { name = "Ada" });

            using var player = await client.GetFromJsonAsync<JsonDocument>("/player");
            var root = player!.RootElement;
            var margin = root.GetProperty("margin");
            Assert.Equal(0m, margin.GetProperty("debitBalance").GetDecimal());
            Assert.True(margin.GetProperty("buyingPower").GetDecimal() > 0m);

            var participantId = root.GetProperty("id").GetInt32();
            using var detail = await client.GetFromJsonAsync<JsonDocument>($"/participants/{participantId}");
            Assert.Equal(
                margin.GetProperty("accountEquity").GetDecimal(),
                detail!.RootElement.GetProperty("margin").GetProperty("accountEquity").GetDecimal());
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ManualCycleEndpointIsNotExposed()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            using var response = await client.PostAsync("/cycles/tick", null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
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

            var tick = await RunCycleAsync(configuredFactory);
            Assert.Equal(1, tick.FillCount);

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
    public async Task MarketResponseIncludesLogicalTradingClock()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            var market = await client.GetFromJsonAsync<MarketDto>("/market");

            Assert.NotNull(market);
            Assert.Equal(1, market.TradingDayNumber);
            Assert.Equal("Trading", market.TradingSessionState);
            Assert.Equal(1, market.TradingCycleNumber);
            Assert.Equal(209, market.RemainingTradingCycles);
            Assert.Equal(418, market.RemainingPhaseSeconds);
            Assert.Equal(2, market.TradingCycleSeconds);
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

            await RunCycleAsync(configuredFactory);
            Assert.NotEmpty((await client.GetFromJsonAsync<ShareTransactionDto[]>("/transactions/shares"))!);
            var corporateBeforeReset = await client.GetFromJsonAsync<PagedCorporateCashMovementsDto>(
                $"/companies/{companySell.CompanyId}/corporate-cash-movements?page=1&pageSize=10");
            Assert.Empty(corporateBeforeReset!.Items);
            var settlementsBeforeReset = await client.GetFromJsonAsync<PagedSettlementsDto>(
                $"/participants/{buyer.Id}/settlements?status=pending&page=1&pageSize=10");
            Assert.Single(settlementsBeforeReset!.Items);

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
            var corporateAfterReset = await client.GetFromJsonAsync<PagedCorporateCashMovementsDto>(
                $"/companies/{companySell.CompanyId}/corporate-cash-movements?page=1&pageSize=10");
            Assert.Empty(corporateAfterReset!.Items);
            var settlementsAfterReset = await client.GetFromJsonAsync<PagedSettlementsDto>(
                $"/participants/{buyer.Id}/settlements?status=pending&page=1&pageSize=10");
            Assert.Empty(settlementsAfterReset!.Items);
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

            await RunCycleAsync(configuredFactory);

            // The buy fully fills and leaves the open list; the partially filled company sell stays open.
            var openAfterAdvance = await client.GetFromJsonAsync<OrderDto[]>("/orders?status=open");
            var allAfterAdvance = await client.GetFromJsonAsync<OrderDto[]>("/orders");
            Assert.Equal(
                allAfterAdvance!.Count(order => order.Status is "Open" or "PartiallyFilled"),
                openAfterAdvance!.Length);
            Assert.All(openAfterAdvance, order => Assert.True(order.Status is "Open" or "PartiallyFilled"));
            Assert.Contains(allAfterAdvance!, order => order.Type == "Buy" && order.Status == "Filled");
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
            await RunCycleAsync(configuredFactory);

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
    public async Task TradeResponsesExposePendingTPlusOneSettlementAndReconciledActorBalances()
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
            const int quantity = 5;
            var cash = companySell.LimitPrice * quantity;

            await client.PostAsJsonAsync("/orders", new
            {
                participantId = buyer.Id,
                companyId = companySell.CompanyId,
                type = "Buy",
                quantity,
                limitPrice = companySell.LimitPrice,
            });
            await RunCycleAsync(configuredFactory);

            var detail = await client.GetFromJsonAsync<ParticipantDetailDto>($"/participants/{buyer.Id}");
            Assert.Equal(detail!.CurrentBalance - detail.SettledCashBalance, detail.UnsettledCashBalance);
            Assert.Equal(-cash, detail.UnsettledCashBalance);

            var holding = Assert.Single(
                (await client.GetFromJsonAsync<HoldingDto[]>($"/participants/{buyer.Id}/holdings"))!);
            Assert.Equal(quantity, holding.Shares);
            Assert.Equal(0, holding.SettledShares);
            Assert.Equal(quantity, holding.PendingShares);

            var settlements = await client.GetFromJsonAsync<PagedSettlementsDto>(
                $"/participants/{buyer.Id}/settlements?status=pending&page=1&pageSize=10");
            var instruction = Assert.Single(settlements!.Items);
            Assert.Equal("Buy", instruction.Side);
            Assert.Equal(companySell.CompanyId, instruction.CompanyId);
            Assert.Equal(cash, instruction.CashAmount);
            Assert.Equal(1, instruction.TradeDayNumber);
            Assert.Equal(2, instruction.DueDayNumber);

            var trade = Assert.Single(
                (await client.GetFromJsonAsync<ShareTransactionDto[]>($"/participants/{buyer.Id}/share-transactions"))!);
            Assert.Equal(1, trade.TradeDayNumber);
            Assert.Equal(2, trade.DueDayNumber);
            Assert.Equal("Pending", trade.SettlementStatus);
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
            Assert.Contains(afterSeed!, point => point.TradingDayNumber == 1 && point.TradingCycleNumber == 1);

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
            await RunCycleAsync(configuredFactory);

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
            await RunCycleAsync(configuredFactory);

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
            await RunCycleAsync(configuredFactory);

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
    public async Task ParticipantCashMovementsArePagedNewestFirst()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            int participantId;
            int oldestId;
            int middleId;
            int newestId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cycleId = await db.MarketCycles
                    .OrderByDescending(cycle => cycle.Id)
                    .Select(cycle => cycle.Id)
                    .FirstAsync();
                var participant = new Participant
                {
                    Name = "Cash Pager",
                    Type = ParticipantType.Individual,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    InitialBalance = 1_000m,
                    CurrentBalance = 1_000m,
                    SettledCashBalance = 1_000m,
                    IsActive = true,
                };
                db.Participants.Add(participant);
                await db.SaveChangesAsync();
                participantId = participant.Id;

                var createdAt = DateTime.UtcNow;
                var oldest = new MoneyTransaction
                {
                    ParticipantId = participantId,
                    Type = MoneyTransactionType.Credit,
                    Amount = 10m,
                    CreatedInCycleId = cycleId,
                    CreatedAt = createdAt,
                };
                var middle = new MoneyTransaction
                {
                    ParticipantId = participantId,
                    Type = MoneyTransactionType.Credit,
                    Amount = 20m,
                    CreatedInCycleId = cycleId,
                    CreatedAt = createdAt.AddSeconds(1),
                };
                var newest = new MoneyTransaction
                {
                    ParticipantId = participantId,
                    Type = MoneyTransactionType.Credit,
                    Amount = 30m,
                    CreatedInCycleId = cycleId,
                    CreatedAt = createdAt.AddSeconds(2),
                };
                db.MoneyTransactions.AddRange(oldest, middle, newest);
                await db.SaveChangesAsync();
                oldestId = oldest.Id;
                middleId = middle.Id;
                newestId = newest.Id;
            }

            var firstPage = await client.GetFromJsonAsync<PagedMoneyTransactionsDto>(
                $"/participants/{participantId}/money-transactions/paged?page=1&pageSize=2");
            Assert.Equal(3, firstPage!.Total);
            Assert.Equal([newestId, middleId], firstPage.Items.Select(item => item.Id));
            Assert.Equal(1, firstPage.Page);
            Assert.Equal(2, firstPage.PageSize);

            var secondPage = await client.GetFromJsonAsync<PagedMoneyTransactionsDto>(
                $"/participants/{participantId}/money-transactions/paged?page=2&pageSize=2");
            Assert.Equal(oldestId, Assert.Single(secondPage!.Items).Id);
            Assert.Equal(2, secondPage.Page);
            Assert.Equal(2, secondPage.PageSize);
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
            await RunCycleAsync(configuredFactory);

            var detail = await client.GetFromJsonAsync<CompanyDetailDto>($"/companies/{companySell.CompanyId}");

            Assert.Equal(companySell.CompanyId, detail!.Id);
            Assert.False(string.IsNullOrWhiteSpace(detail.Name));
            Assert.True(detail.IssuedSharesCount > 0);
            Assert.Equal(price, detail.CurrentPrice);
            Assert.Equal(quantity, detail.SharesOutstanding);
            Assert.Equal(detail.IssuedSharesCount - quantity, detail.SharesHeldByIssuer);
            Assert.Equal(1, detail.ShareholderCount);
            Assert.Equal(price * detail.IssuedSharesCount, detail.MarketCap);
            Assert.Equal(0m, detail.IssuerCash);

            var movements = await client.GetFromJsonAsync<PagedCorporateCashMovementsDto>(
                $"/companies/{companySell.CompanyId}/corporate-cash-movements?page=1&pageSize=10");
            Assert.Empty(movements!.Items);
            var settlements = await client.GetFromJsonAsync<PagedSettlementsDto>(
                $"/participants/{buyer.Id}/settlements?status=pending&page=1&pageSize=10");
            Assert.Single(settlements!.Items);
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
            await RunCycleAsync(configuredFactory);

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
            await RunCycleAsync(configuredFactory);

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
    public async Task ParticipantsHideClosedFundsWhileClosedFundsEndpointReturnsThem()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            int activeFundId;
            int closedFundId;
            int individualId;
            int cycleNumber;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var cycle = new MarketCycle { CycleNumber = 8, Status = CycleStatus.Completed, StartedAt = now };
                dbContext.MarketCycles.Add(cycle);
                await dbContext.SaveChangesAsync();
                cycleNumber = cycle.CycleNumber;

                var individual = new Participant
                {
                    Name = "Live Trader",
                    Type = ParticipantType.Individual,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    InitialBalance = 100_000m,
                    CurrentBalance = 100_000m,
                    IsActive = true,
                };
                var activeFund = new Participant
                {
                    Name = "Active Fund",
                    Type = ParticipantType.CollectiveFund,
                    Temperament = Temperament.Aggressive,
                    RiskProfile = RiskProfile.High,
                    CurrentBalance = 500_000m,
                    IsActive = true,
                };
                var closedFund = new Participant
                {
                    Name = "Closed Fund",
                    Type = ParticipantType.CollectiveFund,
                    Temperament = Temperament.Conservative,
                    RiskProfile = RiskProfile.Low,
                    CurrentBalance = 0m,
                    IsActive = false,
                };
                dbContext.Participants.AddRange(individual, activeFund, closedFund);
                await dbContext.SaveChangesAsync();
                individualId = individual.Id;
                activeFundId = activeFund.Id;
                closedFundId = closedFund.Id;

                dbContext.CollectiveFunds.AddRange(
                    new CollectiveFund
                    {
                        ParticipantId = activeFund.Id,
                        FoundedByParticipantId = individual.Id,
                        Status = CollectiveFundStatus.Active,
                        CreatedInCycleId = cycle.Id,
                        CreatedAt = now,
                    },
                    new CollectiveFund
                    {
                        ParticipantId = closedFund.Id,
                        FoundedByParticipantId = individual.Id,
                        Status = CollectiveFundStatus.Closed,
                        CreatedInCycleId = cycle.Id,
                        CreatedAt = now,
                        ClosedAt = now,
                        PeakNetWorth = 750_000m,
                    });
                await dbContext.SaveChangesAsync();
            }

            var participants = await client.GetFromJsonAsync<ParticipantDto[]>("/participants");
            var ids = participants!.Select(participant => participant.Id).ToHashSet();
            Assert.Contains(individualId, ids);
            Assert.Contains(activeFundId, ids);
            Assert.DoesNotContain(closedFundId, ids);

            var closed = await client.GetFromJsonAsync<PagedClosedFundsDto>("/collective-funds/closed");
            Assert.Equal(1, closed!.Total);
            var fund = Assert.Single(closed.Items);
            Assert.Equal(closedFundId, fund.ParticipantId);
            Assert.Equal("Closed Fund", fund.Name);
            Assert.Equal("Conservative", fund.Temperament);
            Assert.Equal("Low", fund.RiskProfile);
            Assert.Equal(750_000m, fund.PeakNetWorth);
            Assert.Equal(cycleNumber, fund.CreatedInCycleNumber);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PagedParticipantsLabelFundMembersAndFilterByStatus()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            int individualId;
            int fundParticipantId;
            int memberId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var cycle = new MarketCycle { CycleNumber = 4, Status = CycleStatus.Completed, StartedAt = now };
                dbContext.MarketCycles.Add(cycle);
                await dbContext.SaveChangesAsync();

                var individual = new Participant
                {
                    Name = "Solo Trader",
                    Type = ParticipantType.Individual,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    InitialBalance = 100_000m,
                    CurrentBalance = 100_000m,
                    IsActive = true,
                };
                var fundParticipant = new Participant
                {
                    Name = "Growth Fund",
                    Type = ParticipantType.CollectiveFund,
                    Temperament = Temperament.Aggressive,
                    RiskProfile = RiskProfile.High,
                    CurrentBalance = 500_000m,
                    IsActive = true,
                };
                var member = new Participant
                {
                    Name = "Pooled Trader",
                    Type = ParticipantType.Individual,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    InitialBalance = 100_000m,
                    CurrentBalance = 10_000m,
                    IsActive = true,
                };
                dbContext.Participants.AddRange(individual, fundParticipant, member);
                await dbContext.SaveChangesAsync();
                individualId = individual.Id;
                fundParticipantId = fundParticipant.Id;
                memberId = member.Id;

                var fund = new CollectiveFund
                {
                    ParticipantId = fundParticipant.Id,
                    FoundedByParticipantId = individual.Id,
                    Status = CollectiveFundStatus.Active,
                    CreatedInCycleId = cycle.Id,
                    CreatedAt = now,
                };
                dbContext.CollectiveFunds.Add(fund);
                await dbContext.SaveChangesAsync();

                dbContext.CollectiveFundParticipants.Add(new CollectiveFundParticipant
                {
                    CollectiveFundId = fund.Id,
                    ParticipantId = member.Id,
                    JoinedAt = now,
                    JoinedInCycleId = cycle.Id,
                    DepositAmount = 90_000m,
                });
                await dbContext.SaveChangesAsync();
            }

            // The default roster keeps the member, labeled with the fund it trades through.
            var all = await client.GetFromJsonAsync<PagedParticipantsDto>("/participants/paged?pageSize=100");
            var labelledMember = Assert.Single(all!.Items, item => item.Id == memberId);
            Assert.Equal(fundParticipantId, labelledMember.MemberOfCollectiveFundId);
            Assert.Equal("Growth Fund", labelledMember.MemberOfCollectiveFundName);
            var soloRow = Assert.Single(all.Items, item => item.Id == individualId);
            Assert.Null(soloRow.MemberOfCollectiveFundId);

            var activeOnly = await client.GetFromJsonAsync<PagedParticipantsDto>("/participants/paged?pageSize=100&status=active");
            Assert.DoesNotContain(activeOnly!.Items, item => item.Id == memberId);
            Assert.Contains(activeOnly.Items, item => item.Id == individualId);

            var inFundOnly = await client.GetFromJsonAsync<PagedParticipantsDto>("/participants/paged?pageSize=100&status=in-fund");
            var onlyMember = Assert.Single(inFundOnly!.Items);
            Assert.Equal(memberId, onlyMember.Id);

            // The unpaged array still drops members so dashboard aggregates do not double-count pooled cash.
            var unpaged = await client.GetFromJsonAsync<ParticipantDto[]>("/participants");
            Assert.DoesNotContain(unpaged!, item => item.Id == memberId);
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
                dbContext.CompanyRatings.Add(new CompanyRating
                {
                    CompanyId = companyId,
                    AuditorId = auditorId,
                    Rating = CompanyRiskRating.ExtraRaisedExpectations,
                    ImpactPercent = 15m,
                    CreatedInCycleId = cycleId,
                    CreatedAt = DateTime.UtcNow,
                });
                await dbContext.SaveChangesAsync();
            }

            var detail = await client.GetFromJsonAsync<CompanyDetailDto>($"/companies/{companyId}");
            Assert.Equal("ExtraRaisedExpectations", detail!.CurrentRating);
            Assert.Equal("Extra", detail.PreviousRating);

            var ratings = await client.GetFromJsonAsync<CompanyRatingDto[]>($"/companies/{companyId}/ratings");
            Assert.Equal(3, ratings!.Length);
            Assert.Equal("ExtraRaisedExpectations", ratings[0].Rating);
            Assert.Equal(15m, ratings[0].ImpactPercent);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(CompanyRiskRating.RaisedExpectations)]
    [InlineData(CompanyRiskRating.ExtraRaisedExpectations)]
    public async Task PositiveAuditorRatingsDoNotPutAHoldingInHighRiskAttention(CompanyRiskRating rating)
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            int participantId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                participantId = await dbContext.Participants.Select(participant => participant.Id).FirstAsync();
                var companyId = await dbContext.Companies.Select(company => company.Id).FirstAsync();
                dbContext.Holdings.Add(new Holding
                {
                    ParticipantId = participantId,
                    CompanyId = companyId,
                    Quantity = 1,
                    SettledQuantity = 1,
                    AverageCost = 100m,
                });
                var auditorId = await dbContext.Auditors.Select(auditor => auditor.Id).FirstAsync();
                var cycleId = (await dbContext.Markets.FirstAsync()).CurrentCycleId!.Value;
                dbContext.CompanyRatings.Add(new CompanyRating
                {
                    CompanyId = companyId,
                    AuditorId = auditorId,
                    Rating = rating,
                    ImpactPercent = 10m,
                    CreatedInCycleId = cycleId,
                    CreatedAt = DateTime.UtcNow,
                });
                await dbContext.SaveChangesAsync();
            }

            using var response = await client.GetFromJsonAsync<JsonDocument>(
                $"/participants/{participantId}/companies-attention");
            Assert.Empty(response!.RootElement.EnumerateArray());
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

    [Fact]
    public async Task NewsEndpointReportsThePublishedCycleNumber()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);

            int cycleNumber;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cycleId = (await dbContext.Markets.FirstAsync()).CurrentCycleId!.Value;
                cycleNumber = await dbContext.MarketCycles
                    .Where(cycle => cycle.Id == cycleId)
                    .Select(cycle => cycle.CycleNumber)
                    .FirstAsync();
                dbContext.NewsPosts.Add(new NewsPost
                {
                    Title = "Test headline",
                    Content = "Body",
                    PublishedInCycleId = cycleId,
                    PublishedAt = DateTime.UtcNow,
                    Scope = NewsImpactScope.None,
                });
                await dbContext.SaveChangesAsync();
            }

            var news = await client.GetFromJsonAsync<NewsDto[]>("/news");
            var post = news!.Single(item => item.Title == "Test headline");
            Assert.Equal(cycleNumber, post.PublishedInCycleNumber);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FundMembershipHistoryEndpointServesBothMemberAndFundPerspectives()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            int fundParticipantId;
            int memberId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var cycle = new MarketCycle { CycleNumber = 12, Status = CycleStatus.Running, StartedAt = now };
                dbContext.MarketCycles.Add(cycle);
                await dbContext.SaveChangesAsync();

                var fundParticipant = new Participant
                {
                    Name = "History Fund",
                    Type = ParticipantType.CollectiveFund,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    CurrentBalance = 0m,
                    IsActive = true,
                };
                var member = new Participant
                {
                    Name = "History Member",
                    Type = ParticipantType.Individual,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    CurrentBalance = 10_000m,
                    IsActive = true,
                };
                dbContext.Participants.AddRange(fundParticipant, member);
                await dbContext.SaveChangesAsync();
                fundParticipantId = fundParticipant.Id;
                memberId = member.Id;

                var fund = new CollectiveFund
                {
                    ParticipantId = fundParticipant.Id,
                    FoundedByParticipantId = member.Id,
                    Status = CollectiveFundStatus.Active,
                    CreatedInCycleId = cycle.Id,
                    CreatedAt = now,
                };
                dbContext.CollectiveFunds.Add(fund);
                await dbContext.SaveChangesAsync();

                dbContext.CollectiveFundMembershipEvents.AddRange(
                    new CollectiveFundMembershipEvent
                    {
                        CollectiveFundId = fund.Id,
                        FundParticipantId = fundParticipant.Id,
                        ParticipantId = member.Id,
                        Type = CollectiveFundMembershipEventType.Joined,
                        Amount = 9_000m,
                        CreatedInCycleId = cycle.Id,
                        CreatedAt = now,
                    },
                    new CollectiveFundMembershipEvent
                    {
                        CollectiveFundId = fund.Id,
                        FundParticipantId = fundParticipant.Id,
                        ParticipantId = member.Id,
                        Type = CollectiveFundMembershipEventType.Left,
                        Amount = 9_500m,
                        CreatedInCycleId = cycle.Id,
                        CreatedAt = now,
                    });
                await dbContext.SaveChangesAsync();
            }

            // The fund's page reads newest-first and names the member as the counterparty.
            var fromFund = await client.GetFromJsonAsync<PagedFundMembershipEventsDto>(
                $"/participants/{fundParticipantId}/fund-membership-history");
            Assert.Equal(2, fromFund!.Total);
            Assert.Equal("Left", fromFund.Items[0].Type);
            Assert.Equal(9_500m, fromFund.Items[0].Amount);
            Assert.Equal("History Member", fromFund.Items[0].MemberName);
            Assert.Equal("History Fund", fromFund.Items[0].FundName);
            Assert.Equal(12, fromFund.Items[0].CreatedInCycleNumber);

            // The member's page returns the same two events from the other side.
            var fromMember = await client.GetFromJsonAsync<PagedFundMembershipEventsDto>(
                $"/participants/{memberId}/fund-membership-history");
            Assert.Equal(2, fromMember!.Total);
            Assert.Equal("Joined", fromMember.Items[1].Type);
            Assert.Equal(9_000m, fromMember.Items[1].Amount);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FundDetailReportsMemberLeaveCountdownInTradingDaysAndFounder()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            int fundParticipantId;
            int founderId;
            int freshId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var founderDay = new TradingDay { DayNumber = 10, State = TradingSessionState.Trading };
                var freshDay = new TradingDay { DayNumber = 14, State = TradingSessionState.Trading };
                var currentDay = new TradingDay { DayNumber = 20, State = TradingSessionState.Trading };
                dbContext.TradingDays.AddRange(founderDay, freshDay, currentDay);
                await dbContext.SaveChangesAsync();
                var founderCycle = new MarketCycle
                {
                    CycleNumber = 10,
                    TradingDayId = founderDay.Id,
                    TradingCycleNumber = 1,
                    Status = CycleStatus.Completed,
                };
                var freshCycle = new MarketCycle
                {
                    CycleNumber = 14,
                    TradingDayId = freshDay.Id,
                    TradingCycleNumber = 1,
                    Status = CycleStatus.Completed,
                };
                var currentCycle = new MarketCycle
                {
                    CycleNumber = 20,
                    TradingDayId = currentDay.Id,
                    TradingCycleNumber = 1,
                    Status = CycleStatus.Running,
                    StartedAt = now,
                };
                dbContext.MarketCycles.AddRange(founderCycle, freshCycle, currentCycle);
                await dbContext.SaveChangesAsync();
                founderDay.OpenedInCycleId = founderCycle.Id;
                freshDay.OpenedInCycleId = freshCycle.Id;
                currentDay.OpenedInCycleId = currentCycle.Id;
                dbContext.Markets.Add(new Market
                {
                    Name = "Countdown Market",
                    Status = MarketStatus.Running,
                    CurrentCycleId = currentCycle.Id,
                    CurrentTradingDayId = currentDay.Id,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await dbContext.SaveChangesAsync();

                var fundParticipant = new Participant
                {
                    Name = "Countdown Fund",
                    Type = ParticipantType.CollectiveFund,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    CurrentBalance = 0m,
                    IsActive = true,
                };
                var founder = new Participant
                {
                    Name = "Founder",
                    Type = ParticipantType.Individual,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    CurrentBalance = 1_000m,
                    IsActive = true,
                };
                var fresh = new Participant
                {
                    Name = "Fresh Joiner",
                    Type = ParticipantType.Individual,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    CurrentBalance = 1_000m,
                    IsActive = true,
                };
                dbContext.Participants.AddRange(fundParticipant, founder, fresh);
                await dbContext.SaveChangesAsync();
                fundParticipantId = fundParticipant.Id;
                founderId = founder.Id;
                freshId = fresh.Id;

                var fund = new CollectiveFund
                {
                    ParticipantId = fundParticipant.Id,
                    FoundedByParticipantId = founder.Id,
                    Status = CollectiveFundStatus.Active,
                    CreatedInCycleId = founderCycle.Id,
                    CreatedAt = now,
                };
                dbContext.CollectiveFunds.Add(fund);
                await dbContext.SaveChangesAsync();

                dbContext.CollectiveFundParticipants.AddRange(
                    new CollectiveFundParticipant
                    {
                        CollectiveFundId = fund.Id,
                        ParticipantId = founder.Id,
                        JoinedAt = now,
                        JoinedInCycleId = founderCycle.Id,
                        DepositAmount = 9_000m,
                    },
                    new CollectiveFundParticipant
                    {
                        CollectiveFundId = fund.Id,
                        ParticipantId = fresh.Id,
                        JoinedAt = now,
                        JoinedInCycleId = freshCycle.Id,
                        DepositAmount = 900m,
                    });
                await dbContext.SaveChangesAsync();
            }

            var detail = await client.GetFromJsonAsync<FundDetailDto>($"/participants/{fundParticipantId}");
            Assert.NotNull(detail);

            // The founder never switches away, so its row is flagged and its countdown is not shown to users.
            var founderMember = detail!.CollectiveFundMembers.Single(member => member.ParticipantId == founderId);
            Assert.True(founderMember.IsFounder);
            Assert.Equal(3, founderMember.LeaveCountdownTradingDays);

            // A fresh joiner is one trading day short of the configured seven-day tenure.
            var freshMember = detail.CollectiveFundMembers.Single(member => member.ParticipantId == freshId);
            Assert.False(freshMember.IsFounder);
            Assert.Equal(-1, freshMember.LeaveCountdownTradingDays);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task HoldingsAndPortfolioPagedEndpointsPageSortAndReportCostBasis()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            int playerId;
            decimal expectedCostBasis = 0m;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var cycle = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
                db.MarketCycles.Add(cycle);
                var alpha = new Industry { Name = "Alpha" };
                var beta = new Industry { Name = "Beta" };
                db.Industries.AddRange(alpha, beta);
                await db.SaveChangesAsync();

                var player = new Participant
                {
                    Name = "Holder",
                    Type = ParticipantType.Individual,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    CurrentBalance = 0m,
                    IsActive = true,
                };
                db.Participants.Add(player);
                await db.SaveChangesAsync();
                playerId = player.Id;

                // Twelve companies (>page size) split evenly across two industries. Quantity ascends with the index
                // while the name ascends too, so quantity-desc and name-asc orderings resolve to opposite ends.
                for (var i = 0; i < 12; i++)
                {
                    var company = new Company
                    {
                        Name = $"Company {(char)('A' + i)}",
                        IndustryId = i < 6 ? alpha.Id : beta.Id,
                        IssuedSharesCount = 1_000,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    db.Companies.Add(company);
                    await db.SaveChangesAsync();

                    var price = 10m + i;
                    var averageCost = 9m + i;
                    var quantity = (i + 1) * 5;
                    db.PriceSnapshots.Add(new PriceSnapshot { CompanyId = company.Id, Price = price, CreatedInCycleId = cycle.Id, CreatedAt = now });
                    db.Holdings.Add(new Holding
                    {
                        ParticipantId = player.Id,
                        CompanyId = company.Id,
                        Quantity = quantity,
                        SettledQuantity = quantity,
                        AverageCost = averageCost,
                    });
                    expectedCostBasis += quantity * averageCost;
                }

                await db.SaveChangesAsync();
            }

            // Default sort is quantity descending; the last-indexed company holds the most shares.
            var firstPage = await client.GetFromJsonAsync<PagedHoldingsDto>(
                $"/participants/{playerId}/holdings/paged?page=1&pageSize=10");
            Assert.Equal(12, firstPage!.Total);
            Assert.Equal(10, firstPage.Items.Length);
            Assert.Equal(60, firstPage.Items[0].Shares);
            Assert.Equal("Company L", firstPage.Items[0].CompanyName);

            var secondPage = await client.GetFromJsonAsync<PagedHoldingsDto>(
                $"/participants/{playerId}/holdings/paged?page=2&pageSize=10");
            Assert.Equal(2, secondPage!.Items.Length);

            var byName = await client.GetFromJsonAsync<PagedHoldingsDto>(
                $"/participants/{playerId}/holdings/paged?sort=company&sortDir=asc&pageSize=10");
            Assert.Equal("Company A", byName!.Items[0].CompanyName);
            Assert.Equal(5, byName.Items[0].Shares);

            // Portfolio-by-industry rolls the twelve holdings into two industry buckets.
            var industries = await client.GetFromJsonAsync<PagedIndustryHoldingsDto>(
                $"/participants/{playerId}/portfolio-by-industry/paged?pageSize=10");
            Assert.Equal(2, industries!.Total);
            Assert.Equal(6, industries.Items[0].CompanyCount);
            Assert.Equal("Beta", industries.Items[0].IndustryName); // higher-value bucket leads by default
            Assert.True(Math.Abs(industries.Items.Sum(item => item.Pct) - 1.0) < 1e-6);

            var industriesByName = await client.GetFromJsonAsync<PagedIndustryHoldingsDto>(
                $"/participants/{playerId}/portfolio-by-industry/paged?sort=industry&sortDir=asc&pageSize=10");
            Assert.Equal("Alpha", industriesByName!.Items[0].IndustryName);

            // The detail response carries the whole-portfolio cost basis so the client need not load every holding.
            using var detail = await client.GetFromJsonAsync<JsonDocument>($"/participants/{playerId}");
            Assert.Equal(expectedCostBasis, detail!.RootElement.GetProperty("holdingsCostBasis").GetDecimal());
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InvestmentsPagedEndpointPagesAndSorts()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            int investorId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var cycle = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
                db.MarketCycles.Add(cycle);
                var industry = new Industry { Name = "Tech" };
                db.Industries.Add(industry);
                await db.SaveChangesAsync();

                var company = new Company { Name = "Acme", IndustryId = industry.Id, IssuedSharesCount = 1_000, CreatedAt = now, UpdatedAt = now };
                var investor = new Participant
                {
                    Name = "Investor",
                    Type = ParticipantType.Individual,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    CurrentBalance = 0m,
                    IsActive = true,
                };
                db.Companies.Add(company);
                db.Participants.Add(investor);
                await db.SaveChangesAsync();
                investorId = investor.Id;

                for (var i = 0; i < 12; i++)
                {
                    db.CompanyInvestments.Add(new CompanyInvestment
                    {
                        CompanyId = company.Id,
                        InvestorParticipantId = investor.Id,
                        DealValue = (i + 1) * 1_000m,
                        SharesIssued = (i + 1) * 10,
                        SharesBeforeDeal = 100,
                        CapitalizationBeforeDeal = 1_000m,
                        FinalCapitalization = 2_000m,
                        InvestorSharePercent = i,
                        TradingDayNumber = i,
                        CreatedInCycleId = cycle.Id,
                        CreatedAt = now,
                    });
                }

                await db.SaveChangesAsync();
            }

            // Default order is newest first (highest id), which is the last-created, highest deal value.
            var firstPage = await client.GetFromJsonAsync<PagedInvestmentsDto>(
                $"/participants/{investorId}/investments/paged?page=1&pageSize=10");
            Assert.Equal(12, firstPage!.Total);
            Assert.Equal(10, firstPage.Items.Length);
            Assert.Equal(12_000m, firstPage.Items[0].DealValue);

            var secondPage = await client.GetFromJsonAsync<PagedInvestmentsDto>(
                $"/participants/{investorId}/investments/paged?page=2&pageSize=10");
            Assert.Equal(2, secondPage!.Items.Length);

            var byDealAsc = await client.GetFromJsonAsync<PagedInvestmentsDto>(
                $"/participants/{investorId}/investments/paged?sort=dealValue&sortDir=asc&pageSize=10");
            Assert.Equal(1_000m, byDealAsc!.Items[0].DealValue);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FundMembersPagedDefaultsToLargestDepositAndPages()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            int fundParticipantId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var cycle = new MarketCycle { CycleNumber = 3, Status = CycleStatus.Running, StartedAt = now };
                db.MarketCycles.Add(cycle);
                await db.SaveChangesAsync();

                var fundParticipant = new Participant
                {
                    Name = "Deposit Fund",
                    Type = ParticipantType.CollectiveFund,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    CurrentBalance = 0m,
                    IsActive = true,
                };
                var mia = NewMember("Mia");
                var zoe = NewMember("Zoe");
                var ana = NewMember("Ana");
                db.Participants.AddRange(fundParticipant, mia, zoe, ana);
                await db.SaveChangesAsync();
                fundParticipantId = fundParticipant.Id;

                var fund = new CollectiveFund
                {
                    ParticipantId = fundParticipant.Id,
                    FoundedByParticipantId = mia.Id,
                    Status = CollectiveFundStatus.Active,
                    CreatedInCycleId = cycle.Id,
                    CreatedAt = now,
                };
                db.CollectiveFunds.Add(fund);
                await db.SaveChangesAsync();

                db.CollectiveFundParticipants.AddRange(
                    new CollectiveFundParticipant { CollectiveFundId = fund.Id, ParticipantId = mia.Id, JoinedAt = now, JoinedInCycleId = cycle.Id, DepositAmount = 5_000m },
                    new CollectiveFundParticipant { CollectiveFundId = fund.Id, ParticipantId = zoe.Id, JoinedAt = now, JoinedInCycleId = cycle.Id, DepositAmount = 2_000m },
                    new CollectiveFundParticipant { CollectiveFundId = fund.Id, ParticipantId = ana.Id, JoinedAt = now, JoinedInCycleId = cycle.Id, DepositAmount = 900m });
                await db.SaveChangesAsync();

                Participant NewMember(string name) => new()
                {
                    Name = name,
                    Type = ParticipantType.Individual,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    CurrentBalance = 1_000m,
                    IsActive = true,
                };
            }

            // Largest depositor leads by default; paging keeps that order.
            var firstPage = await client.GetFromJsonAsync<PagedFundMembersDto>(
                $"/participants/{fundParticipantId}/fund-members/paged?page=1&pageSize=2");
            Assert.Equal(3, firstPage!.Total);
            Assert.Equal(2, firstPage.Items.Length);
            Assert.Equal(5_000m, firstPage.Items[0].Deposit);
            Assert.Equal(2_000m, firstPage.Items[1].Deposit);

            var secondPage = await client.GetFromJsonAsync<PagedFundMembersDto>(
                $"/participants/{fundParticipantId}/fund-members/paged?page=2&pageSize=2");
            Assert.Single(secondPage!.Items);
            Assert.Equal(900m, secondPage.Items[0].Deposit);

            var byName = await client.GetFromJsonAsync<PagedFundMembersDto>(
                $"/participants/{fundParticipantId}/fund-members/paged?sort=member&sortDir=asc&pageSize=10");
            Assert.Equal("Ana", byName!.Items[0].Name);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoanEndpointsExposeReconciledArrearsBreakdown()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            int participantId;
            int transactionId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var cycle = new MarketCycle { CycleNumber = 12, Status = CycleStatus.Running, StartedAt = now };
                var bank = new Bank { Name = "National bank", InterestRatePerCycle = 0.001m };
                var borrower = new Participant
                {
                    Name = "Borrower",
                    Type = ParticipantType.Individual,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    InitialBalance = 20_000m,
                    CurrentBalance = 20_000m,
                    IsActive = true,
                };
                dbContext.AddRange(cycle, bank, borrower);
                await dbContext.SaveChangesAsync();
                participantId = borrower.Id;

                dbContext.Markets.Add(new Market
                {
                    Name = "Demo",
                    Status = MarketStatus.Running,
                    CurrentCycleId = cycle.Id,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                var loan = new Loan
                {
                    BankId = bank.Id,
                    ParticipantId = borrower.Id,
                    Principal = 10_000m,
                    RemainingPrincipal = 10_000m,
                    InterestRatePerCycle = 0.001m,
                    TermCycles = 100,
                    ScheduledInstallment = 100m,
                    PastDuePrincipal = 100m,
                    PastDueInterest = 10m,
                    AccruedFees = 11m,
                    Status = LoanStatus.Open,
                    OpenedInCycleId = cycle.Id,
                    CreatedAt = now,
                };
                dbContext.Loans.Add(loan);
                await dbContext.SaveChangesAsync();

                var transaction = new MoneyTransaction
                {
                    ParticipantId = borrower.Id,
                    Type = MoneyTransactionType.LoanFine,
                    Amount = 11m,
                    RelatedLoanId = loan.Id,
                    CreatedInCycleId = cycle.Id,
                    CreatedAt = now,
                };
                dbContext.MoneyTransactions.Add(transaction);
                await dbContext.SaveChangesAsync();
                transactionId = transaction.Id;
            }

            var loans = await client.GetFromJsonAsync<LoanDto[]>($"/participants/{participantId}/loans");
            var loanResponse = Assert.Single(loans!);
            Assert.Equal(100m, loanResponse.PastDuePrincipal);
            Assert.Equal(10m, loanResponse.PastDueInterest);
            Assert.Equal(11m, loanResponse.AccruedFees);
            Assert.Equal(10_021m, loanResponse.TotalLiability);

            var detail = await client.GetFromJsonAsync<MoneyTransactionDetailDto>(
                $"/participants/{participantId}/money-transactions/{transactionId}");
            Assert.NotNull(detail!.Loan);
            Assert.Equal(100m, detail.Loan!.PastDuePrincipal);
            Assert.Equal(10m, detail.Loan.PastDueInterest);
            Assert.Equal(11m, detail.Loan.AccruedFees);
            Assert.Equal(10_021m, detail.Loan.TotalLiability);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MoneyTransactionDetailReturnsTheDividendCompanyBreakdown()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            int participantId;
            int transactionId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var cycle = new MarketCycle { CycleNumber = 12, Status = CycleStatus.Completed, StartedAt = now };
                dbContext.MarketCycles.Add(cycle);
                var industry = new Industry { Name = "Tech" };
                dbContext.Industries.Add(industry);
                await dbContext.SaveChangesAsync();

                var acme = new Company { Name = "Acme", IndustryId = industry.Id, IssuedSharesCount = 1000, CreatedAt = now, UpdatedAt = now };
                var globex = new Company { Name = "Globex", IndustryId = industry.Id, IssuedSharesCount = 1000, CreatedAt = now, UpdatedAt = now };
                dbContext.Companies.AddRange(acme, globex);
                var holder = new Participant
                {
                    Name = "Holder",
                    Type = ParticipantType.Individual,
                    Temperament = Temperament.Balanced,
                    RiskProfile = RiskProfile.Medium,
                    InitialBalance = 0m,
                    CurrentBalance = 30m,
                    IsActive = true,
                };
                dbContext.Participants.Add(holder);
                await dbContext.SaveChangesAsync();
                participantId = holder.Id;

                var dividend = new MoneyTransaction
                {
                    ParticipantId = holder.Id,
                    Type = MoneyTransactionType.Dividend,
                    Amount = 30m,
                    CreatedInCycleId = cycle.Id,
                    CreatedAt = now,
                };
                dbContext.MoneyTransactions.Add(dividend);
                dbContext.DividendPayouts.Add(new DividendPayout { MoneyTransaction = dividend, CompanyId = acme.Id, Amount = 20m, CreatedInCycleId = cycle.Id, CreatedAt = now });
                dbContext.DividendPayouts.Add(new DividendPayout { MoneyTransaction = dividend, CompanyId = globex.Id, Amount = 10m, CreatedInCycleId = cycle.Id, CreatedAt = now });
                await dbContext.SaveChangesAsync();
                transactionId = dividend.Id;
            }

            var detail = await client.GetFromJsonAsync<MoneyTransactionDetailDto>(
                $"/participants/{participantId}/money-transactions/{transactionId}");

            Assert.Equal("Dividend", detail!.Type);
            Assert.Equal(30m, detail.Amount);
            Assert.Equal(12, detail.CycleNumber);
            Assert.Null(detail.Order);
            Assert.Null(detail.Loan);
            Assert.NotNull(detail.DividendBreakdown);
            Assert.Equal(2, detail.DividendBreakdown!.Length);
            // Ordered by amount descending, so the larger payer leads and names resolve.
            Assert.Equal("Acme", detail.DividendBreakdown[0].CompanyName);
            Assert.Equal(20m, detail.DividendBreakdown[0].Amount);
            Assert.Equal("Globex", detail.DividendBreakdown[1].CompanyName);
            Assert.Equal(30m, detail.DividendBreakdown.Sum(line => line.Amount));
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MoneyTransactionDetailReturnsNotFoundForAnotherParticipant()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            int transactionId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var cycle = new MarketCycle { CycleNumber = 3, Status = CycleStatus.Completed, StartedAt = now };
                dbContext.MarketCycles.Add(cycle);
                await dbContext.SaveChangesAsync();

                var transaction = new MoneyTransaction
                {
                    ParticipantId = 100,
                    Type = MoneyTransactionType.Credit,
                    Amount = 5m,
                    CreatedInCycleId = cycle.Id,
                    CreatedAt = now,
                };
                dbContext.MoneyTransactions.Add(transaction);
                await dbContext.SaveChangesAsync();
                transactionId = transaction.Id;
            }

            // The transaction belongs to participant 100; requesting it under a different id must not leak it.
            using var response = await client.GetAsync($"/participants/200/money-transactions/{transactionId}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MoneyTransactionDetailReturnsTheSourceParticipantAndDescription()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            int participantId;
            int sourceId;
            int transactionId;
            using (var scope = configuredFactory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var cycle = new MarketCycle { CycleNumber = 7, Status = CycleStatus.Completed, StartedAt = now };
                dbContext.MarketCycles.Add(cycle);

                var recipient = new Participant { Name = "Recipient", Type = ParticipantType.Individual, IsActive = true };
                var source = new Participant { Name = "Benefactor", Type = ParticipantType.Individual, IsActive = true };
                dbContext.Participants.Add(recipient);
                dbContext.Participants.Add(source);
                await dbContext.SaveChangesAsync();
                participantId = recipient.Id;
                sourceId = source.Id;

                var transaction = new MoneyTransaction
                {
                    ParticipantId = recipient.Id,
                    Type = MoneyTransactionType.Credit,
                    Amount = 250m,
                    FromWhomId = source.Id,
                    Description = "Bonus payout",
                    CreatedInCycleId = cycle.Id,
                    CreatedAt = now,
                };
                dbContext.MoneyTransactions.Add(transaction);
                await dbContext.SaveChangesAsync();
                transactionId = transaction.Id;
            }

            var detail = await client.GetFromJsonAsync<MoneyTransactionDetailDto>(
                $"/participants/{participantId}/money-transactions/{transactionId}");
            Assert.Equal(sourceId, detail!.FromWhomId);
            Assert.Equal("Benefactor", detail.FromWhomName);
            Assert.Equal("Bonus payout", detail.Description);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AdvertiseEndpointsQuoteThenChargeTheFundAndLiftPopularity()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            using var configuredFactory = CreateFactory(databasePath);
            using var client = configuredFactory.CreateClient();

            await client.PostAsync("/market/seed", null);
            await client.PostAsJsonAsync("/player", new { name = "Ada" });
            var withFund = await (await client.PostAsJsonAsync("/player/fund", new { seedAmount = 5_000m, name = (string?)null }))
                .Content.ReadFromJsonAsync<PlayerDto>();
            var fundId = withFund!.FundParticipantId!.Value;

            // A fresh fund has no growth history, so the quote is the dear 10% of its 5,000 worth.
            var quote = await client.GetFromJsonAsync<FundAdvertiseQuoteDto>($"/funds/{fundId}/advertise-quote");
            Assert.Equal(0, quote!.PopularityIndex);
            Assert.Equal(0.10m, quote.Fraction);
            Assert.Equal(5_000m, quote.FundWorth);
            Assert.Equal(500m, quote.Price);

            var afterAd = await (await client.PostAsync($"/funds/{fundId}/advertise", null))
                .Content.ReadFromJsonAsync<PlayerDto>();
            Assert.Equal(1, afterAd!.FundPopularityIndex);
            Assert.Equal(4_500m, afterAd.FundCurrentBalance);
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MarketResetClearsAiTraderTablesAndPreservesGameSettings()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databaseDirectory);
        try
        {
            using var configuredFactory = CreateFactory(Path.Combine(databaseDirectory, "app.db"));
            using var client = configuredFactory.CreateClient();
            await client.PostAsync("/market/seed", null);
            using (var settingsResponse = await client.PutAsJsonAsync("/settings", new
            {
                values = new Dictionary<string, object>
                {
                    ["Margin:InitialMarginRate"] = 0.40m,
                },
            }))
            {
                settingsResponse.EnsureSuccessStatusCode();
            }

            using (var scope = configuredFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var participant = await db.Participants.FirstAsync(candidate => candidate.Type == ParticipantType.Individual);
                participant.Type = ParticipantType.AIAgent;
                db.AiTraderConfigurations.Add(new AiTraderConfiguration
                {
                    ParticipantId = participant.Id,
                    ProviderId = "glm",
                    Model = "glm-4.6",
                    ApiKey = "secret-key",
                    Revision = 1,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                db.AiTraderCalls.Add(new AiTraderCall
                {
                    ParticipantId = participant.Id,
                    ParticipantName = participant.Name,
                    ProviderId = "glm",
                    ProviderLabel = "GLM",
                    Model = "glm-4.6",
                    PromptHash = "hash",
                    RequestJson = "{}",
                    Status = AiTraderCallStatus.Completed,
                    RequestedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            using var resetResponse = await client.PostAsync("/market/reset", null);
            resetResponse.EnsureSuccessStatusCode();

            using (var scope = configuredFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(0, await db.AiTraderConfigurations.CountAsync());
                Assert.Equal(0, await db.AiTraderCalls.CountAsync());
                Assert.Equal("0.40", await db.GameSettings
                    .Where(setting => setting.Key == "Margin:InitialMarginRate")
                    .Select(setting => setting.ValueJson)
                    .SingleAsync());
            }

            var settings = await client.GetFromJsonAsync<JsonElement[]>("/settings");
            var margin = Assert.Single(settings!, setting =>
                setting.GetProperty("key").GetString() == "Margin:InitialMarginRate");
            Assert.Equal(0.40m, margin.GetProperty("value").GetDecimal());
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AiProvidersEndpointListsConfiguredProviders()
    {
        await WithClientAsync(async (client, _) =>
        {
            using var doc = JsonDocument.Parse(await client.GetStringAsync("/ai/providers"));
            var providers = doc.RootElement.EnumerateArray().ToList();
            var ids = providers.Select(entry => entry.GetProperty("id").GetString()).ToList();
            Assert.Contains("glm", ids);
            Assert.Contains("minimax", ids);
            var glm = providers.Single(entry => entry.GetProperty("id").GetString() == "glm");
            Assert.Equal("GLM", glm.GetProperty("label").GetString());
            Assert.NotEmpty(glm.GetProperty("models").EnumerateArray().ToList());
        });
    }

    [Fact]
    public async Task AutomationConvertsIndividualToAiAndBackWithoutExposingKey()
    {
        await WithClientAsync(async (client, factoryInstance) =>
        {
            await client.PostAsync("/market/seed", null);
            var participantId = await FirstIndividualIdAsync(factoryInstance);

            using var put = await client.PutAsJsonAsync(
                $"/participants/{participantId}/automation",
                new { type = "AIAgent", providerId = "glm", model = "glm-4.6", apiKey = "secret-key" });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var putBody = await put.Content.ReadAsStringAsync();
            Assert.DoesNotContain("secret-key", putBody);
            using (var detail = JsonDocument.Parse(putBody))
            {
                Assert.Equal("glm", detail.RootElement.GetProperty("aiProviderId").GetString());
                Assert.Equal("GLM", detail.RootElement.GetProperty("aiProviderLabel").GetString());
                Assert.Equal("glm-4.6", detail.RootElement.GetProperty("aiModel").GetString());
                Assert.True(detail.RootElement.GetProperty("hasAiApiKey").GetBoolean());
            }

            Assert.DoesNotContain("secret-key", await client.GetStringAsync("/participants"));

            using var back = await client.PutAsJsonAsync(
                $"/participants/{participantId}/automation",
                new { type = "Individual", providerId = (string?)null, model = (string?)null, apiKey = (string?)null });
            Assert.Equal(HttpStatusCode.OK, back.StatusCode);
            using var backDetail = JsonDocument.Parse(await back.Content.ReadAsStringAsync());
            Assert.False(backDetail.RootElement.GetProperty("hasAiApiKey").GetBoolean());
        });
    }

    [Fact]
    public async Task AutomationReturnsBadRequestAndNotFound()
    {
        await WithClientAsync(async (client, factoryInstance) =>
        {
            await client.PostAsync("/market/seed", null);
            var participantId = await FirstIndividualIdAsync(factoryInstance);

            using var bad = await client.PutAsJsonAsync(
                $"/participants/{participantId}/automation",
                new { type = "AIAgent", providerId = "unknown", model = "x", apiKey = "k" });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

            using var missing = await client.PutAsJsonAsync(
                "/participants/999999/automation",
                new { type = "AIAgent", providerId = "glm", model = "glm-4.6", apiKey = "k" });
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        });
    }

    [Fact]
    public async Task AutomationTestReturnsProviderReply()
    {
        await WithClientAsync(
            async (client, factoryInstance) =>
            {
                await client.PostAsync("/market/seed", null);
                var participantId = await FirstIndividualIdAsync(factoryInstance);

                using var response = await client.PostAsJsonAsync(
                    $"/participants/{participantId}/automation/test",
                    new { providerId = "glm", model = "glm-4.6", apiKey = "secret-key" });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal("I am the model.", doc.RootElement.GetProperty("assistantContent").GetString());
                Assert.Equal("I am the model.", doc.RootElement.GetProperty("responseBody").GetString());
            },
            configure: services =>
            {
                services.RemoveAll<IAiProviderClient>();
                services.AddScoped<IAiProviderClient>(_ => new StubAiProviderClient("I am the model."));
            });
    }

    [Fact]
    public async Task AiCallHistoryIsNewestFirstBoundedAndOwnerScoped()
    {
        await WithClientAsync(async (client, factoryInstance) =>
        {
            await client.PostAsync("/market/seed", null);

            int ownerId;
            int otherId;
            using (var scope = factoryInstance.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var individuals = await db.Participants
                    .Where(participant => participant.Type == ParticipantType.Individual)
                    .Take(2)
                    .ToListAsync();
                ownerId = individuals[0].Id;
                otherId = individuals[1].Id;
                for (var index = 0; index < 25; index++)
                {
                    db.AiTraderCalls.Add(AiCall(ownerId));
                }

                await db.SaveChangesAsync();
            }

            using var pageDoc = JsonDocument.Parse(
                await client.GetStringAsync($"/participants/{ownerId}/ai-calls?page=1&pageSize=100"));
            Assert.Equal(25, pageDoc.RootElement.GetProperty("total").GetInt32());
            var items = pageDoc.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(20, items.Count);
            var ids = items.Select(item => item.GetProperty("id").GetInt64()).ToList();
            Assert.Equal(ids.OrderByDescending(id => id).ToList(), ids);

            var callId = ids[0];
            using var ownerDetail = await client.GetAsync($"/participants/{ownerId}/ai-calls/{callId}");
            Assert.Equal(HttpStatusCode.OK, ownerDetail.StatusCode);
            using var detailDoc = JsonDocument.Parse(await ownerDetail.Content.ReadAsStringAsync());
            Assert.False(string.IsNullOrEmpty(detailDoc.RootElement.GetProperty("requestJson").GetString()));

            using var otherDetail = await client.GetAsync($"/participants/{otherId}/ai-calls/{callId}");
            Assert.Equal(HttpStatusCode.NotFound, otherDetail.StatusCode);
        });
    }

    private static async Task<int> FirstIndividualIdAsync(WebApplicationFactory<Program> factoryInstance)
    {
        using var scope = factoryInstance.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Participants.FirstAsync(participant =>
            participant.Type == ParticipantType.Individual && participant.IsActive)).Id;
    }

    private static AiTraderCall AiCall(int participantId) => new()
    {
        ParticipantId = participantId,
        ParticipantName = "Trader",
        ProviderId = "glm",
        ProviderLabel = "GLM",
        Model = "glm-4.6",
        PromptHash = "hash",
        RequestJson = "{\"prompt\":true}",
        Status = AiTraderCallStatus.Completed,
        RequestedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task PlayerCanFundABigInvestmentThroughTheInvestEndpoint()
    {
        await WithClientAsync(async (client, configured) =>
        {
            await client.PostAsync("/market/seed", null);
            await client.PostAsJsonAsync("/player", new { name = "Ada" });

            using var playerJson = await client.GetFromJsonAsync<JsonDocument>("/player");
            var playerId = playerJson!.RootElement.GetProperty("id").GetInt32();

            using var companies = await client.GetFromJsonAsync<JsonDocument>("/companies");
            var companyId = companies!.RootElement.EnumerateArray().First().GetProperty("id").GetInt32();

            using var detail = await client.GetFromJsonAsync<JsonDocument>($"/companies/{companyId}");
            var marketCap = detail!.RootElement.GetProperty("marketCap").GetDecimal();
            var price = detail.RootElement.GetProperty("currentPrice").GetDecimal();
            var sharesBefore = detail.RootElement.GetProperty("issuedSharesCount").GetInt32();

            // Fund the player generously so a 40%-of-cap deal is affordable.
            using (var scope = configured.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var player = await db.Participants.SingleAsync(participant => participant.Id == playerId);
                player.CurrentBalance = (marketCap * 3m) + 1_000_000m;
                player.SettledCashBalance = player.CurrentBalance;
                await db.SaveChangesAsync();
            }

            // Below the 40% floor is rejected.
            using var tooSmall = await client.PostAsJsonAsync(
                $"/companies/{companyId}/invest", new { participantId = playerId, amount = 1m });
            Assert.Equal(HttpStatusCode.BadRequest, tooSmall.StatusCode);

            // An unknown participant is rejected.
            using var missing = await client.PostAsJsonAsync(
                $"/companies/{companyId}/invest", new { participantId = 999999, amount = marketCap });
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

            // A valid deal at or above the 40% floor mints new shares and returns the count.
            var amount = Math.Ceiling(marketCap * 0.4m) + price;
            using var ok = await client.PostAsJsonAsync(
                $"/companies/{companyId}/invest", new { participantId = playerId, amount });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            using var okDoc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
            Assert.True(okDoc.RootElement.GetProperty("sharesMinted").GetInt32() > 0);

            using var afterDetail = await client.GetFromJsonAsync<JsonDocument>($"/companies/{companyId}");
            Assert.True(afterDetail!.RootElement.GetProperty("issuedSharesCount").GetInt32() > sharesBefore);

            // The deal is recorded and surfaced by all three investment feeds.
            using var companyInvestments = await client.GetFromJsonAsync<JsonDocument>($"/companies/{companyId}/investments");
            var companyRow = companyInvestments!.RootElement.EnumerateArray().Single();
            Assert.Equal(playerId, companyRow.GetProperty("investorParticipantId").GetInt32());
            Assert.True(companyRow.GetProperty("sharesIssued").GetInt32() > 0);
            Assert.True(companyRow.GetProperty("finalCapitalization").GetDecimal()
                > companyRow.GetProperty("capitalizationBeforeDeal").GetDecimal());

            using var participantInvestments = await client.GetFromJsonAsync<JsonDocument>($"/participants/{playerId}/investments");
            Assert.Equal(companyId, participantInvestments!.RootElement.EnumerateArray().Single().GetProperty("companyId").GetInt32());

            using var marketInvestments = await client.GetFromJsonAsync<JsonDocument>("/investments");
            Assert.Contains(
                marketInvestments!.RootElement.EnumerateArray(),
                row => row.GetProperty("companyId").GetInt32() == companyId
                    && row.GetProperty("investorParticipantId").GetInt32() == playerId);
        });
    }

    private async Task WithClientAsync(
        Func<HttpClient, WebApplicationFactory<Program>, Task> body,
        Action<IServiceCollection>? configure = null)
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"trader-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databaseDirectory);
        try
        {
            var configured = CreateFactory(Path.Combine(databaseDirectory, "app.db"));
            if (configure is not null)
            {
                configured = configured.WithWebHostBuilder(builder => builder.ConfigureServices(configure));
            }

            using (configured)
            using (var client = configured.CreateClient())
            {
                await body(client, configured);
            }
        }
        finally
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    private sealed class StubAiProviderClient(string content) : IAiProviderClient
    {
        public PreparedAiProviderRequest Prepare(AiProviderDescriptor provider, string model, string systemMessage, string userMessage)
            => new(provider.Id, provider.Label, model, provider.Endpoint, "{}");

        public Task<AiProviderResponse> SendTestAsync(AiProviderDescriptor provider, string model, string apiKey, CancellationToken cancellationToken)
            => Task.FromResult(new AiProviderResponse(AiProviderCallOutcome.Success, 200, content, content, 1, 1, 2, null, null));

        public Task<AiProviderResponse> SendAsync(PreparedAiProviderRequest prepared, string apiKey, CancellationToken cancellationToken)
            => Task.FromResult(new AiProviderResponse(AiProviderCallOutcome.Success, 200, content, content, 1, 1, 2, null, null));
    }

    private WebApplicationFactory<Program> CreateFactory(string databasePath)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={databasePath}");

            // Auditors fire every cycle under the shared Random and would perturb the exact-count assertions
            // below; they are exercised directly against seeded rating rows instead.
            builder.UseSetting("Auditor:Enabled", "false");

            // The automated big-investment roll would likewise add orders and trades on a random cycle and perturb
            // the exact-count assertions; the manual invest endpoint it shares an executor with is unaffected.
            builder.UseSetting("BigInvestment:Enabled", "false");

            // The AI coordinator is a hosted service that would otherwise poll and, for any configuration these
            // tests create, attempt a real provider call. It stays disabled so tests never touch the network.
            builder.UseSetting("AiTrading:Enabled", "false");

            // API tests advance cycles explicitly through MarketService so no background tick can race their
            // exact-count assertions.
            builder.UseSetting("MarketLoop:Enabled", "false");

            // The no-op engine removes generated trades so these tests settle only the order they place by
            // hand and can assert exact counts. Settlement goes through the manual advance (see RunCycleAsync),
            // which crosses the book immediately; the live tick's one-cycle order hold is covered elsewhere.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDecisionEngine>();
                services.AddScoped<IDecisionEngine, NoOpDecisionEngine>();
            });
        });
    }

    // Settles hand-placed orders through the manual advance, which crosses the book in the same cycle. The
    // live tick (RunCycleTickAsync) holds orders created this cycle for one cycle before matching; that timing
    // is covered by MarketLoopTests, so these projection tests keep a deterministic single-cycle settle.
    private static async Task<AdvanceCycleResult> RunCycleAsync(WebApplicationFactory<Program> configuredFactory)
    {
        using var scope = configuredFactory.Services.CreateScope();
        var marketService = scope.ServiceProvider.GetRequiredService<MarketService>();
        await marketService.SetStatusAsync(MarketStatus.Running);
        return await marketService.AdvanceCycleAsync();
    }

    private sealed record ParticipantDto(
        int Id,
        string Name,
        decimal CurrentBalance,
        decimal SettledCashBalance,
        decimal UnsettledCashBalance,
        decimal ReservedBalance,
        int SharesOwned);

    private sealed record PagedParticipantsDto(ParticipantRosterDto[] Items, int Total, int Page, int PageSize);

    private sealed record ParticipantRosterDto(
        int Id,
        string Name,
        string Type,
        int? MemberOfCollectiveFundId,
        string? MemberOfCollectiveFundName);

    private sealed record PlayerDto(int Id, int? FundParticipantId, decimal? FundCurrentBalance, int? FundPopularityIndex);

    private sealed record FundAdvertiseQuoteDto(decimal Price, decimal Fraction, decimal GrowthPct, decimal FundWorth, int PopularityIndex);

    private sealed record CompanyDto(int Id, string Name, decimal? CurrentPrice, decimal PriceChangePct);

    private sealed record CompanyDetailDto(
        int Id,
        string Name,
        int IssuedSharesCount,
        decimal? CurrentPrice,
        decimal PriceChangePct,
        decimal MarketCap,
        decimal IssuerCash,
        int SharesHeldByIssuer,
        int SharesOutstanding,
        int ShareholderCount,
        string? CurrentRating,
        string? PreviousRating);

    private sealed record CorporateCashMovementDto(
        int Id,
        string Type,
        decimal Amount,
        int CreatedInCycleNumber);

    private sealed record PagedCorporateCashMovementsDto(
        CorporateCashMovementDto[] Items,
        int Total,
        int Page,
        int PageSize);

    private sealed record AuditorDto(int Id, string Name, string Description, int AuditCount);

    private sealed record AuditRowDto(int Id, int CompanyId, string CompanyName, string Rating, decimal? ImpactPercent, int CyclesAgo);

    private sealed record PagedAuditsDto(AuditRowDto[] Items, int Total, int Page, int PageSize);

    private sealed record CompanyRatingDto(int Id, string Rating, decimal? ImpactPercent, string AuditorName, int CyclesAgo);

    private sealed record ShareEmissionDto(int Id, int SharesEmitted, int RecipientCount, int CyclesAgo);

    private sealed record NewsDto(int Id, string Title, int PublishedInCycleId, int PublishedInCycleNumber);

    private sealed record ShareholderDto(
        int OwnerId,
        string OwnerName,
        int Shares,
        decimal MarketValue,
        decimal CostBasis,
        decimal PctOfIssued);

    private sealed record ShareTransactionDto(
        int Id,
        int? SellerId,
        int BuyerId,
        int Quantity,
        decimal Price,
        int? TradeDayNumber,
        int? DueDayNumber,
        string? SettlementStatus);

    private sealed record PagedShareTransactionsDto(ShareTransactionDto[] Items, int Total, int Page, int PageSize);

    private sealed record SettlementDto(
        int Id,
        int ShareTransactionId,
        string Side,
        int CompanyId,
        string CompanyName,
        int Quantity,
        decimal CashAmount,
        int TradeDayNumber,
        int DueDayNumber,
        string Status);

    private sealed record PagedSettlementsDto(SettlementDto[] Items, int Total, int Page, int PageSize);

    private sealed record MoneyTransactionDetailDto(
        int Id,
        string Type,
        decimal Amount,
        int CreatedInCycleId,
        int? CycleNumber,
        int? FromWhomId,
        string? FromWhomName,
        string? Description,
        MoneyTransactionOrderDto? Order,
        MoneyTransactionTradeDto? Trade,
        MoneyTransactionLoanDto? Loan,
        DividendPayoutLineDto[]? DividendBreakdown);

    private sealed record MoneyTransactionOrderDto(int OrderId, int CompanyId, string? CompanyName, string Side, string Status);

    private sealed record MoneyTransactionTradeDto(int ShareTransactionId, int CompanyId, string? CompanyName, int Quantity, decimal Price);

    private sealed record MoneyTransactionLoanDto(
        int LoanId,
        decimal Principal,
        decimal RemainingPrincipal,
        decimal PastDuePrincipal,
        decimal PastDueInterest,
        decimal AccruedFees,
        decimal TotalLiability,
        string Status);

    private sealed record LoanDto(
        int Id,
        decimal RemainingPrincipal,
        decimal PastDuePrincipal,
        decimal PastDueInterest,
        decimal AccruedFees,
        decimal TotalLiability);

    private sealed record DividendPayoutLineDto(int CompanyId, string? CompanyName, decimal Amount);

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

    private sealed record ClosedFundDto(
        int Id,
        int ParticipantId,
        string Name,
        string? Temperament,
        string? RiskProfile,
        decimal PeakNetWorth,
        int CreatedInCycleNumber,
        DateTime? ClosedAt);

    private sealed record PagedClosedFundsDto(ClosedFundDto[] Items, int Total, int Page, int PageSize);

    private sealed record FundMembershipEventDto(
        int Id,
        string Type,
        decimal Amount,
        int CollectiveFundId,
        int MemberParticipantId,
        string MemberName,
        int FundParticipantId,
        string FundName,
        int CreatedInCycleId,
        int CreatedInCycleNumber);

    private sealed record PagedFundMembershipEventsDto(FundMembershipEventDto[] Items, int Total, int Page, int PageSize);

    private sealed record CycleDto(int Id, int CycleNumber);

    private sealed record HoldingDto(
        int CompanyId,
        string CompanyName,
        int Shares,
        int SettledShares,
        int PendingShares,
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
        decimal SettledCashBalance,
        decimal UnsettledCashBalance,
        decimal ReservedBalance,
        decimal AvailableBalance,
        int SharesOwned,
        bool IsActive);

    private sealed record PagedHoldingsDto(HoldingDto[] Items, int Total, int Page, int PageSize);

    private sealed record IndustryHoldingDto(
        int IndustryId,
        string IndustryName,
        int CompanyCount,
        int Shares,
        decimal Value,
        decimal CostBasis,
        decimal Pnl,
        double Pct);

    private sealed record PagedIndustryHoldingsDto(IndustryHoldingDto[] Items, int Total, int Page, int PageSize);

    private sealed record InvestmentDto(int Id, string CompanyName, decimal DealValue, int SharesIssued);

    private sealed record PagedInvestmentsDto(InvestmentDto[] Items, int Total, int Page, int PageSize);

    private sealed record FundMemberDto(int ParticipantId, string Name, decimal Deposit);

    private sealed record PagedFundMembersDto(FundMemberDto[] Items, int Total, int Page, int PageSize);

    private sealed record FundDetailDto(int Id, CollectiveFundMemberDto[] CollectiveFundMembers);

    private sealed record CollectiveFundMemberDto(
        int ParticipantId,
        string Name,
        decimal Deposit,
        bool IsLeaving,
        int LeaveCountdownTradingDays,
        bool IsFounder);

    private sealed record MoneyTransactionDto(int Id, string Type, decimal Amount, int CreatedInCycleId);

    private sealed record PagedMoneyTransactionsDto(MoneyTransactionDto[] Items, int Total, int Page, int PageSize);

    private sealed record ActivityDto(
        int CycleNumber,
        int TradingDayNumber,
        int TradingCycleNumber,
        int OrdersPlaced);

    private sealed record MarketDto(
        int Id,
        string Name,
        string Status,
        int? CurrentCycleId,
        int? TradingDayNumber,
        string? TradingSessionState,
        int? TradingCycleNumber,
        int? RemainingTradingCycles,
        int? RemainingPhaseSeconds,
        int? TradingCycleSeconds);

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
