using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VRLinkPublisher.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManualApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAtUtc",
                table: "Clips",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "Clips",
                type: "TEXT",
                nullable: true);

            // Hard rule (customer, 2026-10-20): every video needs the owner's approval.
            // 1) old review-mode status → AwaitingApproval
            migrationBuilder.Sql("UPDATE Clips SET Status = 'AwaitingApproval' WHERE Status = 'PendingReview';");
            // 2) clips approved automatically before this rule (no ApprovedAtUtc) and not yet posted anywhere → back to the owner
            migrationBuilder.Sql(
                "UPDATE Clips SET Status = 'AwaitingApproval', StatusReason = 'auto-approved before manual-approval rule: please review' " +
                "WHERE Status = 'Approved' AND ApprovedAtUtc IS NULL " +
                "AND Id NOT IN (SELECT ClipId FROM Posts WHERE Status IN ('Published','Publishing'));");
            // 3) their waiting queue entries are cancelled (re-queued by the owner's APPROVE)
            migrationBuilder.Sql(
                "UPDATE Posts SET Status = 'Cancelled', LastError = 'needs owner approval' " +
                "WHERE Status IN ('Queued','Scheduled') AND ClipId IN (SELECT Id FROM Clips WHERE ApprovedAtUtc IS NULL);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedAtUtc",
                table: "Clips");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "Clips");
        }
    }
}
