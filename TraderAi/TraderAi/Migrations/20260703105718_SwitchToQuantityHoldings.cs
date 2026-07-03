using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class SwitchToQuantityHoldings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Holdings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    AverageCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Holdings", x => x.Id);
                });

            // Preserve existing positions: collapse each owner's per-share rows into one quantity row, with the
            // mean price paid as the weighted-average cost. Runs before the old tables are dropped.
            migrationBuilder.Sql(
                "INSERT INTO Holdings (ParticipantId, CompanyId, Quantity, AverageCost) " +
                "SELECT OwnerId, CompanyId, COUNT(*), ROUND(AVG(CurrentPrice), 2) " +
                "FROM Shares WHERE OwnerId IS NOT NULL GROUP BY OwnerId, CompanyId;");

            migrationBuilder.DropTable(
                name: "OrderShares");

            migrationBuilder.DropTable(
                name: "Shares");

            migrationBuilder.CreateIndex(
                name: "IX_Holdings_CompanyId",
                table: "Holdings",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Holdings_ParticipantId_CompanyId",
                table: "Holdings",
                columns: new[] { "ParticipantId", "CompanyId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Holdings");

            migrationBuilder.CreateTable(
                name: "Shares",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LastShareTransactionId = table.Column<int>(type: "INTEGER", nullable: true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    InitialPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Shares_ShareTransactions_LastShareTransactionId",
                        column: x => x.LastShareTransactionId,
                        principalTable: "ShareTransactions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OrderShares",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    ShareId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderShares_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrderShares_Shares_ShareId",
                        column: x => x.ShareId,
                        principalTable: "Shares",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderShares_OrderId",
                table: "OrderShares",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderShares_ShareId",
                table: "OrderShares",
                column: "ShareId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shares_LastShareTransactionId",
                table: "Shares",
                column: "LastShareTransactionId");
        }
    }
}
