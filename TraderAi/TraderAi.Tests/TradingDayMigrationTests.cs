using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Tests;

public sealed class TradingDayMigrationTests
{
    [Fact]
    public async Task CompanyFundamentalsMigrationCreatesCompleteAuditAndFinancialSchema()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"trader-ai-fundamentals-migration-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using var context = new AppDbContext(options);
            var migrator = context.GetService<IMigrator>();

            await migrator.MigrateAsync("AddCompanyFundamentalsAndAudits");

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var schemaCommand = connection.CreateCommand();
            schemaCommand.CommandText = """
                SELECT type, name, tbl_name, COALESCE(sql, '')
                FROM sqlite_master
                WHERE type IN ('table', 'index')
                """;
            await using var reader = await schemaCommand.ExecuteReaderAsync();
            var tables = new Dictionary<string, string>(StringComparer.Ordinal);
            var indexes = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                if (reader.GetString(0) == "table")
                {
                    tables[reader.GetString(1)] = reader.GetString(3);
                }
                else
                {
                    indexes.Add(reader.GetString(1));
                }
            }

            Assert.Subset(
                tables.Keys.ToHashSet(StringComparer.Ordinal),
                new HashSet<string>(
                    [
                        "CompanyAuditEvidence",
                        "CompanyDividendEvents",
                        "CompanyFinancialSnapshots",
                        "PortfolioAuditSummaries",
                        "PortfolioAuditSummaryItems",
                        "PrimaryIssuanceEvents",
                    ],
                    StringComparer.Ordinal));

            Assert.Contains("CK_CompanyDividendEvents_Amounts_NonNegative", tables["CompanyDividendEvents"]);
            Assert.Contains("CK_CompanyDividendEvents_FundedNotAboveDeclared", tables["CompanyDividendEvents"]);
            Assert.Contains("CK_CompanyFinancialSnapshots_TradingDay_Positive", tables["CompanyFinancialSnapshots"]);
            Assert.Contains("CK_CompanyFinancialSnapshots_NonNegativeValues", tables["CompanyFinancialSnapshots"]);
            Assert.Contains("CK_CompanyFinancialSnapshots_DebtWithinLiabilities", tables["CompanyFinancialSnapshots"]);
            Assert.Contains("CK_CompanyFinancialSnapshots_ScoresInRange", tables["CompanyFinancialSnapshots"]);
            Assert.Contains("CK_CompanyFinancialSnapshots_Moment_Valid", tables["CompanyFinancialSnapshots"]);
            Assert.Contains("CK_CompanyFinancialSnapshots_ManagementOutlook_Valid", tables["CompanyFinancialSnapshots"]);
            Assert.Contains("CK_CompanyFinancialSnapshots_Levels_Valid", tables["CompanyFinancialSnapshots"]);
            Assert.Contains("CK_CompanyFinancialSnapshots_ChangedMetrics_Valid", tables["CompanyFinancialSnapshots"]);
            Assert.Contains("CK_PortfolioAuditSummaryItems_Quantities_NonNegative", tables["PortfolioAuditSummaryItems"]);
            Assert.Contains("CK_PrimaryIssuanceEvents_Counts_Positive", tables["PrimaryIssuanceEvents"]);
            Assert.Contains("CK_PrimaryIssuanceEvents_Counts_Coherent", tables["PrimaryIssuanceEvents"]);

            Assert.Subset(
                indexes,
                new HashSet<string>(
                    [
                        "IX_CompanyAuditEvidence_CompanyFinancialSnapshotId_CompanyId",
                        "IX_CompanyAuditEvidence_CompanyId_EffectiveTradingDayNumber",
                        "IX_CompanyAuditEvidence_LatestDividendEventId_CompanyId",
                        "IX_CompanyDividendEvents_CompanyId_TradingDayNumber_Id",
                        "IX_CompanyFinancialSnapshots_CompanyId_CreatedInCycleId",
                        "IX_CompanyFinancialSnapshots_CompanyId_TradingDayNumber_Moment",
                        "IX_CompanyFinancialSnapshots_CreatedInCycleId",
                        "IX_CompanyFinancialSnapshots_LatestDividendEventId_CompanyId",
                        "IX_PortfolioAuditSummaries_NewsPostId",
                        "IX_PortfolioAuditSummaryItems_CompanyId",
                        "IX_PortfolioAuditSummaryItems_CompanyRatingId",
                        "IX_PortfolioAuditSummaryItems_PortfolioAuditSummaryId_CompanyId",
                        "IX_PrimaryIssuanceEvents_CompanyId_CreatedInCycleId",
                        "IX_PrimaryIssuanceEvents_CreatedInCycleId",
                    ],
                    StringComparer.Ordinal));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task CompanyFundamentalsMigrationNormalizesLegacyExtraRatings()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"trader-ai-rating-migration-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using var context = new AppDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260722154353_ResetAiSystemPromptForPriceOffset");

            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO Industries (Id, Name, SentimentValue, SentimentVolatility, SectorBeta)
                VALUES (1, 'Legacy industry', 0, 0, 1);

                INSERT INTO Companies (
                    Id, Name, IsFavorite, IndustryId, IssuedSharesCount, CashBalance,
                    LastDividendCapitalization, CreatedInCycleId, LastMergedInCycleId,
                    ClosedInCycleId, CloseProtectedUntilTradingDayNumber, ClosedAt, CreatedAt, UpdatedAt)
                VALUES (
                    1, 'Legacy issuer', 0, 1, 1000, 1000,
                    NULL, NULL, NULL,
                    NULL, NULL, NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);

                INSERT INTO Auditors (Id, Name, Description, CreatedAt)
                VALUES (1, 'Legacy auditor', 'Migration fixture', CURRENT_TIMESTAMP);

                INSERT INTO CompanyRatings (
                    Id, CompanyId, AuditorId, Rating, ImpactPercent, CreatedInCycleId, CreatedAt)
                VALUES
                    (1, 1, 1, 'Extra', NULL, 1, CURRENT_TIMESTAMP),
                    (2, 1, 1, '2', NULL, 2, CURRENT_TIMESTAMP);
                """);

            await migrator.MigrateAsync("AddCompanyFundamentalsAndAudits");
            context.ChangeTracker.Clear();

            var ratings = await context.CompanyRatings
                .AsNoTracking()
                .OrderBy(rating => rating.Id)
                .Select(rating => rating.Rating)
                .ToListAsync();
            Assert.Equal([CompanyRiskRating.HighRisk, CompanyRiskRating.HighRisk], ratings);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var valuesCommand = connection.CreateCommand();
            valuesCommand.CommandText = "SELECT Rating FROM CompanyRatings ORDER BY Id";
            await using var valuesReader = await valuesCommand.ExecuteReaderAsync();
            var storedValues = new List<string>();
            while (await valuesReader.ReadAsync())
            {
                storedValues.Add(valuesReader.GetString(0));
            }
            Assert.Equal(["HighRisk", "HighRisk"], storedValues);

            await using var typeCommand = connection.CreateCommand();
            typeCommand.CommandText = "SELECT type FROM pragma_table_info('CompanyRatings') WHERE name = 'Rating'";
            Assert.Equal("TEXT", (string?)await typeCommand.ExecuteScalarAsync());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task HistoricalCyclesArePartitionedIntoTwoHundredTenCycleTradingDays()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"trader-ai-migration-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using var context = new AppDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260712182312_RestoreCurrentPriceAnchors");

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                for (var cycleNumber = 1; cycleNumber <= 421; cycleNumber++)
                {
                    await using var cycleCommand = connection.CreateCommand();
                    cycleCommand.CommandText = "INSERT INTO MarketCycles (CycleNumber, Status) VALUES ($cycleNumber, 'Completed')";
                    cycleCommand.Parameters.AddWithValue("$cycleNumber", cycleNumber);
                    await cycleCommand.ExecuteNonQueryAsync();
                }

                await using var marketCommand = connection.CreateCommand();
                marketCommand.CommandText = """
                    INSERT INTO Markets (Name, Status, CurrentCycleId, CreatedAt, UpdatedAt)
                    VALUES ('Backfill market', 'Paused', (SELECT Id FROM MarketCycles WHERE CycleNumber = 421), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                    """;
                await marketCommand.ExecuteNonQueryAsync();
            }

            await migrator.MigrateAsync("20260712183323_AddTradingDays");

            await using var verification = new SqliteConnection($"Data Source={databasePath}");
            await verification.OpenAsync();
            await using var daysCommand = verification.CreateCommand();
            daysCommand.CommandText = "SELECT DayNumber, State, OpenedInCycleId, ClosedInCycleId FROM TradingDays ORDER BY DayNumber";
            await using var reader = await daysCommand.ExecuteReaderAsync();
            var days = new List<(int Number, string State, int OpenedCycleId, int? ClosedCycleId)>();
            while (await reader.ReadAsync())
            {
                days.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetInt32(3)));
            }

            Assert.Equal(3, days.Count);
            Assert.Equal((1, "Break", 1, (int?)210), days[0]);
            Assert.Equal((2, "Break", 211, (int?)420), days[1]);
            Assert.Equal((3, "Trading", 421, (int?)null), days[2]);

            await using var cyclesCommand = verification.CreateCommand();
            cyclesCommand.CommandText = """
                SELECT td.DayNumber, MIN(mc.TradingCycleNumber), MAX(mc.TradingCycleNumber), COUNT(*)
                FROM MarketCycles mc
                JOIN TradingDays td ON td.Id = mc.TradingDayId
                GROUP BY td.DayNumber
                ORDER BY td.DayNumber
                """;
            await using var cyclesReader = await cyclesCommand.ExecuteReaderAsync();
            var partitions = new List<(int Day, int Min, int Max, int Count)>();
            while (await cyclesReader.ReadAsync())
            {
                partitions.Add((cyclesReader.GetInt32(0), cyclesReader.GetInt32(1), cyclesReader.GetInt32(2), cyclesReader.GetInt32(3)));
            }

            Assert.Equal([(1, 1, 210, 210), (2, 1, 210, 210), (3, 1, 1, 1)], partitions);

            await using var currentDayCommand = verification.CreateCommand();
            currentDayCommand.CommandText = """
                SELECT td.DayNumber
                FROM Markets market
                JOIN TradingDays td ON td.Id = market.CurrentTradingDayId
                """;
            Assert.Equal(3L, (long)(await currentDayCommand.ExecuteScalarAsync())!);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }
}
