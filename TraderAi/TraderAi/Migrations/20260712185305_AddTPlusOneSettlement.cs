using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddTPlusOneSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SettledCashBalance",
                table: "Participants",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "SettledQuantity",
                table: "Holdings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE Participants SET SettledCashBalance = CurrentBalance;");
            migrationBuilder.Sql("UPDATE Holdings SET SettledQuantity = Quantity;");

            migrationBuilder.CreateTable(
                name: "SettlementInstructions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShareTransactionId = table.Column<int>(type: "INTEGER", nullable: false),
                    BuyerId = table.Column<int>(type: "INTEGER", nullable: false),
                    SellerId = table.Column<int>(type: "INTEGER", nullable: true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    CashAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    TradeDayNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    DueDayNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    SettledInCycleId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SettledAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettlementInstructions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SettlementInstructions_ShareTransactions_ShareTransactionId",
                        column: x => x.ShareTransactionId,
                        principalTable: "ShareTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SettlementInstructions_BuyerId_Status_DueDayNumber",
                table: "SettlementInstructions",
                columns: new[] { "BuyerId", "Status", "DueDayNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_SettlementInstructions_SellerId_Status_DueDayNumber",
                table: "SettlementInstructions",
                columns: new[] { "SellerId", "Status", "DueDayNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_SettlementInstructions_ShareTransactionId",
                table: "SettlementInstructions",
                column: "ShareTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SettlementInstructions_Status_DueDayNumber",
                table: "SettlementInstructions",
                columns: new[] { "Status", "DueDayNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SettlementInstructions");

            migrationBuilder.DropColumn(
                name: "SettledCashBalance",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "SettledQuantity",
                table: "Holdings");
        }
    }
}
