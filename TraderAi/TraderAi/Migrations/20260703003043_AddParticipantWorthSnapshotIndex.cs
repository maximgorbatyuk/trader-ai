using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddParticipantWorthSnapshotIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ParticipantWorthSnapshots_ParticipantId_CreatedInCycleId",
                table: "ParticipantWorthSnapshots",
                columns: new[] { "ParticipantId", "CreatedInCycleId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ParticipantWorthSnapshots_ParticipantId_CreatedInCycleId",
                table: "ParticipantWorthSnapshots");
        }
    }
}
