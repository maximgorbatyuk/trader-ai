using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddFundPopularityAndAdvertising : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastAdvertisedInCycleNumber",
                table: "CollectiveFunds",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PopularityIndex",
                table: "CollectiveFunds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastAdvertisedInCycleNumber",
                table: "CollectiveFunds");

            migrationBuilder.DropColumn(
                name: "PopularityIndex",
                table: "CollectiveFunds");
        }
    }
}
