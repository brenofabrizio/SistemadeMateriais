using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SistemaMateriais.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InventoryAdjustmentRequestId",
                table: "InventoryMovements",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InventoryAdjustmentRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MaterialId = table.Column<int>(type: "integer", nullable: false),
                    SectorId = table.Column<int>(type: "integer", nullable: false),
                    DeltaQuantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    RequestedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    DecidedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryAdjustmentRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryAdjustmentRequests_Materials_MaterialId",
                        column: x => x.MaterialId,
                        principalTable: "Materials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryAdjustmentRequests_Sectors_SectorId",
                        column: x => x.SectorId,
                        principalTable: "Sectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_InventoryAdjustmentRequestId",
                table: "InventoryMovements",
                column: "InventoryAdjustmentRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAdjustmentRequests_MaterialId",
                table: "InventoryAdjustmentRequests",
                column: "MaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAdjustmentRequests_SectorId_Status_RequestedAtUtc",
                table: "InventoryAdjustmentRequests",
                columns: new[] { "SectorId", "Status", "RequestedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_InventoryAdjustmentRequests_InventoryAdj~",
                table: "InventoryMovements",
                column: "InventoryAdjustmentRequestId",
                principalTable: "InventoryAdjustmentRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_InventoryAdjustmentRequests_InventoryAdj~",
                table: "InventoryMovements");

            migrationBuilder.DropTable(
                name: "InventoryAdjustmentRequests");

            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_InventoryAdjustmentRequestId",
                table: "InventoryMovements");

            migrationBuilder.DropColumn(
                name: "InventoryAdjustmentRequestId",
                table: "InventoryMovements");
        }
    }
}
