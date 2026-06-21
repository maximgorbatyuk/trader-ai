using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddCrisis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastGlobalCrisisCycleNumber",
                table: "Markets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastLocalCrisisCycleNumber",
                table: "Markets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Crises",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    TriggeredInCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    TriggeredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Crises", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CrisisIndustries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CrisisId = table.Column<int>(type: "INTEGER", nullable: false),
                    IndustryId = table.Column<int>(type: "INTEGER", nullable: false),
                    ImpactPercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrisisIndustries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrisisIndustries_Crises_CrisisId",
                        column: x => x.CrisisId,
                        principalTable: "Crises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CrisisIndustries_CrisisId",
                table: "CrisisIndustries",
                column: "CrisisId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrisisIndustries");

            migrationBuilder.DropTable(
                name: "Crises");

            migrationBuilder.DropColumn(
                name: "LastGlobalCrisisCycleNumber",
                table: "Markets");

            migrationBuilder.DropColumn(
                name: "LastLocalCrisisCycleNumber",
                table: "Markets");
        }
    }
}
