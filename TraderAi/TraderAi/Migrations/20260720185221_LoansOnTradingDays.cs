using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class LoansOnTradingDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TermCycles",
                table: "Loans",
                newName: "TermTradingDays");

            migrationBuilder.RenameColumn(
                name: "InterestRatePerCycle",
                table: "Loans",
                newName: "InterestRate");

            migrationBuilder.RenameColumn(
                name: "InterestRatePerCycle",
                table: "Banks",
                newName: "InterestRate");

            migrationBuilder.AddColumn<int>(
                name: "LastServicedTradingDayId",
                table: "Loans",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OpenedInTradingDayId",
                table: "Loans",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "RemainingInterest",
                table: "Loans",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Bring any pre-existing loans onto the trading-day model: stamp the opening trading day, cap the
            // renamed term (old cycle counts read as trading days would be absurd), and seed the fixed 10%
            // interest and the per-day principal installment. Status 0 is an open loan; closed loans are inert.
            migrationBuilder.Sql(
                "UPDATE Loans SET OpenedInTradingDayId = COALESCE(" +
                "(SELECT TradingDayId FROM MarketCycles WHERE MarketCycles.Id = Loans.OpenedInCycleId), 0);");
            migrationBuilder.Sql(
                "UPDATE Loans SET TermTradingDays = 20 WHERE Status = 0 AND TermTradingDays > 20;");
            migrationBuilder.Sql(
                "UPDATE Loans SET RemainingInterest = ROUND(Principal * 0.10, 2), " +
                "ScheduledInstallment = ROUND(Principal / TermTradingDays, 2) " +
                "WHERE Status = 0 AND TermTradingDays > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastServicedTradingDayId",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "OpenedInTradingDayId",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "RemainingInterest",
                table: "Loans");

            migrationBuilder.RenameColumn(
                name: "TermTradingDays",
                table: "Loans",
                newName: "TermCycles");

            migrationBuilder.RenameColumn(
                name: "InterestRate",
                table: "Loans",
                newName: "InterestRatePerCycle");

            migrationBuilder.RenameColumn(
                name: "InterestRate",
                table: "Banks",
                newName: "InterestRatePerCycle");
        }
    }
}
