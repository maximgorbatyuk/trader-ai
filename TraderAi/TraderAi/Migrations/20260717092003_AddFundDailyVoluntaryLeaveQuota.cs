using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddFundDailyVoluntaryLeaveQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VoluntaryLeaveQuota",
                table: "CollectiveFunds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "VoluntaryLeavesUsed",
                table: "CollectiveFunds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsIndependentExit",
                table: "CollectiveFundParticipants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VoluntaryLeaveQuota",
                table: "CollectiveFunds");

            migrationBuilder.DropColumn(
                name: "VoluntaryLeavesUsed",
                table: "CollectiveFunds");

            migrationBuilder.DropColumn(
                name: "IsIndependentExit",
                table: "CollectiveFundParticipants");
        }
    }
}
