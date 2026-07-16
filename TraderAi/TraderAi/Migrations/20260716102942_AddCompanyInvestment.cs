using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyInvestment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyInvestments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    InvestorParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    DealValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    SharesIssued = table.Column<int>(type: "INTEGER", nullable: false),
                    SharesBeforeDeal = table.Column<int>(type: "INTEGER", nullable: false),
                    CapitalizationBeforeDeal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    FinalCapitalization = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    InvestorSharePercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    TradingDayNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyInvestments", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyInvestments");
        }
    }
}
