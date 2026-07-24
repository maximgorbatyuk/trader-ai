using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyFundamentalsAndAudits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rebuilding before dependent tables exist keeps the new constraints and legacy normalization in the
            // migration transaction; SQLite's generated AddForeignKey operation would disable foreign keys outside it.
            migrationBuilder.Sql(
                """
                CREATE TABLE "__CompanyRatings_new" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_CompanyRatings" PRIMARY KEY AUTOINCREMENT,
                    "CompanyId" INTEGER NOT NULL,
                    "AuditorId" INTEGER NOT NULL,
                    "Rating" TEXT NOT NULL,
                    "ImpactPercent" TEXT NULL,
                    "CreatedInCycleId" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    CONSTRAINT "AK_CompanyRatings_Id_CompanyId" UNIQUE ("Id", "CompanyId"),
                    CONSTRAINT "FK_CompanyRatings_Auditors_AuditorId"
                        FOREIGN KEY ("AuditorId") REFERENCES "Auditors" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_CompanyRatings_Companies_CompanyId"
                        FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT
                );

                INSERT INTO "__CompanyRatings_new" (
                    "Id", "CompanyId", "AuditorId", "Rating", "ImpactPercent", "CreatedInCycleId", "CreatedAt")
                SELECT
                    "Id",
                    "CompanyId",
                    "AuditorId",
                    CASE
                        WHEN "Rating" IN ('Low', '0') THEN 'LowRisk'
                        WHEN "Rating" IN ('High', 'Extra', '1', '2') THEN 'HighRisk'
                        ELSE "Rating"
                    END,
                    "ImpactPercent",
                    "CreatedInCycleId",
                    "CreatedAt"
                FROM "CompanyRatings";

                DROP TABLE "CompanyRatings";
                ALTER TABLE "__CompanyRatings_new" RENAME TO "CompanyRatings";

                CREATE INDEX "IX_CompanyRatings_AuditorId" ON "CompanyRatings" ("AuditorId");
                CREATE INDEX "IX_CompanyRatings_CompanyId_CreatedInCycleId"
                    ON "CompanyRatings" ("CompanyId", "CreatedInCycleId");
                """);

            migrationBuilder.CreateTable(
                name: "CompanyDividendEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeclaredAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    FundedAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    FundingOutcome = table.Column<string>(type: "TEXT", nullable: false),
                    IssuerCashBeforeFunding = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    CreatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    TradingDayNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyDividendEvents", x => x.Id);
                    table.UniqueConstraint("AK_CompanyDividendEvents_Id_CompanyId", x => new { x.Id, x.CompanyId });
                    table.CheckConstraint("CK_CompanyDividendEvents_Amounts_NonNegative", "CAST(DeclaredAmount AS NUMERIC) >= 0 AND CAST(FundedAmount AS NUMERIC) >= 0 AND CAST(IssuerCashBeforeFunding AS NUMERIC) >= 0");
                    table.CheckConstraint("CK_CompanyDividendEvents_FundedNotAboveDeclared", "CAST(FundedAmount AS NUMERIC) <= CAST(DeclaredAmount AS NUMERIC)");
                    table.ForeignKey(
                        name: "FK_CompanyDividendEvents_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyDividendEvents_MarketCycles_CreatedInCycleId",
                        column: x => x.CreatedInCycleId,
                        principalTable: "MarketCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PortfolioAuditSummaries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NewsPostId = table.Column<int>(type: "INTEGER", nullable: false),
                    EvaluationStartTradingDayNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EvaluationEndTradingDayNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveTradingDayNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ExtraRaisedExpectationsCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RaisedExpectationsCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StableCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LowRiskCount = table.Column<int>(type: "INTEGER", nullable: false),
                    HighRiskCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AverageScore = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    OverallDirection = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioAuditSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortfolioAuditSummaries_NewsPosts_NewsPostId",
                        column: x => x.NewsPostId,
                        principalTable: "NewsPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PrimaryIssuanceEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    IssuedSharesBefore = table.Column<int>(type: "INTEGER", nullable: false),
                    NewlyIssuedShares = table.Column<int>(type: "INTEGER", nullable: false),
                    IssuedSharesAfter = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrimaryIssuanceEvents", x => x.Id);
                    table.CheckConstraint("CK_PrimaryIssuanceEvents_Counts_Coherent", "IssuedSharesBefore + NewlyIssuedShares = IssuedSharesAfter");
                    table.CheckConstraint("CK_PrimaryIssuanceEvents_Counts_Positive", "IssuedSharesBefore > 0 AND NewlyIssuedShares > 0 AND IssuedSharesAfter > 0");
                    table.ForeignKey(
                        name: "FK_PrimaryIssuanceEvents_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrimaryIssuanceEvents_MarketCycles_CreatedInCycleId",
                        column: x => x.CreatedInCycleId,
                        principalTable: "MarketCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CompanyFinancialSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    TradingDayNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Moment = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Revenue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    NetProfit = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    OperatingCashFlow = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    TotalAssets = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    TotalLiabilities = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    TotalDebt = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ExpectedDividendPerShare = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    ExpectedDividendPool = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    DividendCoverageRatio = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    LatestDividendEventId = table.Column<int>(type: "INTEGER", nullable: true),
                    BusinessRiskScore = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    ManagementRevenueForecast = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ManagementProfitForecast = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ManagementOperatingCashFlowForecast = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ManagementOutlook = table.Column<int>(type: "INTEGER", nullable: false),
                    ManagementConfidenceScore = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    ProfitabilityScore = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    ProfitabilityLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    StabilityScore = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    FinancialVolatilityLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    ClosureRiskScore = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    ClosureRiskLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    ChangedMetrics = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyFinancialSnapshots", x => x.Id);
                    table.UniqueConstraint("AK_CompanyFinancialSnapshots_Id_CompanyId", x => new { x.Id, x.CompanyId });
                    table.CheckConstraint("CK_CompanyFinancialSnapshots_ChangedMetrics_Valid", "ChangedMetrics >= 0 AND (ChangedMetrics & ~4095) = 0");
                    table.CheckConstraint("CK_CompanyFinancialSnapshots_DebtWithinLiabilities", "CAST(TotalDebt AS NUMERIC) <= CAST(TotalLiabilities AS NUMERIC)");
                    table.CheckConstraint("CK_CompanyFinancialSnapshots_Levels_Valid", "ProfitabilityLevel IN (0, 1, 2) AND FinancialVolatilityLevel IN (0, 1, 2) AND ClosureRiskLevel IN (0, 1, 2)");
                    table.CheckConstraint("CK_CompanyFinancialSnapshots_ManagementOutlook_Valid", "ManagementOutlook IN (0, 1, 2)");
                    table.CheckConstraint("CK_CompanyFinancialSnapshots_Moment_Valid", "Moment IN (0, 1, 2)");
                    table.CheckConstraint("CK_CompanyFinancialSnapshots_NonNegativeValues", "CAST(Revenue AS NUMERIC) >= 0 AND CAST(TotalAssets AS NUMERIC) >= 0 AND CAST(TotalLiabilities AS NUMERIC) >= 0 AND CAST(TotalDebt AS NUMERIC) >= 0 AND CAST(ExpectedDividendPerShare AS NUMERIC) >= 0 AND CAST(ExpectedDividendPool AS NUMERIC) >= 0 AND CAST(DividendCoverageRatio AS NUMERIC) >= 0 AND CAST(BusinessRiskScore AS NUMERIC) >= 0 AND CAST(ManagementRevenueForecast AS NUMERIC) >= 0");
                    table.CheckConstraint("CK_CompanyFinancialSnapshots_ScoresInRange", "CAST(BusinessRiskScore AS NUMERIC) <= 100 AND CAST(ManagementConfidenceScore AS NUMERIC) >= 0 AND CAST(ManagementConfidenceScore AS NUMERIC) <= 100 AND CAST(ProfitabilityScore AS NUMERIC) >= 0 AND CAST(ProfitabilityScore AS NUMERIC) <= 100 AND CAST(StabilityScore AS NUMERIC) >= 0 AND CAST(StabilityScore AS NUMERIC) <= 100 AND CAST(ClosureRiskScore AS NUMERIC) >= 0 AND CAST(ClosureRiskScore AS NUMERIC) <= 100");
                    table.CheckConstraint("CK_CompanyFinancialSnapshots_TradingDay_Positive", "TradingDayNumber > 0");
                    table.ForeignKey(
                        name: "FK_CompanyFinancialSnapshots_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyFinancialSnapshots_CompanyDividendEvents_LatestDividendEventId_CompanyId",
                        columns: x => new { x.LatestDividendEventId, x.CompanyId },
                        principalTable: "CompanyDividendEvents",
                        principalColumns: new[] { "Id", "CompanyId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyFinancialSnapshots_MarketCycles_CreatedInCycleId",
                        column: x => x.CreatedInCycleId,
                        principalTable: "MarketCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PortfolioAuditSummaryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PortfolioAuditSummaryId = table.Column<int>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    CompanyRatingId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayerQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    ManagedFundQuantity = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioAuditSummaryItems", x => x.Id);
                    table.CheckConstraint("CK_PortfolioAuditSummaryItems_Quantities_NonNegative", "PlayerQuantity >= 0 AND ManagedFundQuantity >= 0");
                    table.ForeignKey(
                        name: "FK_PortfolioAuditSummaryItems_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PortfolioAuditSummaryItems_CompanyRatings_CompanyRatingId_CompanyId",
                        columns: x => new { x.CompanyRatingId, x.CompanyId },
                        principalTable: "CompanyRatings",
                        principalColumns: new[] { "Id", "CompanyId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PortfolioAuditSummaryItems_PortfolioAuditSummaries_PortfolioAuditSummaryId",
                        column: x => x.PortfolioAuditSummaryId,
                        principalTable: "PortfolioAuditSummaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CompanyAuditEvidence",
                columns: table => new
                {
                    CompanyRatingId = table.Column<int>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    CompanyFinancialSnapshotId = table.Column<int>(type: "INTEGER", nullable: true),
                    EvaluationStartTradingDayNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EvaluationEndTradingDayNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveTradingDayNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalScore = table.Column<int>(type: "INTEGER", nullable: false),
                    AdjustedReturnScore = table.Column<int>(type: "INTEGER", nullable: false),
                    CycleJumpScore = table.Column<int>(type: "INTEGER", nullable: false),
                    FreeShareEmissionScore = table.Column<int>(type: "INTEGER", nullable: false),
                    DenominationScore = table.Column<int>(type: "INTEGER", nullable: false),
                    DividendOutcomeScore = table.Column<int>(type: "INTEGER", nullable: false),
                    DividendCoverageScore = table.Column<int>(type: "INTEGER", nullable: false),
                    IndustryScore = table.Column<int>(type: "INTEGER", nullable: false),
                    ProfitabilityFactorScore = table.Column<int>(type: "INTEGER", nullable: false),
                    StabilityFactorScore = table.Column<int>(type: "INTEGER", nullable: false),
                    ClosureRiskFactorScore = table.Column<int>(type: "INTEGER", nullable: false),
                    ManagementOutlookFactorScore = table.Column<int>(type: "INTEGER", nullable: false),
                    StartPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    EndPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    AdjustedReturnPercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    MaximumAdjustedCycleMovePercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    OpeningIssuedShares = table.Column<int>(type: "INTEGER", nullable: false),
                    EmittedShares = table.Column<int>(type: "INTEGER", nullable: false),
                    FreeShareDilutionPercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    StockSplitCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ReverseSplitCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LatestDividendEventId = table.Column<int>(type: "INTEGER", nullable: true),
                    IssuerCash = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ModeledMaximumDividend = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    DividendCoverageRatio = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    OpeningIndustrySentiment = table.Column<int>(type: "INTEGER", nullable: true),
                    ClosingIndustrySentiment = table.Column<int>(type: "INTEGER", nullable: true),
                    IndustryTrend = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyAuditEvidence", x => x.CompanyRatingId);
                    table.ForeignKey(
                        name: "FK_CompanyAuditEvidence_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyAuditEvidence_CompanyDividendEvents_LatestDividendEventId_CompanyId",
                        columns: x => new { x.LatestDividendEventId, x.CompanyId },
                        principalTable: "CompanyDividendEvents",
                        principalColumns: new[] { "Id", "CompanyId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyAuditEvidence_CompanyFinancialSnapshots_CompanyFinancialSnapshotId_CompanyId",
                        columns: x => new { x.CompanyFinancialSnapshotId, x.CompanyId },
                        principalTable: "CompanyFinancialSnapshots",
                        principalColumns: new[] { "Id", "CompanyId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyAuditEvidence_CompanyRatings_CompanyRatingId_CompanyId",
                        columns: x => new { x.CompanyRatingId, x.CompanyId },
                        principalTable: "CompanyRatings",
                        principalColumns: new[] { "Id", "CompanyId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyAuditEvidence_CompanyFinancialSnapshotId_CompanyId",
                table: "CompanyAuditEvidence",
                columns: new[] { "CompanyFinancialSnapshotId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyAuditEvidence_CompanyId_EffectiveTradingDayNumber",
                table: "CompanyAuditEvidence",
                columns: new[] { "CompanyId", "EffectiveTradingDayNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyAuditEvidence_CompanyRatingId_CompanyId",
                table: "CompanyAuditEvidence",
                columns: new[] { "CompanyRatingId", "CompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyAuditEvidence_LatestDividendEventId_CompanyId",
                table: "CompanyAuditEvidence",
                columns: new[] { "LatestDividendEventId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyDividendEvents_CompanyId_TradingDayNumber_Id",
                table: "CompanyDividendEvents",
                columns: new[] { "CompanyId", "TradingDayNumber", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyDividendEvents_CreatedInCycleId",
                table: "CompanyDividendEvents",
                column: "CreatedInCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyFinancialSnapshots_CompanyId_CreatedInCycleId",
                table: "CompanyFinancialSnapshots",
                columns: new[] { "CompanyId", "CreatedInCycleId" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyFinancialSnapshots_CompanyId_TradingDayNumber_Moment",
                table: "CompanyFinancialSnapshots",
                columns: new[] { "CompanyId", "TradingDayNumber", "Moment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyFinancialSnapshots_CreatedInCycleId",
                table: "CompanyFinancialSnapshots",
                column: "CreatedInCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyFinancialSnapshots_LatestDividendEventId_CompanyId",
                table: "CompanyFinancialSnapshots",
                columns: new[] { "LatestDividendEventId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioAuditSummaries_NewsPostId",
                table: "PortfolioAuditSummaries",
                column: "NewsPostId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioAuditSummaryItems_CompanyId",
                table: "PortfolioAuditSummaryItems",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioAuditSummaryItems_CompanyRatingId_CompanyId",
                table: "PortfolioAuditSummaryItems",
                columns: new[] { "CompanyRatingId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioAuditSummaryItems_PortfolioAuditSummaryId_CompanyId",
                table: "PortfolioAuditSummaryItems",
                columns: new[] { "PortfolioAuditSummaryId", "CompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryIssuanceEvents_CompanyId_CreatedInCycleId",
                table: "PrimaryIssuanceEvents",
                columns: new[] { "CompanyId", "CreatedInCycleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryIssuanceEvents_CreatedInCycleId",
                table: "PrimaryIssuanceEvents",
                column: "CreatedInCycleId");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyAuditEvidence");

            migrationBuilder.DropTable(
                name: "PortfolioAuditSummaryItems");

            migrationBuilder.DropTable(
                name: "PrimaryIssuanceEvents");

            migrationBuilder.DropTable(
                name: "CompanyFinancialSnapshots");

            migrationBuilder.DropTable(
                name: "PortfolioAuditSummaries");

            migrationBuilder.DropTable(
                name: "CompanyDividendEvents");

            // All rating dependents are gone, so the predecessor schema can be restored without disabling foreign keys.
            migrationBuilder.Sql(
                """
                CREATE TABLE "__CompanyRatings_old" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_CompanyRatings" PRIMARY KEY AUTOINCREMENT,
                    "CompanyId" INTEGER NOT NULL,
                    "AuditorId" INTEGER NOT NULL,
                    "Rating" TEXT NOT NULL,
                    "ImpactPercent" TEXT NULL,
                    "CreatedInCycleId" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL
                );

                INSERT INTO "__CompanyRatings_old" (
                    "Id", "CompanyId", "AuditorId", "Rating", "ImpactPercent", "CreatedInCycleId", "CreatedAt")
                SELECT
                    "Id", "CompanyId", "AuditorId", "Rating", "ImpactPercent", "CreatedInCycleId", "CreatedAt"
                FROM "CompanyRatings";

                DROP TABLE "CompanyRatings";
                ALTER TABLE "__CompanyRatings_old" RENAME TO "CompanyRatings";

                CREATE INDEX "IX_CompanyRatings_CompanyId_CreatedInCycleId"
                    ON "CompanyRatings" ("CompanyId", "CreatedInCycleId");
                """);
        }
    }
}
