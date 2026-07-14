using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddAiTraders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiTraderCalls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParticipantName = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProviderLabel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ConfigurationRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotCycleId = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotCycleNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    PromptHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseBody = table.Column<string>(type: "TEXT", nullable: true),
                    DecisionJson = table.Column<string>(type: "TEXT", nullable: true),
                    ApplicationResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiTraderCalls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiTraderConfigurations",
                columns: table => new
                {
                    ParticipantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiTraderConfigurations", x => x.ParticipantId);
                    table.ForeignKey(
                        name: "FK_AiTraderConfigurations_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiTraderCalls_ParticipantId_Id",
                table: "AiTraderCalls",
                columns: new[] { "ParticipantId", "Id" });

            // Placeholder AI agents predate real provider configuration; convert them to Individuals before the
            // new invariant is enforced. Type is persisted as a string, so this matches the stored value, not an int.
            migrationBuilder.Sql("UPDATE Participants SET Type = 'Individual' WHERE Type = 'AIAgent';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiTraderCalls");

            migrationBuilder.DropTable(
                name: "AiTraderConfigurations");
        }
    }
}
