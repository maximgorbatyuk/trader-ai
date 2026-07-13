using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class RestoreCurrentPriceAnchors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO PriceSnapshots
                    (Id, CompanyId, Price, Capitalization, SourceShareTransactionId, CreatedInCycleId, CreatedAt)
                SELECT archive.Id,
                       archive.CompanyId,
                       archive.Price,
                       archive.Capitalization,
                       archive.SourceShareTransactionId,
                       archive.CreatedInCycleId,
                       archive.CreatedAt
                FROM PriceSnapshotArchives AS archive
                WHERE archive.Id = (
                    SELECT MAX(candidate.Id)
                    FROM PriceSnapshotArchives AS candidate
                    WHERE candidate.CompanyId = archive.CompanyId)
                  AND NOT EXISTS (
                    SELECT 1
                    FROM PriceSnapshots AS live
                    WHERE live.CompanyId = archive.CompanyId);

                DELETE FROM PriceSnapshotArchives
                WHERE EXISTS (
                    SELECT 1
                    FROM PriceSnapshots AS live
                    WHERE live.Id = PriceSnapshotArchives.Id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
