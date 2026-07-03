using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyLastDividendCapitalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LastDividendCapitalization",
                table: "Companies",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastDividendCapitalization",
                table: "Companies");
        }
    }
}
