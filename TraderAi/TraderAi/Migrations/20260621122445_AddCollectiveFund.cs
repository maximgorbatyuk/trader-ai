using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectiveFund : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CannotBuyCycles",
                table: "Participants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CollectiveFunds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    FoundedByParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectiveFunds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CollectiveFundParticipants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CollectiveFundId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    JoinedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    DepositAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    LeaveRampCycles = table.Column<int>(type: "INTEGER", nullable: false),
                    IsLeaving = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectiveFundParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CollectiveFundParticipants_CollectiveFunds_CollectiveFundId",
                        column: x => x.CollectiveFundId,
                        principalTable: "CollectiveFunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectiveFundParticipants_CollectiveFundId",
                table: "CollectiveFundParticipants",
                column: "CollectiveFundId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectiveFundParticipants");

            migrationBuilder.DropTable(
                name: "CollectiveFunds");

            migrationBuilder.DropColumn(
                name: "CannotBuyCycles",
                table: "Participants");
        }
    }
}
