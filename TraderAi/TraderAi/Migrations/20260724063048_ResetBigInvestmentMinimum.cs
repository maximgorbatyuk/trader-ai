using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class ResetBigInvestmentMinimum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Initialization preserves stored settings, so remove only the former shipped default and let startup
            // seed the new 10% value without overwriting a different operator-selected minimum.
            migrationBuilder.Sql(
                """
                DELETE FROM "GameSettings"
                WHERE "Key" = 'RandomChanceRates:RandomMagnitudeBands:BigInvestmentFractionMin'
                  AND CAST("ValueJson" AS REAL) = 0.40;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT OR IGNORE INTO "GameSettings" ("Key", "ValueJson")
                VALUES ('RandomChanceRates:RandomMagnitudeBands:BigInvestmentFractionMin', '0.40');
                """);
        }
    }
}
