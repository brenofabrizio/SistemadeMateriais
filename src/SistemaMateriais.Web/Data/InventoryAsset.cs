namespace SistemaMateriais.Web.Data;

public enum InventoryAssetStatus
{
    Available,
    Loaned,
    Consumed,
    Retired
}

public sealed class InventoryAsset
{
    public int Id { get; set; }
    public int MaterialId { get; set; }
    public Material Material { get; set; } = null!;
    public int SectorId { get; set; }
    public Sector Sector { get; set; } = null!;
    public required string Identifier { get; set; }
    public required string NormalizedIdentifier { get; set; }
    public InventoryAssetStatus Status { get; set; } = InventoryAssetStatus.Available;
    public DateTimeOffset RegisteredAtUtc { get; set; }
    public required string RegisteredByUserId { get; set; }
    public required string RegisteredByName { get; set; }
}
