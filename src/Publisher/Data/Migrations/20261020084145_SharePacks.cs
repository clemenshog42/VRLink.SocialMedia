using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VRLinkPublisher.Data.Migrations
{
    /// <inheritdoc />
    public partial class SharePacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SharePacks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MatchId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Uploader = table.Column<string>(type: "TEXT", nullable: false),
                    PackId = table.Column<string>(type: "TEXT", nullable: true),
                    Token = table.Column<string>(type: "TEXT", nullable: true),
                    ShareUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClipIds = table.Column<string>(type: "TEXT", nullable: false),
                    ReleasedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReleasedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharePacks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SharePacks_MatchId",
                table: "SharePacks",
                column: "MatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SharePacks");
        }
    }
}
