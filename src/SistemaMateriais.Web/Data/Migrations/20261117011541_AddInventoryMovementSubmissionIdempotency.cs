using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SistemaMateriais.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryMovementSubmissionIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubmissionFingerprint",
                table: "InventoryMovements",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubmissionId",
                table: "InventoryMovements",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_SectorId_ActorUserId_SubmissionId",
                table: "InventoryMovements",
                columns: new[] { "SectorId", "ActorUserId", "SubmissionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_SectorId_ActorUserId_SubmissionId",
                table: "InventoryMovements");

            migrationBuilder.DropColumn(
                name: "SubmissionFingerprint",
                table: "InventoryMovements");

            migrationBuilder.DropColumn(
                name: "SubmissionId",
                table: "InventoryMovements");
        }
    }
}
