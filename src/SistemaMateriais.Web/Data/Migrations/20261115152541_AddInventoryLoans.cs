using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SistemaMateriais.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryLoans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InventoryLoanId",
                table: "InventoryMovements",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InventoryLoans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MaterialId = table.Column<int>(type: "integer", nullable: false),
                    SectorId = table.Column<int>(type: "integer", nullable: false),
                    BorrowerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryLoans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryLoans_Materials_MaterialId",
                        column: x => x.MaterialId,
                        principalTable: "Materials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLoans_Sectors_SectorId",
                        column: x => x.SectorId,
                        principalTable: "Sectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_InventoryLoanId",
                table: "InventoryMovements",
                column: "InventoryLoanId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLoans_MaterialId",
                table: "InventoryLoans",
                column: "MaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLoans_SectorId_MaterialId_CreatedAtUtc",
                table: "InventoryLoans",
                columns: new[] { "SectorId", "MaterialId", "CreatedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_InventoryLoans_InventoryLoanId",
                table: "InventoryMovements",
                column: "InventoryLoanId",
                principalTable: "InventoryLoans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_InventoryLoans_InventoryLoanId",
                table: "InventoryMovements");

            migrationBuilder.DropTable(
                name: "InventoryLoans");

            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_InventoryLoanId",
                table: "InventoryMovements");

            migrationBuilder.DropColumn(
                name: "InventoryLoanId",
                table: "InventoryMovements");
        }
    }
}
