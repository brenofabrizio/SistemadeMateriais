using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SistemaMateriais.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext(options)
{
    public DbSet<Sector> Sectors => Set<Sector>();
    public DbSet<MaterialCategory> MaterialCategories => Set<MaterialCategory>();
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<InventoryLoan> InventoryLoans => Set<InventoryLoan>();
    public DbSet<InventoryAsset> InventoryAssets => Set<InventoryAsset>();
    public DbSet<InventoryAdjustmentRequest> InventoryAdjustmentRequests => Set<InventoryAdjustmentRequest>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Sector>(entity =>
        {
            entity.Property(sector => sector.Name).HasMaxLength(80).IsRequired();
            entity.HasData(new Sector { Id = 1, Name = "TI", IsActive = true });
        });

        builder.Entity<MaterialCategory>(entity =>
        {
            entity.Property(category => category.Name).HasMaxLength(80).IsRequired();
            entity.Property(category => category.NormalizedName).HasMaxLength(80).IsRequired();
            entity.HasIndex(category => new { category.SectorId, category.NormalizedName }).IsUnique();
            entity.HasAlternateKey(category => new { category.SectorId, category.Id });
            entity.HasOne(category => category.Sector)
                .WithMany()
                .HasForeignKey(category => category.SectorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Material>(entity =>
        {
            entity.Property(material => material.Code).HasMaxLength(40).IsRequired();
            entity.Property(material => material.NormalizedCode).HasMaxLength(40).IsRequired();
            entity.Property(material => material.Name).HasMaxLength(120).IsRequired();
            entity.Property(material => material.Description).HasMaxLength(500);
            entity.Property(material => material.Unit).HasMaxLength(20).IsRequired();
            entity.Property(material => material.TrackingMode).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(material => material.MinimumStock).HasPrecision(12, 3);
            entity.HasIndex(material => new { material.SectorId, material.NormalizedCode }).IsUnique();
            entity.HasAlternateKey(material => new { material.SectorId, material.Id });
            entity.HasOne(material => material.Category)
                .WithMany()
                .HasForeignKey(material => new { material.SectorId, material.CategoryId })
                .HasPrincipalKey(category => new { category.SectorId, category.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(material => material.Sector)
                .WithMany()
                .HasForeignKey(material => material.SectorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<InventoryAdjustmentRequest>(entity =>
        {
            entity.Property(request => request.DeltaQuantity).HasPrecision(12, 3);
            entity.Property(request => request.Reason).HasMaxLength(500).IsRequired();
            entity.Property(request => request.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(request => request.RequestedByUserId).HasMaxLength(450).IsRequired();
            entity.Property(request => request.RequestedByName).HasMaxLength(120).IsRequired();
            entity.Property(request => request.SubmissionFingerprint).HasMaxLength(64);
            entity.Property(request => request.DecidedByUserId).HasMaxLength(450);
            entity.Property(request => request.DecidedByName).HasMaxLength(120);
            entity.HasIndex(request => new { request.SectorId, request.Status, request.RequestedAtUtc });
            entity.HasIndex(request => new { request.SectorId, request.RequestedByUserId, request.SubmissionId })
                .IsUnique()
                .HasDatabaseName("IX_AdjustmentRequests_SectorId_RequestedByUserId_SubmissionId");
            entity.HasAlternateKey(request => new { request.SectorId, request.Id });
            entity.HasOne(request => request.Material)
                .WithMany()
                .HasForeignKey(request => new { request.SectorId, request.MaterialId })
                .HasPrincipalKey(material => new { material.SectorId, material.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(request => request.Sector)
                .WithMany()
                .HasForeignKey(request => request.SectorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<InventoryLoan>(entity =>
        {
            entity.Property(loan => loan.BorrowerName).HasMaxLength(120).IsRequired();
            entity.Property(loan => loan.Quantity).HasPrecision(12, 3);
            entity.Property(loan => loan.Reference).HasMaxLength(200);
            entity.Property(loan => loan.CreatedByUserId).HasMaxLength(450).IsRequired();
            entity.Property(loan => loan.CreatedByName).HasMaxLength(120).IsRequired();
            entity.HasIndex(loan => new { loan.SectorId, loan.MaterialId, loan.CreatedAtUtc });
            entity.HasIndex(loan => loan.InventoryAssetId);
            entity.HasAlternateKey(loan => new { loan.SectorId, loan.Id });
            entity.HasOne(loan => loan.Material)
                .WithMany()
                .HasForeignKey(loan => new { loan.SectorId, loan.MaterialId })
                .HasPrincipalKey(material => new { material.SectorId, material.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(loan => loan.Sector)
                .WithMany()
                .HasForeignKey(loan => loan.SectorId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(loan => loan.Asset)
                .WithMany()
                .HasForeignKey(loan => new { loan.SectorId, loan.InventoryAssetId })
                .HasPrincipalKey(asset => new { asset.SectorId, asset.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<InventoryAsset>(entity =>
        {
            entity.Property(asset => asset.Identifier).HasMaxLength(120).IsRequired();
            entity.Property(asset => asset.NormalizedIdentifier).HasMaxLength(120).IsRequired();
            entity.Property(asset => asset.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(asset => asset.RegisteredByUserId).HasMaxLength(450).IsRequired();
            entity.Property(asset => asset.RegisteredByName).HasMaxLength(120).IsRequired();
            entity.HasIndex(asset => new { asset.SectorId, asset.NormalizedIdentifier }).IsUnique();
            entity.HasIndex(asset => new { asset.SectorId, asset.MaterialId, asset.Status });
            entity.HasAlternateKey(asset => new { asset.SectorId, asset.Id });
            entity.HasOne(asset => asset.Material)
                .WithMany()
                .HasForeignKey(asset => new { asset.SectorId, asset.MaterialId })
                .HasPrincipalKey(material => new { material.SectorId, material.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(asset => asset.Sector)
                .WithMany()
                .HasForeignKey(asset => asset.SectorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<InventoryMovement>(entity =>
        {
            entity.Property(movement => movement.Type).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(movement => movement.Quantity).HasPrecision(12, 3);
            entity.Property(movement => movement.Reference).HasMaxLength(200);
            entity.Property(movement => movement.SubmissionFingerprint).HasMaxLength(64);
            entity.HasIndex(movement => movement.InventoryLoanId);
            entity.HasIndex(movement => movement.InventoryAssetId);
            entity.HasIndex(movement => movement.InventoryAdjustmentRequestId);
            entity.HasIndex(movement => new { movement.SectorId, movement.ActorUserId, movement.SubmissionId })
                .IsUnique()
                .HasDatabaseName("IX_InventoryMovements_SectorId_ActorUserId_SubmissionId");
            entity.Property(movement => movement.ActorUserId).HasMaxLength(450).IsRequired();
            entity.Property(movement => movement.ActorName).HasMaxLength(120).IsRequired();
            entity.HasIndex(movement => new { movement.SectorId, movement.MaterialId, movement.CreatedAtUtc });
            entity.HasOne(movement => movement.Material)
                .WithMany()
                .HasForeignKey(movement => new { movement.SectorId, movement.MaterialId })
                .HasPrincipalKey(material => new { material.SectorId, material.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(movement => movement.Sector)
                .WithMany()
                .HasForeignKey(movement => movement.SectorId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(movement => movement.Loan)
                .WithMany()
                .HasForeignKey(movement => new { movement.SectorId, movement.InventoryLoanId })
                .HasPrincipalKey(loan => new { loan.SectorId, loan.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(movement => movement.Asset)
                .WithMany()
                .HasForeignKey(movement => new { movement.SectorId, movement.InventoryAssetId })
                .HasPrincipalKey(asset => new { asset.SectorId, asset.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(movement => movement.AdjustmentRequest)
                .WithMany()
                .HasForeignKey(movement => new { movement.SectorId, movement.InventoryAdjustmentRequestId })
                .HasPrincipalKey(request => new { request.SectorId, request.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
