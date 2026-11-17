using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SistemaMateriais.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryAdjustmentSubmissionIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubmissionFingerprint",
                table: "InventoryAdjustmentRequests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubmissionId",
                table: "InventoryAdjustmentRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdjustmentRequests_SectorId_RequestedByUserId_SubmissionId",
                table: "InventoryAdjustmentRequests",
                columns: new[] { "SectorId", "RequestedByUserId", "SubmissionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AdjustmentRequests_SectorId_RequestedByUserId_SubmissionId",
                table: "InventoryAdjustmentRequests");

            migrationBuilder.DropColumn(
                name: "SubmissionFingerprint",
                table: "InventoryAdjustmentRequests");

            migrationBuilder.DropColumn(
                name: "SubmissionId",
                table: "InventoryAdjustmentRequests");
        }
    }
}
