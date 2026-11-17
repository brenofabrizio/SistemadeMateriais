using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Tests;

public sealed class SectorRelationshipIntegrityTests
{
    [Fact]
    public async Task MaterialCannotReferenceCategoryFromAnotherSector()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var otherSector = new Sector { Name = "Outro setor", IsActive = true };
        db.Sectors.Add(otherSector);
        await db.SaveChangesAsync();
        var category = new MaterialCategory
        {
            Name = "Categoria externa",
            NormalizedName = "CATEGORIA EXTERNA",
            SectorId = otherSector.Id
        };
        db.MaterialCategories.Add(category);
        await db.SaveChangesAsync();

        db.Materials.Add(new Material
        {
            Code = "MAT-CROSS-SECTOR",
            NormalizedCode = "MAT-CROSS-SECTOR",
            Name = "Material com categoria de outro setor",
            Unit = "unidade",
            CategoryId = category.Id,
            SectorId = 1,
            TrackingMode = MaterialTrackingMode.Quantity,
            MinimumStock = 0m
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task MovementCannotReferenceMaterialFromAnotherSector()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var otherSector = new Sector { Name = "Outro setor", IsActive = true };
        db.Sectors.Add(otherSector);
        await db.SaveChangesAsync();
        var category = new MaterialCategory
        {
            Name = "Categoria externa",
            NormalizedName = "CATEGORIA EXTERNA",
            SectorId = otherSector.Id
        };
        db.MaterialCategories.Add(category);
        await db.SaveChangesAsync();
        var material = new Material
        {
            Code = "MAT-OTHER-SECTOR",
            NormalizedCode = "MAT-OTHER-SECTOR",
            Name = "Material de outro setor",
            Unit = "unidade",
            CategoryId = category.Id,
            SectorId = otherSector.Id,
            TrackingMode = MaterialTrackingMode.Quantity,
            MinimumStock = 0m
        };
        db.Materials.Add(material);
        await db.SaveChangesAsync();

        db.InventoryMovements.Add(new InventoryMovement
        {
            MaterialId = material.Id,
            SectorId = 1,
            Type = InventoryMovementType.Entry,
            Quantity = 1m,
            ActorUserId = "test-user",
            ActorName = "Test User",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
