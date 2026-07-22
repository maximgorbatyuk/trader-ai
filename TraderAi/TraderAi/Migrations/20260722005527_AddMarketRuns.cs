using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketRuns", x => x.Id);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO "MarketRuns" ("StartedAt")
                SELECT COALESCE(MIN("CreatedAt"), CURRENT_TIMESTAMP)
                FROM "Markets";
                """);

            migrationBuilder.AddColumn<int>(
                name: "MarketRunId",
                table: "ShareEmissions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MarketRunId",
                table: "SectorSentimentSnapshotArchives",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MarketRunId",
                table: "PriceSnapshotArchives",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MarketRunId",
                table: "ParticipantWorthSnapshotArchives",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MarketRunId",
                table: "OrderArchives",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MarketRunId",
                table: "MoneyTransactionArchives",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CurrentRunId",
                table: "Markets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "MarketRunId",
                table: "MarketCycles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "MarketRunId",
                table: "CompanyInvestments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MarketRunId",
                table: "AiTraderCalls",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "AiTraderCalls"
                SET "MarketRunId" = 1
                WHERE EXISTS (
                    SELECT 1 FROM "MarketCycles"
                    WHERE "MarketCycles"."Id" = "AiTraderCalls"."SnapshotCycleId");

                UPDATE "CompanyInvestments"
                SET "MarketRunId" = 1
                WHERE EXISTS (
                    SELECT 1 FROM "MarketCycles"
                    WHERE "MarketCycles"."Id" = "CompanyInvestments"."CreatedInCycleId");

                UPDATE "ShareEmissions"
                SET "MarketRunId" = 1
                WHERE EXISTS (
                    SELECT 1 FROM "MarketCycles"
                    WHERE "MarketCycles"."Id" = "ShareEmissions"."CreatedInCycleId");

                UPDATE "OrderArchives"
                SET "MarketRunId" = 1
                WHERE EXISTS (
                    SELECT 1 FROM "MarketCycles"
                    WHERE "MarketCycles"."Id" = "OrderArchives"."CreatedInCycleId");

                UPDATE "PriceSnapshotArchives"
                SET "MarketRunId" = 1
                WHERE EXISTS (
                    SELECT 1 FROM "MarketCycles"
                    WHERE "MarketCycles"."Id" = "PriceSnapshotArchives"."CreatedInCycleId");

                UPDATE "MoneyTransactionArchives"
                SET "MarketRunId" = 1
                WHERE EXISTS (
                    SELECT 1 FROM "MarketCycles"
                    WHERE "MarketCycles"."Id" = "MoneyTransactionArchives"."CreatedInCycleId");

                UPDATE "ParticipantWorthSnapshotArchives"
                SET "MarketRunId" = 1
                WHERE EXISTS (
                    SELECT 1 FROM "MarketCycles"
                    WHERE "MarketCycles"."Id" = "ParticipantWorthSnapshotArchives"."CreatedInCycleId");

                UPDATE "SectorSentimentSnapshotArchives"
                SET "MarketRunId" = 1
                WHERE EXISTS (
                    SELECT 1 FROM "MarketCycles"
                    WHERE "MarketCycles"."Id" = "SectorSentimentSnapshotArchives"."CreatedInCycleId");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ShareEmissions_MarketRunId",
                table: "ShareEmissions",
                column: "MarketRunId");

            migrationBuilder.CreateIndex(
                name: "IX_SectorSentimentSnapshotArchives_MarketRunId",
                table: "SectorSentimentSnapshotArchives",
                column: "MarketRunId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceSnapshotArchives_MarketRunId",
                table: "PriceSnapshotArchives",
                column: "MarketRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantWorthSnapshotArchives_MarketRunId",
                table: "ParticipantWorthSnapshotArchives",
                column: "MarketRunId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderArchives_MarketRunId",
                table: "OrderArchives",
                column: "MarketRunId");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyTransactionArchives_MarketRunId",
                table: "MoneyTransactionArchives",
                column: "MarketRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Markets_CurrentRunId",
                table: "Markets",
                column: "CurrentRunId");

            migrationBuilder.CreateIndex(
                name: "IX_MarketCycles_MarketRunId",
                table: "MarketCycles",
                column: "MarketRunId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyInvestments_MarketRunId",
                table: "CompanyInvestments",
                column: "MarketRunId");

            migrationBuilder.CreateIndex(
                name: "IX_AiTraderCalls_MarketRunId",
                table: "AiTraderCalls",
                column: "MarketRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketRuns");

            migrationBuilder.DropIndex(
                name: "IX_ShareEmissions_MarketRunId",
                table: "ShareEmissions");

            migrationBuilder.DropIndex(
                name: "IX_SectorSentimentSnapshotArchives_MarketRunId",
                table: "SectorSentimentSnapshotArchives");

            migrationBuilder.DropIndex(
                name: "IX_PriceSnapshotArchives_MarketRunId",
                table: "PriceSnapshotArchives");

            migrationBuilder.DropIndex(
                name: "IX_ParticipantWorthSnapshotArchives_MarketRunId",
                table: "ParticipantWorthSnapshotArchives");

            migrationBuilder.DropIndex(
                name: "IX_OrderArchives_MarketRunId",
                table: "OrderArchives");

            migrationBuilder.DropIndex(
                name: "IX_MoneyTransactionArchives_MarketRunId",
                table: "MoneyTransactionArchives");

            migrationBuilder.DropIndex(
                name: "IX_Markets_CurrentRunId",
                table: "Markets");

            migrationBuilder.DropIndex(
                name: "IX_MarketCycles_MarketRunId",
                table: "MarketCycles");

            migrationBuilder.DropIndex(
                name: "IX_CompanyInvestments_MarketRunId",
                table: "CompanyInvestments");

            migrationBuilder.DropIndex(
                name: "IX_AiTraderCalls_MarketRunId",
                table: "AiTraderCalls");

            migrationBuilder.DropColumn(
                name: "MarketRunId",
                table: "ShareEmissions");

            migrationBuilder.DropColumn(
                name: "MarketRunId",
                table: "SectorSentimentSnapshotArchives");

            migrationBuilder.DropColumn(
                name: "MarketRunId",
                table: "PriceSnapshotArchives");

            migrationBuilder.DropColumn(
                name: "MarketRunId",
                table: "ParticipantWorthSnapshotArchives");

            migrationBuilder.DropColumn(
                name: "MarketRunId",
                table: "OrderArchives");

            migrationBuilder.DropColumn(
                name: "MarketRunId",
                table: "MoneyTransactionArchives");

            migrationBuilder.DropColumn(
                name: "CurrentRunId",
                table: "Markets");

            migrationBuilder.DropColumn(
                name: "MarketRunId",
                table: "MarketCycles");

            migrationBuilder.DropColumn(
                name: "MarketRunId",
                table: "CompanyInvestments");

            migrationBuilder.DropColumn(
                name: "MarketRunId",
                table: "AiTraderCalls");
        }
    }
}
