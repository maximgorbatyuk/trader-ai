using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddFundSwitchingAndPeakWorth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PendingFundSwitch",
                table: "Participants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "PeakNetWorth",
                table: "CollectiveFunds",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "TenureCycles",
                table: "CollectiveFundParticipants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingFundSwitch",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "PeakNetWorth",
                table: "CollectiveFunds");

            migrationBuilder.DropColumn(
                name: "TenureCycles",
                table: "CollectiveFundParticipants");
        }
    }
}
