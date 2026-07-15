using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraderAi.Migrations
{
    /// <inheritdoc />
    public partial class AddGameSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ValueJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameSettings", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameSettings");
        }
    }
}
