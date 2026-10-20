using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VRLinkPublisher.Data.Migrations
{
    /// <inheritdoc />
    public partial class Learning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CaptionVariant",
                table: "Posts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CaptionVariant",
                table: "Posts");
        }
    }
}
