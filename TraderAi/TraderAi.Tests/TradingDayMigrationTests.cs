using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
            var setupOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using (var setupContext = new AppDbContext(setupOptions))
            {
                await setupContext.GetService<IMigrator>()
                    .MigrateAsync("20260722154353_ResetAiSystemPromptForPriceOffset");
            }

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .ConfigureWarnings(warnings =>
                    warnings.Throw(RelationalEventId.NonTransactionalMigrationOperationWarning))
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
            Assert.Contains("AK_CompanyRatings_Id_CompanyId", tables["CompanyRatings"]);
            Assert.Contains("\"BusinessRiskLevel\" TEXT NOT NULL", tables["CompanyAuditEvidence"]);
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
                        "IX_CompanyAuditEvidence_CompanyRatingId_CompanyId",
                        "IX_CompanyAuditEvidence_LatestDividendEventId_CompanyId",
                        "IX_CompanyDividendEvents_CompanyId_TradingDayNumber_Id",
                        "IX_CompanyDividendEvents_CreatedInCycleId",
                        "IX_CompanyFinancialSnapshots_CompanyId_CreatedInCycleId",
                        "IX_CompanyFinancialSnapshots_CompanyId_TradingDayNumber_Moment",
                        "IX_CompanyFinancialSnapshots_CreatedInCycleId",
                        "IX_CompanyFinancialSnapshots_LatestDividendEventId_CompanyId",
                        "IX_PortfolioAuditSummaries_NewsPostId",
                        "IX_PortfolioAuditSummaryItems_CompanyId",
                        "IX_PortfolioAuditSummaryItems_CompanyRatingId_CompanyId",
                        "IX_PortfolioAuditSummaryItems_PortfolioAuditSummaryId_CompanyId",
                        "IX_PrimaryIssuanceEvents_CompanyId_CreatedInCycleId",
                        "IX_PrimaryIssuanceEvents_CreatedInCycleId",
                    ],
                    StringComparer.Ordinal));

            await reader.DisposeAsync();
            await using var foreignKeysCommand = connection.CreateCommand();
            foreignKeysCommand.CommandText = """
                SELECT 'CompanyAuditEvidence', id, "from", "table", "to"
                FROM pragma_foreign_key_list('CompanyAuditEvidence')
                UNION ALL
                SELECT 'PortfolioAuditSummaryItems', id, "from", "table", "to"
                FROM pragma_foreign_key_list('PortfolioAuditSummaryItems')
                UNION ALL
                SELECT 'CompanyDividendEvents', id, "from", "table", "to"
                FROM pragma_foreign_key_list('CompanyDividendEvents')
                """;
            await using var foreignKeysReader = await foreignKeysCommand.ExecuteReaderAsync();
            var foreignKeys = new List<(string Dependent, long Id, string From, string Principal, string To)>();
            while (await foreignKeysReader.ReadAsync())
            {
                foreignKeys.Add((
                    foreignKeysReader.GetString(0),
                    foreignKeysReader.GetInt64(1),
                    foreignKeysReader.GetString(2),
                    foreignKeysReader.GetString(3),
                    foreignKeysReader.GetString(4)));
            }

            var evidenceRatingForeignKeys = foreignKeys
                .Where(foreignKey =>
                    foreignKey.Dependent == "CompanyAuditEvidence"
                    && foreignKey.Principal == "CompanyRatings")
                .ToList();
            Assert.Equal(2, evidenceRatingForeignKeys.Count);
            Assert.Single(evidenceRatingForeignKeys.Select(foreignKey => foreignKey.Id).Distinct());
            Assert.Contains(evidenceRatingForeignKeys, foreignKey =>
                foreignKey.From == "CompanyRatingId" && foreignKey.To == "Id");
            Assert.Contains(evidenceRatingForeignKeys, foreignKey =>
                foreignKey.From == "CompanyId" && foreignKey.To == "CompanyId");

            var itemRatingForeignKeys = foreignKeys
                .Where(foreignKey =>
                    foreignKey.Dependent == "PortfolioAuditSummaryItems"
                    && foreignKey.Principal == "CompanyRatings")
                .ToList();
            Assert.Equal(2, itemRatingForeignKeys.Count);
            Assert.Single(itemRatingForeignKeys.Select(foreignKey => foreignKey.Id).Distinct());
            Assert.Contains(itemRatingForeignKeys, foreignKey =>
                foreignKey.From == "CompanyRatingId" && foreignKey.To == "Id");
            Assert.Contains(itemRatingForeignKeys, foreignKey =>
                foreignKey.From == "CompanyId" && foreignKey.To == "CompanyId");

            Assert.Contains(foreignKeys, foreignKey =>
                foreignKey.Dependent == "CompanyDividendEvents"
                && foreignKey.From == "CreatedInCycleId"
                && foreignKey.Principal == "MarketCycles"
                && foreignKey.To == "Id");
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task CompanyFundamentalsMigrationNormalizesAllLegacyRatings()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"trader-ai-rating-migration-{Guid.NewGuid():N}.db");
        try
        {
            var setupOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using (var setupContext = new AppDbContext(setupOptions))
            {
                var setupMigrator = setupContext.GetService<IMigrator>();
                await setupMigrator.MigrateAsync("20260722154353_ResetAiSystemPromptForPriceOffset");

                await setupContext.Database.ExecuteSqlRawAsync(
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
                        (1, 1, 1, 'Low', NULL, 1, CURRENT_TIMESTAMP),
                        (2, 1, 1, 'High', NULL, 2, CURRENT_TIMESTAMP),
                        (3, 1, 1, 'Extra', NULL, 3, CURRENT_TIMESTAMP),
                        (4, 1, 1, 0, NULL, 4, CURRENT_TIMESTAMP),
                        (5, 1, 1, 1, NULL, 5, CURRENT_TIMESTAMP),
                        (6, 1, 1, 2, NULL, 6, CURRENT_TIMESTAMP),
                        (100, 1, 1, 'Stable', NULL, 100, CURRENT_TIMESTAMP);

                    DELETE FROM CompanyRatings WHERE Id = 100;
                    """);
            }

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .ConfigureWarnings(warnings =>
                    warnings.Throw(RelationalEventId.NonTransactionalMigrationOperationWarning))
                .Options;
            await using var context = new AppDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("AddCompanyFundamentalsAndAudits");
            context.ChangeTracker.Clear();

            var ratings = await context.CompanyRatings
                .AsNoTracking()
                .OrderBy(rating => rating.Id)
                .Select(rating => rating.Rating)
                .ToListAsync();
            Assert.Equal(
                [
                    CompanyRiskRating.LowRisk,
                    CompanyRiskRating.HighRisk,
                    CompanyRiskRating.HighRisk,
                    CompanyRiskRating.LowRisk,
                    CompanyRiskRating.HighRisk,
                    CompanyRiskRating.HighRisk,
                ],
                ratings);

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
            Assert.Equal(
                ["LowRisk", "HighRisk", "HighRisk", "LowRisk", "HighRisk", "HighRisk"],
                storedValues);

            await using var typeCommand = connection.CreateCommand();
            typeCommand.CommandText = "SELECT type FROM pragma_table_info('CompanyRatings') WHERE name = 'Rating'";
            Assert.Equal("TEXT", (string?)await typeCommand.ExecuteScalarAsync());

            await using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = """
                INSERT INTO CompanyRatings (
                    CompanyId, AuditorId, Rating, ImpactPercent, CreatedInCycleId, CreatedAt)
                VALUES (1, 1, 'Stable', NULL, 7, CURRENT_TIMESTAMP);
                SELECT last_insert_rowid();
                """;
            Assert.Equal(101L, (long)(await insertCommand.ExecuteScalarAsync())!);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task CompanyFundamentalsMigrationDowngradePreservesRatingsWithoutNonTransactionalRebuild()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"trader-ai-rating-downgrade-{Guid.NewGuid():N}.db");
        try
        {
            const string predecessorMigration = "20260722154353_ResetAiSystemPromptForPriceOffset";
            var setupOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using (var setupContext = new AppDbContext(setupOptions))
            {
                await setupContext.GetService<IMigrator>().MigrateAsync(predecessorMigration);

                await setupContext.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO Industries (Id, Name, SentimentValue, SentimentVolatility, SectorBeta)
                    VALUES (1, 'Downgrade industry', 0, 0, 1);

                    INSERT INTO Companies (
                        Id, Name, IsFavorite, IndustryId, IssuedSharesCount, CashBalance,
                        LastDividendCapitalization, CreatedInCycleId, LastMergedInCycleId,
                        ClosedInCycleId, CloseProtectedUntilTradingDayNumber, ClosedAt, CreatedAt, UpdatedAt)
                    VALUES (
                        1, 'Downgrade issuer', 0, 1, 1000, 1000,
                        NULL, NULL, NULL,
                        NULL, NULL, NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);

                    INSERT INTO Auditors (Id, Name, Description, CreatedAt)
                    VALUES (1, 'Downgrade auditor', 'Migration fixture', CURRENT_TIMESTAMP);

                    INSERT INTO CompanyRatings (
                        Id, CompanyId, AuditorId, Rating, ImpactPercent, CreatedInCycleId, CreatedAt)
                    VALUES (41, 1, 1, 'Low', 12.34, 9, '2026-07-24T00:00:00Z');
                    """);
            }

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .ConfigureWarnings(warnings =>
                    warnings.Throw(RelationalEventId.NonTransactionalMigrationOperationWarning))
                .Options;
            await using var context = new AppDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("AddCompanyFundamentalsAndAudits");
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO CompanyRatings (
                    Id, CompanyId, AuditorId, Rating, ImpactPercent, CreatedInCycleId, CreatedAt)
                VALUES (200, 1, 1, 'Stable', NULL, 200, CURRENT_TIMESTAMP);

                DELETE FROM CompanyRatings WHERE Id = 200;
                """);
            await migrator.MigrateAsync(predecessorMigration);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var ratingCommand = connection.CreateCommand();
            ratingCommand.CommandText = """
                SELECT Id, CompanyId, AuditorId, Rating, ImpactPercent, CreatedInCycleId, CreatedAt
                FROM CompanyRatings
                """;
            await using var ratingReader = await ratingCommand.ExecuteReaderAsync();
            Assert.True(await ratingReader.ReadAsync());
            Assert.Equal(41L, ratingReader.GetInt64(0));
            Assert.Equal(1L, ratingReader.GetInt64(1));
            Assert.Equal(1L, ratingReader.GetInt64(2));
            Assert.Equal("LowRisk", ratingReader.GetString(3));
            Assert.Equal("12.34", ratingReader.GetString(4));
            Assert.Equal(9L, ratingReader.GetInt64(5));
            Assert.Equal("2026-07-24T00:00:00Z", ratingReader.GetString(6));
            Assert.False(await ratingReader.ReadAsync());

            await ratingReader.DisposeAsync();
            await using var foreignKeyCommand = connection.CreateCommand();
            foreignKeyCommand.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_list('CompanyRatings')";
            Assert.Equal(0L, (long)(await foreignKeyCommand.ExecuteScalarAsync())!);

            await using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText = """
                SELECT name
                FROM pragma_index_list('CompanyRatings')
                WHERE name NOT LIKE 'sqlite_autoindex_%'
                ORDER BY name
                """;
            await using var indexReader = await indexCommand.ExecuteReaderAsync();
            var indexes = new List<string>();
            while (await indexReader.ReadAsync())
            {
                indexes.Add(indexReader.GetString(0));
            }
            Assert.Equal(["IX_CompanyRatings_CompanyId_CreatedInCycleId"], indexes);

            await indexReader.DisposeAsync();
            await using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = """
                INSERT INTO CompanyRatings (
                    CompanyId, AuditorId, Rating, ImpactPercent, CreatedInCycleId, CreatedAt)
                VALUES (1, 1, 'Stable', NULL, 201, CURRENT_TIMESTAMP);
                SELECT last_insert_rowid();
                """;
            Assert.Equal(201L, (long)(await insertCommand.ExecuteScalarAsync())!);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task CompanyFundamentalsMigrationDowngradesAnEmptyRatingTableSafely()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"trader-ai-empty-rating-downgrade-{Guid.NewGuid():N}.db");
        try
        {
            const string predecessorMigration = "20260722154353_ResetAiSystemPromptForPriceOffset";
            var setupOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using (var setupContext = new AppDbContext(setupOptions))
            {
                await setupContext.GetService<IMigrator>().MigrateAsync("AddCompanyFundamentalsAndAudits");
            }

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .ConfigureWarnings(warnings =>
                    warnings.Throw(RelationalEventId.NonTransactionalMigrationOperationWarning))
                .Options;
            await using var context = new AppDbContext(options);
            await context.GetService<IMigrator>().MigrateAsync(predecessorMigration);

            Assert.Empty(await context.CompanyRatings.AsNoTracking().ToListAsync());
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
