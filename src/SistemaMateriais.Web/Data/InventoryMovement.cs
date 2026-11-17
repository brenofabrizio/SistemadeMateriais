namespace SistemaMateriais.Web.Data;

public enum InventoryMovementType
{
    Entry,
    Consumption,
    Loan,
    Return,
    Adjustment
}

public sealed class InventoryMovement
{
    public int Id { get; set; }
    public int MaterialId { get; set; }
    public Material Material { get; set; } = null!;
    public int SectorId { get; set; }
    public Sector Sector { get; set; } = null!;
    public InventoryMovementType Type { get; set; }
    public decimal Quantity { get; set; }
    public string? Reference { get; set; }
    public Guid? SubmissionId { get; set; }
    public string? SubmissionFingerprint { get; set; }
    public int? InventoryLoanId { get; set; }
    public InventoryLoan? Loan { get; set; }
    public int? InventoryAssetId { get; set; }
    public InventoryAsset? Asset { get; set; }
    public int? InventoryAdjustmentRequestId { get; set; }
    public InventoryAdjustmentRequest? AdjustmentRequest { get; set; }
    public required string ActorUserId { get; set; }
    public required string ActorName { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
