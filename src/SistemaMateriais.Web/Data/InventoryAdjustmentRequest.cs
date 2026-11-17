namespace SistemaMateriais.Web.Data;

public enum InventoryAdjustmentStatus
{
    Pending,
    Approved,
    Rejected
}

public sealed class InventoryAdjustmentRequest
{
    public int Id { get; set; }
    public int MaterialId { get; set; }
    public Material Material { get; set; } = null!;
    public int SectorId { get; set; }
    public Sector Sector { get; set; } = null!;
    public decimal DeltaQuantity { get; set; }
    public required string Reason { get; set; }
    public InventoryAdjustmentStatus Status { get; set; }
    public required string RequestedByUserId { get; set; }
    public required string RequestedByName { get; set; }
    public Guid? SubmissionId { get; set; }
    public string? SubmissionFingerprint { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public string? DecidedByUserId { get; set; }
    public string? DecidedByName { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
}
