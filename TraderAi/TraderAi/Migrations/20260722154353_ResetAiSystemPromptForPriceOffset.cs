using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class ResetAiSystemPromptForPriceOffset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The stored prompt still asks for an absolute limitPrice, which no longer matches the decision contract.
            // Seeding only inserts missing keys, so deleting the row lets startup re-seed it from the current code
            // default, which now describes the price-offset field.
            migrationBuilder.Sql("DELETE FROM \"GameSettings\" WHERE \"Key\" = 'AiTrading:SystemPromptTemplate';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The prior value cannot be restored; startup re-seeds the row from the code default on the next launch.
        }
    }
}
