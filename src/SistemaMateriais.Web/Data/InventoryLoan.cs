namespace SistemaMateriais.Web.Data;

public sealed class InventoryLoan
{
    public int Id { get; set; }
    public int MaterialId { get; set; }
    public Material Material { get; set; } = null!;
    public int SectorId { get; set; }
    public Sector Sector { get; set; } = null!;
    public int? InventoryAssetId { get; set; }
    public InventoryAsset? Asset { get; set; }
    public required string BorrowerName { get; set; }
    public decimal Quantity { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DueAtUtc { get; set; }
    public string? Reference { get; set; }
    public required string CreatedByUserId { get; set; }
    public required string CreatedByName { get; set; }
}
