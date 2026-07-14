using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddAiCallSummaryColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AppliedOrders",
                table: "AiTraderCalls",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RejectedOrders",
                table: "AiTraderCalls",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "AiTraderCalls",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppliedOrders",
                table: "AiTraderCalls");

            migrationBuilder.DropColumn(
                name: "RejectedOrders",
                table: "AiTraderCalls");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "AiTraderCalls");
        }
    }
}
