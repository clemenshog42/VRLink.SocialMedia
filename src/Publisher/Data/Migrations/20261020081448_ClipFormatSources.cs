using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VRLinkPublisher.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClipFormatSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "Clips",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MusicFree",
                table: "Clips",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Players",
                table: "Clips",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Sources",
                table: "Clips",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Format",
                table: "Clips");

            migrationBuilder.DropColumn(
                name: "MusicFree",
                table: "Clips");

            migrationBuilder.DropColumn(
                name: "Players",
                table: "Clips");

            migrationBuilder.DropColumn(
                name: "Sources",
                table: "Clips");
        }
    }
}
