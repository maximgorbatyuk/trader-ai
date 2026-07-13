using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class CorrectLoanArrears : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AccruedFees",
                table: "Loans",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PastDuePrincipal",
                table: "Loans",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Legacy arrears cannot be decomposed exactly, so preserving the aggregate as fees avoids forgiving debt.
            // Resetting the local demo market is the exact migration path for active legacy loans.
            migrationBuilder.Sql("UPDATE Loans SET AccruedFees = PastDueAmount");

            migrationBuilder.DropColumn(
                name: "PastDueAmount",
                table: "Loans");

            migrationBuilder.AddColumn<decimal>(
                name: "PastDueInterest",
                table: "Loans",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PastDueAmount",
                table: "Loans",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(
                "UPDATE Loans SET PastDueAmount = PastDuePrincipal + PastDueInterest + AccruedFees");

            migrationBuilder.DropColumn(
                name: "AccruedFees",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "PastDueInterest",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "PastDuePrincipal",
                table: "Loans");
        }
    }
}
