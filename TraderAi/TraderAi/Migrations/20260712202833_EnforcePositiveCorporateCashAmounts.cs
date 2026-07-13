using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class EnforcePositiveCorporateCashAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_CorporateCashTransactions_Amount_Positive",
                table: "CorporateCashTransactions",
                sql: "CAST(Amount AS NUMERIC) > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CorporateCashTransactions_Amount_Positive",
                table: "CorporateCashTransactions");
        }
    }
}
