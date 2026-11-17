using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SistemaMateriais.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InventoryAssetId",
                table: "InventoryMovements",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InventoryAssets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MaterialId = table.Column<int>(type: "integer", nullable: false),
                    SectorId = table.Column<int>(type: "integer", nullable: false),
                    Identifier = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedIdentifier = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RegisteredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RegisteredByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    RegisteredByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryAssets_Materials_MaterialId",
                        column: x => x.MaterialId,
                        principalTable: "Materials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryAssets_Sectors_SectorId",
                        column: x => x.SectorId,
                        principalTable: "Sectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_InventoryAssetId",
                table: "InventoryMovements",
                column: "InventoryAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAssets_MaterialId",
                table: "InventoryAssets",
                column: "MaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAssets_SectorId_MaterialId_Status",
                table: "InventoryAssets",
                columns: new[] { "SectorId", "MaterialId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAssets_SectorId_NormalizedIdentifier",
                table: "InventoryAssets",
                columns: new[] { "SectorId", "NormalizedIdentifier" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_InventoryAssets_InventoryAssetId",
                table: "InventoryMovements",
                column: "InventoryAssetId",
                principalTable: "InventoryAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_InventoryAssets_InventoryAssetId",
                table: "InventoryMovements");

            migrationBuilder.DropTable(
                name: "InventoryAssets");

            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_InventoryAssetId",
                table: "InventoryMovements");

            migrationBuilder.DropColumn(
                name: "InventoryAssetId",
                table: "InventoryMovements");
        }
    }
}
