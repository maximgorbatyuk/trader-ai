using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddAiNextDayPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxDecisionsPerDay",
                table: "AiTraderConfigurations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "NextDayTargetDayNumber",
                table: "AiTraderCalls",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxDecisionsPerDay",
                table: "AiTraderConfigurations");

            migrationBuilder.DropColumn(
                name: "NextDayTargetDayNumber",
                table: "AiTraderCalls");
        }
    }
}
