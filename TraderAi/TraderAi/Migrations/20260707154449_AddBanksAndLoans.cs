using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddBanksAndLoans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LoanLiability",
                table: "ParticipantWorthSnapshots",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "LoanLiability",
                table: "ParticipantWorthSnapshotArchives",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "RelatedLoanId",
                table: "Orders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RelatedLoanId",
                table: "MoneyTransactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RelatedLoanId",
                table: "MoneyTransactionArchives",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Banks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    InterestRatePerCycle = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Banks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Loans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BankId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Principal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    RemainingPrincipal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    InterestRatePerCycle = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    TermCycles = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduledInstallment = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    PastDueAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    DistressDiscountStep = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    OpenedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClosedInCycleId = table.Column<int>(type: "INTEGER", nullable: true),
                    CloseReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Loans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Loans_Banks_BankId",
                        column: x => x.BankId,
                        principalTable: "Banks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_RelatedLoanId_Status",
                table: "Orders",
                columns: new[] { "RelatedLoanId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MoneyTransactions_RelatedLoanId",
                table: "MoneyTransactions",
                column: "RelatedLoanId");

            migrationBuilder.CreateIndex(
                name: "IX_Loans_BankId_Status",
                table: "Loans",
                columns: new[] { "BankId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Loans_ParticipantId_Status",
                table: "Loans",
                columns: new[] { "ParticipantId", "Status" });

            // Backfill: the old mechanic carried debt as a negative balance. Migrate each such balance into an
            // open loan (shortfall plus the 15% cash buffer, on a fixed 100-cycle term), record a linked
            // disbursement, and leave the borrower with just the buffer so no balance stays negative. On a fresh
            // database there are no negative balances, so every statement is a no-op.
            migrationBuilder.Sql(
                "INSERT INTO Banks (Name, InterestRatePerCycle) " +
                "SELECT 'National bank', 0.001 WHERE NOT EXISTS (SELECT 1 FROM Banks);");

            migrationBuilder.Sql(
                "INSERT INTO Loans (BankId, ParticipantId, Principal, RemainingPrincipal, InterestRatePerCycle, " +
                "TermCycles, ScheduledInstallment, PastDueAmount, DistressDiscountStep, Status, OpenedInCycleId, CreatedAt) " +
                "SELECT (SELECT Id FROM Banks ORDER BY Id LIMIT 1), p.Id, " +
                "ROUND(-p.CurrentBalance * 1.15, 2), ROUND(-p.CurrentBalance * 1.15, 2), 0.001, 100, " +
                "ROUND(ROUND(-p.CurrentBalance * 1.15, 2) / 100.0, 2), 0, 0, 'Open', " +
                "COALESCE((SELECT CurrentCycleId FROM Markets LIMIT 1), 0), datetime('now') " +
                "FROM Participants p WHERE p.CurrentBalance < 0;");

            // Every loan in the table was created by this backfill (the table is new), so each links a disbursement.
            migrationBuilder.Sql(
                "INSERT INTO MoneyTransactions (ParticipantId, Type, Amount, RelatedLoanId, CreatedInCycleId, CreatedAt) " +
                "SELECT l.ParticipantId, 'LoanDisbursement', l.Principal, l.Id, l.OpenedInCycleId, l.CreatedAt FROM Loans l;");

            migrationBuilder.Sql(
                "UPDATE Participants SET CurrentBalance = ROUND(-CurrentBalance * 1.15, 2) + CurrentBalance " +
                "WHERE CurrentBalance < 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Loans");

            migrationBuilder.DropTable(
                name: "Banks");

            migrationBuilder.DropIndex(
                name: "IX_Orders_RelatedLoanId_Status",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_MoneyTransactions_RelatedLoanId",
                table: "MoneyTransactions");

            migrationBuilder.DropColumn(
                name: "LoanLiability",
                table: "ParticipantWorthSnapshots");

            migrationBuilder.DropColumn(
                name: "LoanLiability",
                table: "ParticipantWorthSnapshotArchives");

            migrationBuilder.DropColumn(
                name: "RelatedLoanId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RelatedLoanId",
                table: "MoneyTransactions");

            migrationBuilder.DropColumn(
                name: "RelatedLoanId",
                table: "MoneyTransactionArchives");
        }
    }
}
