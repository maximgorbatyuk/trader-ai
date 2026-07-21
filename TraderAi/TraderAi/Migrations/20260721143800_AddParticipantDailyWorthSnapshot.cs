using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddParticipantDailyWorthSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ParticipantDailyWorthSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    TradingDayId = table.Column<int>(type: "INTEGER", nullable: false),
                    Balance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    HoldingsValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    LoanLiability = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    MarginLiability = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantDailyWorthSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantDailyWorthSnapshots_ParticipantId_TradingDayId",
                table: "ParticipantDailyWorthSnapshots",
                columns: new[] { "ParticipantId", "TradingDayId" },
                unique: true);

            // One-time back-population so existing markets chart daily worth immediately. Each day's close is the
            // participant's worth in that day's last recorded cycle, derived from the per-cycle worth already
            // captured (live plus archived) rather than synthesized. MarketCycles rows are never archived, so the
            // join always resolves; days with no per-cycle worth simply get no daily row.
            migrationBuilder.Sql(@"
INSERT INTO ParticipantDailyWorthSnapshots
    (ParticipantId, TradingDayId, Balance, HoldingsValue, LoanLiability, MarginLiability, CreatedAt)
SELECT ParticipantId, TradingDayId, Balance, HoldingsValue, LoanLiability, MarginLiability, CreatedAt
FROM (
    SELECT s.ParticipantId, mc.TradingDayId, s.Balance, s.HoldingsValue,
           s.LoanLiability, s.MarginLiability, s.CreatedAt,
           ROW_NUMBER() OVER (
               PARTITION BY s.ParticipantId, mc.TradingDayId
               ORDER BY s.CreatedInCycleId DESC) AS rn
    FROM (
        SELECT ParticipantId, CreatedInCycleId, Balance, HoldingsValue, LoanLiability, MarginLiability, CreatedAt
            FROM ParticipantWorthSnapshots
        UNION ALL
        SELECT ParticipantId, CreatedInCycleId, Balance, HoldingsValue, LoanLiability, MarginLiability, CreatedAt
            FROM ParticipantWorthSnapshotArchives
    ) s
    JOIN MarketCycles mc ON mc.Id = s.CreatedInCycleId
    WHERE mc.TradingDayId > 0
) ranked
WHERE rn = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ParticipantDailyWorthSnapshots");
        }
    }
}
