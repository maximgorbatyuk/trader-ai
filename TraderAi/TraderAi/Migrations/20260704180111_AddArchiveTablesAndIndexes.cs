using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveTablesAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MoneyTransactionArchives",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    RelatedOrderId = table.Column<int>(type: "INTEGER", nullable: true),
                    RelatedShareTransactionId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoneyTransactionArchives", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ParticipantWorthSnapshotArchives",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    Balance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    HoldingsValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantWorthSnapshotArchives", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceSnapshotArchives",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    Price = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Capitalization = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    SourceShareTransactionId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceSnapshotArchives", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriceSnapshots_CompanyId_Id",
                table: "PriceSnapshots",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceSnapshots_CreatedInCycleId",
                table: "PriceSnapshots",
                column: "CreatedInCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantWorthSnapshots_CreatedInCycleId",
                table: "ParticipantWorthSnapshots",
                column: "CreatedInCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ParticipantId_CompanyId_Type_Status",
                table: "Orders",
                columns: new[] { "ParticipantId", "CompanyId", "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status",
                table: "Orders",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyTransactions_CreatedInCycleId",
                table: "MoneyTransactions",
                column: "CreatedInCycleId");

            // One-time backfill: move the existing backlog beyond the default 500-cycle retention window into
            // the archive tables now, so the first cycle after this deploy does not have to move it all at once.
            // Steady-state archiving thereafter uses the configurable Archive:RetentionCycles value.
            const string cutoff =
                "(SELECT COALESCE(MAX(Id), 0) FROM MarketCycles " +
                "WHERE CycleNumber <= (SELECT COALESCE(MAX(CycleNumber), 0) FROM MarketCycles) - 500)";

            migrationBuilder.Sql(
                "INSERT INTO PriceSnapshotArchives (Id, CompanyId, Price, Capitalization, SourceShareTransactionId, CreatedInCycleId, CreatedAt) " +
                "SELECT Id, CompanyId, Price, Capitalization, SourceShareTransactionId, CreatedInCycleId, CreatedAt " +
                $"FROM PriceSnapshots WHERE CreatedInCycleId <= {cutoff};");
            migrationBuilder.Sql($"DELETE FROM PriceSnapshots WHERE CreatedInCycleId <= {cutoff};");

            migrationBuilder.Sql(
                "INSERT INTO MoneyTransactionArchives (Id, ParticipantId, Type, Amount, RelatedOrderId, RelatedShareTransactionId, CreatedInCycleId, CreatedAt) " +
                "SELECT Id, ParticipantId, Type, Amount, RelatedOrderId, RelatedShareTransactionId, CreatedInCycleId, CreatedAt " +
                $"FROM MoneyTransactions WHERE CreatedInCycleId <= {cutoff};");
            migrationBuilder.Sql($"DELETE FROM MoneyTransactions WHERE CreatedInCycleId <= {cutoff};");

            migrationBuilder.Sql(
                "INSERT INTO ParticipantWorthSnapshotArchives (Id, ParticipantId, CreatedInCycleId, Balance, HoldingsValue, CreatedAt) " +
                "SELECT Id, ParticipantId, CreatedInCycleId, Balance, HoldingsValue, CreatedAt " +
                $"FROM ParticipantWorthSnapshots WHERE CreatedInCycleId <= {cutoff};");
            migrationBuilder.Sql($"DELETE FROM ParticipantWorthSnapshots WHERE CreatedInCycleId <= {cutoff};");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MoneyTransactionArchives");

            migrationBuilder.DropTable(
                name: "ParticipantWorthSnapshotArchives");

            migrationBuilder.DropTable(
                name: "PriceSnapshotArchives");

            migrationBuilder.DropIndex(
                name: "IX_PriceSnapshots_CompanyId_Id",
                table: "PriceSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_PriceSnapshots_CreatedInCycleId",
                table: "PriceSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_ParticipantWorthSnapshots_CreatedInCycleId",
                table: "ParticipantWorthSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Orders_ParticipantId_CompanyId_Type_Status",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_Status",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_MoneyTransactions_CreatedInCycleId",
                table: "MoneyTransactions");
        }
    }
}
