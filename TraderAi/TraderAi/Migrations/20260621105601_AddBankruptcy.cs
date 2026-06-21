using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddBankruptcy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BankruptcyDiscountStep",
                table: "Participants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BankruptcyOwnedAtStart",
                table: "Participants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsBankrupt",
                table: "Participants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "WealthyCycles",
                table: "Participants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Bankruptcies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CashLost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ShareWorth = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    TriggeredInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    TriggeredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bankruptcies", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Bankruptcies");

            migrationBuilder.DropColumn(
                name: "BankruptcyDiscountStep",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "BankruptcyOwnedAtStart",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "IsBankrupt",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "WealthyCycles",
                table: "Participants");
        }
    }
}
