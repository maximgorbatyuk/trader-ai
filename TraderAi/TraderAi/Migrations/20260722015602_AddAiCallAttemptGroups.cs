using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddAiCallAttemptGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AttemptGroupId",
                table: "AiTraderCalls",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "AttemptNumber",
                table: "AiTraderCalls",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FailureCategory",
                table: "AiTraderCalls",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE AiTraderCalls
                SET AttemptGroupId = lower(hex(randomblob(4))) || '-' ||
                    lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))), 2) || '-' ||
                    '8' || substr(lower(hex(randomblob(2))), 2) || '-' ||
                    lower(hex(randomblob(6))),
                    AttemptNumber = 1
                WHERE AttemptGroupId = '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AiTraderCalls_AttemptGroupId_AttemptNumber",
                table: "AiTraderCalls",
                columns: new[] { "AttemptGroupId", "AttemptNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiTraderCalls_AttemptGroupId_AttemptNumber",
                table: "AiTraderCalls");

            migrationBuilder.DropColumn(
                name: "AttemptGroupId",
                table: "AiTraderCalls");

            migrationBuilder.DropColumn(
                name: "AttemptNumber",
                table: "AiTraderCalls");

            migrationBuilder.DropColumn(
                name: "FailureCategory",
                table: "AiTraderCalls");
        }
    }
}
