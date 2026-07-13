using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TraderAi.Data;

namespace TraderAi.Tests;

public sealed class TradingDayMigrationTests
{
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
