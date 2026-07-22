using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddAiPredictions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiPredictions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AiTraderCallId = table.Column<long>(type: "INTEGER", nullable: false),
                    MarketRunId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotCycleNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotTradingDayNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    BaselinePrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    HorizonCycles = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiPredictions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiPredictions_AiTraderCalls_AiTraderCallId",
                        column: x => x.AiTraderCallId,
                        principalTable: "AiTraderCalls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiPredictions_AiTraderCallId_CompanyId_HorizonCycles",
                table: "AiPredictions",
                columns: new[] { "AiTraderCallId", "CompanyId", "HorizonCycles" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiPredictions_MarketRunId_SnapshotCycleNumber",
                table: "AiPredictions",
                columns: new[] { "MarketRunId", "SnapshotCycleNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiPredictions");
        }
    }
}
