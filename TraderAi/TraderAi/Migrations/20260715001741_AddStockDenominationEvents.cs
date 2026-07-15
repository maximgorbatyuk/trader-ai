using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddStockDenominationEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockDenominationEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionType = table.Column<string>(type: "TEXT", nullable: false),
                    Ratio = table.Column<int>(type: "INTEGER", nullable: false),
                    IssuedSharesBefore = table.Column<int>(type: "INTEGER", nullable: false),
                    IssuedSharesAfter = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceBefore = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    PriceAfter = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    LuldState = table.Column<string>(type: "TEXT", nullable: true),
                    LimitDirection = table.Column<string>(type: "TEXT", nullable: true),
                    ReferencePriceBefore = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    ReferencePriceAfter = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    LowerBandPriceBefore = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    LowerBandPriceAfter = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    UpperBandPriceBefore = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    UpperBandPriceAfter = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    LimitStateStartedCycleNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    PauseUntilCycleNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    PreviousPriceBandUpdatedInCycleId = table.Column<int>(type: "INTEGER", nullable: true),
                    EffectiveInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveInCycleNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockDenominationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockDenominationEvents_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockDenominationEvents_MarketCycles_EffectiveInCycleId",
                        column: x => x.EffectiveInCycleId,
                        principalTable: "MarketCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockDenominationEvents_CompanyId_EffectiveInCycleNumber",
                table: "StockDenominationEvents",
                columns: new[] { "CompanyId", "EffectiveInCycleNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockDenominationEvents_EffectiveInCycleId",
                table: "StockDenominationEvents",
                column: "EffectiveInCycleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockDenominationEvents");
        }
    }
}
