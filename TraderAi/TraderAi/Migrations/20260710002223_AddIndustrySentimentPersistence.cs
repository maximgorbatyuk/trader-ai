using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddIndustrySentimentPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ImpactAppliedInCycleId",
                table: "NewsPosts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SectorBeta",
                table: "Industries",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<int>(
                name: "SentimentValue",
                table: "Industries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "SentimentVolatility",
                table: "Industries",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "SectorSentimentSnapshotArchives",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IndustryId = table.Column<int>(type: "INTEGER", nullable: false),
                    SentimentValue = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SectorSentimentSnapshotArchives", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SectorSentimentSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IndustryId = table.Column<int>(type: "INTEGER", nullable: false),
                    SentimentValue = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SectorSentimentSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SectorSentimentSnapshots_Industries_IndustryId",
                        column: x => x.IndustryId,
                        principalTable: "Industries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SectorSentimentSnapshots_MarketCycles_CreatedInCycleId",
                        column: x => x.CreatedInCycleId,
                        principalTable: "MarketCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SectorSentimentSnapshotArchives_CreatedInCycleId",
                table: "SectorSentimentSnapshotArchives",
                column: "CreatedInCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_SectorSentimentSnapshots_CreatedInCycleId",
                table: "SectorSentimentSnapshots",
                column: "CreatedInCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_SectorSentimentSnapshots_IndustryId",
                table: "SectorSentimentSnapshots",
                column: "IndustryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SectorSentimentSnapshotArchives");

            migrationBuilder.DropTable(
                name: "SectorSentimentSnapshots");

            migrationBuilder.DropColumn(
                name: "ImpactAppliedInCycleId",
                table: "NewsPosts");

            migrationBuilder.DropColumn(
                name: "SectorBeta",
                table: "Industries");

            migrationBuilder.DropColumn(
                name: "SentimentValue",
                table: "Industries");

            migrationBuilder.DropColumn(
                name: "SentimentVolatility",
                table: "Industries");
        }
    }
}
