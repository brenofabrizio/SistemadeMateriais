using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SistemaMateriais.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkLoansToIndividualAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InventoryAssetId",
                table: "InventoryLoans",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLoans_InventoryAssetId",
                table: "InventoryLoans",
                column: "InventoryAssetId");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryLoans_InventoryAssets_InventoryAssetId",
                table: "InventoryLoans",
                column: "InventoryAssetId",
                principalTable: "InventoryAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryLoans_InventoryAssets_InventoryAssetId",
                table: "InventoryLoans");

            migrationBuilder.DropIndex(
                name: "IX_InventoryLoans_InventoryAssetId",
                table: "InventoryLoans");

            migrationBuilder.DropColumn(
                name: "InventoryAssetId",
                table: "InventoryLoans");
        }
    }
}
