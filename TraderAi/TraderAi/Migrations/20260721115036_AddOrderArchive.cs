using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderArchives",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    FilledQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    LimitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ReservedCashAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    IsFloatReplenishment = table.Column<bool>(type: "INTEGER", nullable: false),
                    RelatedLoanId = table.Column<int>(type: "INTEGER", nullable: true),
                    RelatedMarginCallId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderArchives", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CreatedInCycleId",
                table: "Orders",
                column: "CreatedInCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderArchives_ParticipantId",
                table: "OrderArchives",
                column: "ParticipantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderArchives");

            migrationBuilder.DropIndex(
                name: "IX_Orders_CreatedInCycleId",
                table: "Orders");
        }
    }
}
