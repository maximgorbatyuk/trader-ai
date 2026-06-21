using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddScienceInvestigation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastScienceInvestigationCycleNumber",
                table: "Markets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ScienceInvestigations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    TriggeredInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    TriggeredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScienceInvestigations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScienceInvestigationIndustries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScienceInvestigationId = table.Column<int>(type: "INTEGER", nullable: false),
                    IndustryId = table.Column<int>(type: "INTEGER", nullable: false),
                    ImpactPercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScienceInvestigationIndustries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScienceInvestigationIndustries_ScienceInvestigations_ScienceInvestigationId",
                        column: x => x.ScienceInvestigationId,
                        principalTable: "ScienceInvestigations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScienceInvestigationIndustries_ScienceInvestigationId",
                table: "ScienceInvestigationIndustries",
                column: "ScienceInvestigationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScienceInvestigationIndustries");

            migrationBuilder.DropTable(
                name: "ScienceInvestigations");

            migrationBuilder.DropColumn(
                name: "LastScienceInvestigationCycleNumber",
                table: "Markets");
        }
    }
}
