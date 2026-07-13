using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddLuldPriceBands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PriceBandStates",
                columns: table => new
                {
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    LimitDirection = table.Column<string>(type: "TEXT", nullable: true),
                    ReferencePrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    LowerBandPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    UpperBandPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    LimitStateStartedCycleNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    PauseUntilCycleNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceBandStates", x => x.CompanyId);
                    table.ForeignKey(
                        name: "FK_PriceBandStates_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Preserve the determinable remainder of a legacy halt; reference bands initialize on the next trading cycle.
            migrationBuilder.Sql(
                "INSERT INTO PriceBandStates " +
                "(CompanyId, State, LimitDirection, ReferencePrice, LowerBandPrice, UpperBandPrice, LimitStateStartedCycleNumber, PauseUntilCycleNumber, UpdatedInCycleId) " +
                "SELECT Companies.Id, " +
                "CASE WHEN TradingHaltedUntilCycleNumber IS NOT NULL " +
                "AND TradingHaltedUntilCycleNumber >= COALESCE((SELECT CycleNumber FROM MarketCycles WHERE Id = (SELECT CurrentCycleId FROM Markets LIMIT 1)), 0) " +
                "THEN 'TradingPause' ELSE 'Normal' END, " +
                "NULL, 0, 0, 0, NULL, TradingHaltedUntilCycleNumber, COALESCE((SELECT CurrentCycleId FROM Markets LIMIT 1), 0) " +
                "FROM Companies");

            migrationBuilder.DropColumn(
                name: "TradingHaltedUntilCycleNumber",
                table: "Companies");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TradingHaltedUntilCycleNumber",
                table: "Companies",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE Companies SET TradingHaltedUntilCycleNumber = " +
                "(SELECT PauseUntilCycleNumber FROM PriceBandStates WHERE PriceBandStates.CompanyId = Companies.Id) " +
                "WHERE EXISTS (SELECT 1 FROM PriceBandStates WHERE PriceBandStates.CompanyId = Companies.Id AND State = 'TradingPause')");

            migrationBuilder.DropTable(
                name: "PriceBandStates");
        }
    }
}
