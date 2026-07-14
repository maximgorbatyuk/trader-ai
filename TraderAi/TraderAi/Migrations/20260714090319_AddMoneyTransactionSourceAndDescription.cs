using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddMoneyTransactionSourceAndDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "MoneyTransactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FromWhomId",
                table: "MoneyTransactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "MoneyTransactionArchives",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FromWhomId",
                table: "MoneyTransactionArchives",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                table: "MoneyTransactions");

            migrationBuilder.DropColumn(
                name: "FromWhomId",
                table: "MoneyTransactions");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "MoneyTransactionArchives");

            migrationBuilder.DropColumn(
                name: "FromWhomId",
                table: "MoneyTransactionArchives");
        }
    }
}
