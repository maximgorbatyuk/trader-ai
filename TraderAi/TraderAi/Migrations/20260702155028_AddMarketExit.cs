using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketExit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "JoinedInCycleId",
                table: "Participants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxTotalWorth",
                table: "Participants",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "PendingFundLossExitRoll",
                table: "Participants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "IdleCycles",
                table: "CollectiveFunds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MarketExits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    JoinedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    LeftInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    OrdersPlaced = table.Column<int>(type: "INTEGER", nullable: false),
                    InitialBalance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    MaxTotalWorth = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    QuitBalance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    LeftAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketExits", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketExits");

            migrationBuilder.DropColumn(
                name: "JoinedInCycleId",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "MaxTotalWorth",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "PendingFundLossExitRoll",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "IdleCycles",
                table: "CollectiveFunds");
        }
    }
}
