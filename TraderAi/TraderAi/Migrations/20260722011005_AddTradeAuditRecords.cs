using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddTradeAuditRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SellerAverageCost",
                table: "ShareTransactions",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SellerCostBasis",
                table: "ShareTransactions",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SellerGrossRealizedPnl",
                table: "ShareTransactions",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SellerManagerFee",
                table: "ShareTransactions",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SellerNetRealizedPnl",
                table: "ShareTransactions",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SellerTradeFee",
                table: "ShareTransactions",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SellerAverageCost",
                table: "ShareTransactions");

            migrationBuilder.DropColumn(
                name: "SellerCostBasis",
                table: "ShareTransactions");

            migrationBuilder.DropColumn(
                name: "SellerGrossRealizedPnl",
                table: "ShareTransactions");

            migrationBuilder.DropColumn(
                name: "SellerManagerFee",
                table: "ShareTransactions");

            migrationBuilder.DropColumn(
                name: "SellerNetRealizedPnl",
                table: "ShareTransactions");

            migrationBuilder.DropColumn(
                name: "SellerTradeFee",
                table: "ShareTransactions");
        }
    }
}
