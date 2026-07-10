using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectiveFundMembershipEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CollectiveFundMembershipEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CollectiveFundId = table.Column<int>(type: "INTEGER", nullable: false),
                    FundParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    CreatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectiveFundMembershipEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectiveFundMembershipEvents_FundParticipantId_Id",
                table: "CollectiveFundMembershipEvents",
                columns: new[] { "FundParticipantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectiveFundMembershipEvents_ParticipantId_Id",
                table: "CollectiveFundMembershipEvents",
                columns: new[] { "ParticipantId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectiveFundMembershipEvents");
        }
    }
}
