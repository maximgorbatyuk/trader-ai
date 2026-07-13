using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddMarginAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BuyerMarginAdvance",
                table: "SettlementInstructions",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SellerMarginDebitRepayment",
                table: "SettlementInstructions",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SellerMarginInterestPayment",
                table: "SettlementInstructions",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MarginLiability",
                table: "ParticipantWorthSnapshots",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MarginLiability",
                table: "ParticipantWorthSnapshotArchives",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "RelatedMarginCallId",
                table: "Orders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MarginAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    DebitBalance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    AccruedInterest = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    InitialMarginRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    MaintenanceMarginRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    LastInterestAccruedTradingDayId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarginAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarginCalls",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MarginAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenedInTradingDayId = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClosedInTradingDayId = table.Column<int>(type: "INTEGER", nullable: true),
                    AccountEquity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    MaintenanceRequirement = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Deficiency = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarginCalls", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_RelatedMarginCallId_Status",
                table: "Orders",
                columns: new[] { "RelatedMarginCallId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MarginAccounts_ParticipantId",
                table: "MarginAccounts",
                column: "ParticipantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarginCalls_MarginAccountId_Status",
                table: "MarginCalls",
                columns: new[] { "MarginAccountId", "Status" });

            migrationBuilder.Sql("""
                INSERT INTO MarginAccounts
                    (ParticipantId, DebitBalance, AccruedInterest, InitialMarginRate, MaintenanceMarginRate, Status, LastInterestAccruedTradingDayId)
                SELECT Id, 0, 0, 0.50, 0.25, 'Active', (SELECT CurrentTradingDayId FROM Markets LIMIT 1)
                FROM Participants
                WHERE IsActive = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarginAccounts");

            migrationBuilder.DropTable(
                name: "MarginCalls");

            migrationBuilder.DropIndex(
                name: "IX_Orders_RelatedMarginCallId_Status",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "BuyerMarginAdvance",
                table: "SettlementInstructions");

            migrationBuilder.DropColumn(
                name: "SellerMarginDebitRepayment",
                table: "SettlementInstructions");

            migrationBuilder.DropColumn(
                name: "SellerMarginInterestPayment",
                table: "SettlementInstructions");

            migrationBuilder.DropColumn(
                name: "MarginLiability",
                table: "ParticipantWorthSnapshots");

            migrationBuilder.DropColumn(
                name: "MarginLiability",
                table: "ParticipantWorthSnapshotArchives");

            migrationBuilder.DropColumn(
                name: "RelatedMarginCallId",
                table: "Orders");
        }
    }
}
