using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastCompanyAppearanceCycleNumber",
                table: "Markets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                table: "Companies",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClosedInCycleId",
                table: "Companies",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedInCycleId",
                table: "Companies",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCompanyAppearanceCycleNumber",
                table: "Markets");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "ClosedInCycleId",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "CreatedInCycleId",
                table: "Companies");
        }
    }
}
