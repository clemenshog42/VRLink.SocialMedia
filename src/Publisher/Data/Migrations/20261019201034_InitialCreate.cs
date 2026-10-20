using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VRLinkPublisher.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    Username = table.Column<string>(type: "TEXT", nullable: true),
                    PageId = table.Column<string>(type: "TEXT", nullable: true),
                    AccessTokenEnc = table.Column<byte[]>(type: "BLOB", nullable: true),
                    RefreshTokenEnc = table.Column<byte[]>(type: "BLOB", nullable: true),
                    AccessExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RefreshExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Scopes = table.Column<string>(type: "TEXT", nullable: true),
                    ConnectedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRefreshUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastWarningUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Platform);
                });

            migrationBuilder.CreateTable(
                name: "Clips",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HighlightId = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    MatchId = table.Column<string>(type: "TEXT", nullable: true),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationSec = table.Column<double>(type: "REAL", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    Caption = table.Column<string>(type: "TEXT", nullable: true),
                    Hashtags = table.Column<string>(type: "TEXT", nullable: false),
                    Venue = table.Column<string>(type: "TEXT", nullable: true),
                    HeroIsBot = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ImportedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VideoPath = table.Column<string>(type: "TEXT", nullable: false),
                    ThumbPath = table.Column<string>(type: "TEXT", nullable: true),
                    JsonPath = table.Column<string>(type: "TEXT", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StatusReason = table.Column<string>(type: "TEXT", nullable: true),
                    DecidedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clips", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Credentials",
                columns: table => new
                {
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", nullable: true),
                    ClientSecretEnc = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credentials", x => x.Platform);
                });

            migrationBuilder.CreateTable(
                name: "Log",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    ClipId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Posts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClipId = table.Column<int>(type: "INTEGER", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    SlotKey = table.Column<string>(type: "TEXT", nullable: true),
                    ScheduledUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PublishedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    Caption = table.Column<string>(type: "TEXT", nullable: true),
                    PrivacyLevel = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: true),
                    PostId = table.Column<string>(type: "TEXT", nullable: true),
                    PostUrl = table.Column<string>(type: "TEXT", nullable: true),
                    NextStatsPollUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastViews = table.Column<long>(type: "INTEGER", nullable: true),
                    LastLikes = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Posts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Posts_Clips_ClipId",
                        column: x => x.ClipId,
                        principalTable: "Clips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Stats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlatformPostId = table.Column<int>(type: "INTEGER", nullable: false),
                    TakenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Views = table.Column<long>(type: "INTEGER", nullable: false),
                    Likes = table.Column<long>(type: "INTEGER", nullable: false),
                    Comments = table.Column<long>(type: "INTEGER", nullable: false),
                    Shares = table.Column<long>(type: "INTEGER", nullable: false),
                    Saves = table.Column<long>(type: "INTEGER", nullable: false),
                    Reach = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Stats_Posts_PlatformPostId",
                        column: x => x.PlatformPostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clips_ContentHash",
                table: "Clips",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_Clips_HighlightId",
                table: "Clips",
                column: "HighlightId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clips_Status",
                table: "Clips",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Log_Utc",
                table: "Log",
                column: "Utc");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_ClipId_Platform",
                table: "Posts",
                columns: new[] { "ClipId", "Platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Platform_Status",
                table: "Posts",
                columns: new[] { "Platform", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Stats_PlatformPostId_TakenUtc",
                table: "Stats",
                columns: new[] { "PlatformPostId", "TakenUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "Credentials");

            migrationBuilder.DropTable(
                name: "Log");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Stats");

            migrationBuilder.DropTable(
                name: "Posts");

            migrationBuilder.DropTable(
                name: "Clips");
        }
    }
}
