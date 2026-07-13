using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CurrentTradingDayId",
                table: "Markets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TradingCycleNumber",
                table: "MarketCycles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TradingDayId",
                table: "MarketCycles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "TradingBreakCycles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TradingDayId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAfterCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    ElapsedSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingBreakCycles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradingDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DayNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    OpenedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClosedInCycleId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingDays", x => x.Id);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO TradingDays (DayNumber, State, OpenedInCycleId, ClosedInCycleId)
                SELECT
                    ((CycleNumber - 1) / 210) + 1,
                    CASE
                        WHEN ((CycleNumber - 1) / 210) + 1 = COALESCE(
                            (
                                SELECT ((currentCycle.CycleNumber - 1) / 210) + 1
                                FROM Markets market
                                JOIN MarketCycles currentCycle ON currentCycle.Id = market.CurrentCycleId
                                LIMIT 1
                            ),
                            (SELECT ((MAX(CycleNumber) - 1) / 210) + 1 FROM MarketCycles)
                        ) THEN 'Trading'
                        ELSE 'Break'
                    END,
                    MIN(Id),
                    CASE
                        WHEN ((CycleNumber - 1) / 210) + 1 = COALESCE(
                            (
                                SELECT ((currentCycle.CycleNumber - 1) / 210) + 1
                                FROM Markets market
                                JOIN MarketCycles currentCycle ON currentCycle.Id = market.CurrentCycleId
                                LIMIT 1
                            ),
                            (SELECT ((MAX(CycleNumber) - 1) / 210) + 1 FROM MarketCycles)
                        ) THEN NULL
                        ELSE MAX(Id)
                    END
                FROM MarketCycles
                GROUP BY ((CycleNumber - 1) / 210) + 1;

                UPDATE MarketCycles
                SET TradingDayId = COALESCE(
                        (SELECT Id FROM TradingDays WHERE DayNumber = ((MarketCycles.CycleNumber - 1) / 210) + 1),
                        0
                    ),
                    TradingCycleNumber = ((CycleNumber - 1) % 210) + 1;

                UPDATE Markets
                SET CurrentTradingDayId = (
                    SELECT tradingDay.Id
                    FROM MarketCycles currentCycle
                    JOIN TradingDays tradingDay
                        ON tradingDay.DayNumber = ((currentCycle.CycleNumber - 1) / 210) + 1
                    WHERE currentCycle.Id = Markets.CurrentCycleId
                )
                WHERE CurrentCycleId IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MarketCycles_TradingDayId_TradingCycleNumber",
                table: "MarketCycles",
                columns: new[] { "TradingDayId", "TradingCycleNumber" },
                unique: true,
                filter: "TradingDayId > 0");

            migrationBuilder.CreateIndex(
                name: "IX_TradingBreakCycles_TradingDayId_IsActive",
                table: "TradingBreakCycles",
                columns: new[] { "TradingDayId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_TradingDays_DayNumber",
                table: "TradingDays",
                column: "DayNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TradingBreakCycles");

            migrationBuilder.DropTable(
                name: "TradingDays");

            migrationBuilder.DropIndex(
                name: "IX_MarketCycles_TradingDayId_TradingCycleNumber",
                table: "MarketCycles");

            migrationBuilder.DropColumn(
                name: "CurrentTradingDayId",
                table: "Markets");

            migrationBuilder.DropColumn(
                name: "TradingCycleNumber",
                table: "MarketCycles");

            migrationBuilder.DropColumn(
                name: "TradingDayId",
                table: "MarketCycles");
        }
    }
}
