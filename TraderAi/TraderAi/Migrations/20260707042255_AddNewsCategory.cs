using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "NewsPosts",
                type: "TEXT",
                nullable: false,
                defaultValue: "General");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category",
                table: "NewsPosts");
        }
    }
}
