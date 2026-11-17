using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SistemaMateriais.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSectorScopedRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(
                    """
                    DO $sector_scope_preflight$
                    BEGIN
                        IF EXISTS (
                            SELECT 1
                            FROM "Materials" material
                            INNER JOIN "MaterialCategories" category
                                ON category."Id" = material."CategoryId"
                            WHERE material."SectorId" <> category."SectorId"
                            UNION ALL
                            SELECT 1
                            FROM "InventoryAdjustmentRequests" request
                            INNER JOIN "Materials" material
                                ON material."Id" = request."MaterialId"
                            WHERE request."SectorId" <> material."SectorId"
                            UNION ALL
                            SELECT 1
                            FROM "InventoryAssets" asset
                            INNER JOIN "Materials" material
                                ON material."Id" = asset."MaterialId"
                            WHERE asset."SectorId" <> material."SectorId"
                            UNION ALL
                            SELECT 1
                            FROM "InventoryLoans" loan
                            INNER JOIN "Materials" material
                                ON material."Id" = loan."MaterialId"
                            WHERE loan."SectorId" <> material."SectorId"
                            UNION ALL
                            SELECT 1
                            FROM "InventoryLoans" loan
                            INNER JOIN "InventoryAssets" asset
                                ON asset."Id" = loan."InventoryAssetId"
                            WHERE loan."SectorId" <> asset."SectorId"
                            UNION ALL
                            SELECT 1
                            FROM "InventoryMovements" movement
                            INNER JOIN "Materials" material
                                ON material."Id" = movement."MaterialId"
                            WHERE movement."SectorId" <> material."SectorId"
                            UNION ALL
                            SELECT 1
                            FROM "InventoryMovements" movement
                            INNER JOIN "InventoryLoans" loan
                                ON loan."Id" = movement."InventoryLoanId"
                            WHERE movement."SectorId" <> loan."SectorId"
                            UNION ALL
                            SELECT 1
                            FROM "InventoryMovements" movement
                            INNER JOIN "InventoryAssets" asset
                                ON asset."Id" = movement."InventoryAssetId"
                            WHERE movement."SectorId" <> asset."SectorId"
                            UNION ALL
                            SELECT 1
                            FROM "InventoryMovements" movement
                            INNER JOIN "InventoryAdjustmentRequests" request
                                ON request."Id" = movement."InventoryAdjustmentRequestId"
                            WHERE movement."SectorId" <> request."SectorId"
                        ) THEN
                            RAISE EXCEPTION 'Cannot enforce sector-scoped constraints: cross-sector references must be reconciled before migration.';
                        END IF;
                    END;
                    $sector_scope_preflight$;
                    """);
            }

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryAdjustmentRequests_Materials_MaterialId",
                table: "InventoryAdjustmentRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryAssets_Materials_MaterialId",
                table: "InventoryAssets");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryLoans_InventoryAssets_InventoryAssetId",
                table: "InventoryLoans");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryLoans_Materials_MaterialId",
                table: "InventoryLoans");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_InventoryAdjustmentRequests_InventoryAdj~",
                table: "InventoryMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_InventoryAssets_InventoryAssetId",
                table: "InventoryMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_InventoryLoans_InventoryLoanId",
                table: "InventoryMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_Materials_MaterialId",
                table: "InventoryMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_Materials_MaterialCategories_CategoryId",
                table: "Materials");

            migrationBuilder.DropIndex(
                name: "IX_Materials_CategoryId",
                table: "Materials");

            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_MaterialId",
                table: "InventoryMovements");

            migrationBuilder.DropIndex(
                name: "IX_InventoryLoans_MaterialId",
                table: "InventoryLoans");

            migrationBuilder.DropIndex(
                name: "IX_InventoryAssets_MaterialId",
                table: "InventoryAssets");

            migrationBuilder.DropIndex(
                name: "IX_InventoryAdjustmentRequests_MaterialId",
                table: "InventoryAdjustmentRequests");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Materials_SectorId_Id",
                table: "Materials",
                columns: new[] { "SectorId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_MaterialCategories_SectorId_Id",
                table: "MaterialCategories",
                columns: new[] { "SectorId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_InventoryLoans_SectorId_Id",
                table: "InventoryLoans",
                columns: new[] { "SectorId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_InventoryAssets_SectorId_Id",
                table: "InventoryAssets",
                columns: new[] { "SectorId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_InventoryAdjustmentRequests_SectorId_Id",
                table: "InventoryAdjustmentRequests",
                columns: new[] { "SectorId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Materials_SectorId_CategoryId",
                table: "Materials",
                columns: new[] { "SectorId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_SectorId_InventoryAdjustmentRequestId",
                table: "InventoryMovements",
                columns: new[] { "SectorId", "InventoryAdjustmentRequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_SectorId_InventoryAssetId",
                table: "InventoryMovements",
                columns: new[] { "SectorId", "InventoryAssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_SectorId_InventoryLoanId",
                table: "InventoryMovements",
                columns: new[] { "SectorId", "InventoryLoanId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLoans_SectorId_InventoryAssetId",
                table: "InventoryLoans",
                columns: new[] { "SectorId", "InventoryAssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAdjustmentRequests_SectorId_MaterialId",
                table: "InventoryAdjustmentRequests",
                columns: new[] { "SectorId", "MaterialId" });

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryAdjustmentRequests_Materials_SectorId_MaterialId",
                table: "InventoryAdjustmentRequests",
                columns: new[] { "SectorId", "MaterialId" },
                principalTable: "Materials",
                principalColumns: new[] { "SectorId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryAssets_Materials_SectorId_MaterialId",
                table: "InventoryAssets",
                columns: new[] { "SectorId", "MaterialId" },
                principalTable: "Materials",
                principalColumns: new[] { "SectorId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryLoans_InventoryAssets_SectorId_InventoryAssetId",
                table: "InventoryLoans",
                columns: new[] { "SectorId", "InventoryAssetId" },
                principalTable: "InventoryAssets",
                principalColumns: new[] { "SectorId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryLoans_Materials_SectorId_MaterialId",
                table: "InventoryLoans",
                columns: new[] { "SectorId", "MaterialId" },
                principalTable: "Materials",
                principalColumns: new[] { "SectorId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_InventoryAdjustmentRequests_SectorId_Inv~",
                table: "InventoryMovements",
                columns: new[] { "SectorId", "InventoryAdjustmentRequestId" },
                principalTable: "InventoryAdjustmentRequests",
                principalColumns: new[] { "SectorId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_InventoryAssets_SectorId_InventoryAssetId",
                table: "InventoryMovements",
                columns: new[] { "SectorId", "InventoryAssetId" },
                principalTable: "InventoryAssets",
                principalColumns: new[] { "SectorId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_InventoryLoans_SectorId_InventoryLoanId",
                table: "InventoryMovements",
                columns: new[] { "SectorId", "InventoryLoanId" },
                principalTable: "InventoryLoans",
                principalColumns: new[] { "SectorId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_Materials_SectorId_MaterialId",
                table: "InventoryMovements",
                columns: new[] { "SectorId", "MaterialId" },
                principalTable: "Materials",
                principalColumns: new[] { "SectorId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Materials_MaterialCategories_SectorId_CategoryId",
                table: "Materials",
                columns: new[] { "SectorId", "CategoryId" },
                principalTable: "MaterialCategories",
                principalColumns: new[] { "SectorId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryAdjustmentRequests_Materials_SectorId_MaterialId",
                table: "InventoryAdjustmentRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryAssets_Materials_SectorId_MaterialId",
                table: "InventoryAssets");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryLoans_InventoryAssets_SectorId_InventoryAssetId",
                table: "InventoryLoans");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryLoans_Materials_SectorId_MaterialId",
                table: "InventoryLoans");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_InventoryAdjustmentRequests_SectorId_Inv~",
                table: "InventoryMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_InventoryAssets_SectorId_InventoryAssetId",
                table: "InventoryMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_InventoryLoans_SectorId_InventoryLoanId",
                table: "InventoryMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_Materials_SectorId_MaterialId",
                table: "InventoryMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_Materials_MaterialCategories_SectorId_CategoryId",
                table: "Materials");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Materials_SectorId_Id",
                table: "Materials");

            migrationBuilder.DropIndex(
                name: "IX_Materials_SectorId_CategoryId",
                table: "Materials");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_MaterialCategories_SectorId_Id",
                table: "MaterialCategories");

            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_SectorId_InventoryAdjustmentRequestId",
                table: "InventoryMovements");

            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_SectorId_InventoryAssetId",
                table: "InventoryMovements");

            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_SectorId_InventoryLoanId",
                table: "InventoryMovements");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_InventoryLoans_SectorId_Id",
                table: "InventoryLoans");

            migrationBuilder.DropIndex(
                name: "IX_InventoryLoans_SectorId_InventoryAssetId",
                table: "InventoryLoans");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_InventoryAssets_SectorId_Id",
                table: "InventoryAssets");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_InventoryAdjustmentRequests_SectorId_Id",
                table: "InventoryAdjustmentRequests");

            migrationBuilder.DropIndex(
                name: "IX_InventoryAdjustmentRequests_SectorId_MaterialId",
                table: "InventoryAdjustmentRequests");

            migrationBuilder.CreateIndex(
                name: "IX_Materials_CategoryId",
                table: "Materials",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_MaterialId",
                table: "InventoryMovements",
                column: "MaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLoans_MaterialId",
                table: "InventoryLoans",
                column: "MaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAssets_MaterialId",
                table: "InventoryAssets",
                column: "MaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAdjustmentRequests_MaterialId",
                table: "InventoryAdjustmentRequests",
                column: "MaterialId");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryAdjustmentRequests_Materials_MaterialId",
                table: "InventoryAdjustmentRequests",
                column: "MaterialId",
                principalTable: "Materials",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryAssets_Materials_MaterialId",
                table: "InventoryAssets",
                column: "MaterialId",
                principalTable: "Materials",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryLoans_InventoryAssets_InventoryAssetId",
                table: "InventoryLoans",
                column: "InventoryAssetId",
                principalTable: "InventoryAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryLoans_Materials_MaterialId",
                table: "InventoryLoans",
                column: "MaterialId",
                principalTable: "Materials",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_InventoryAdjustmentRequests_InventoryAdj~",
                table: "InventoryMovements",
                column: "InventoryAdjustmentRequestId",
                principalTable: "InventoryAdjustmentRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_InventoryAssets_InventoryAssetId",
                table: "InventoryMovements",
                column: "InventoryAssetId",
                principalTable: "InventoryAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_InventoryLoans_InventoryLoanId",
                table: "InventoryMovements",
                column: "InventoryLoanId",
                principalTable: "InventoryLoans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_Materials_MaterialId",
                table: "InventoryMovements",
                column: "MaterialId",
                principalTable: "Materials",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Materials_MaterialCategories_CategoryId",
                table: "Materials",
                column: "CategoryId",
                principalTable: "MaterialCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
