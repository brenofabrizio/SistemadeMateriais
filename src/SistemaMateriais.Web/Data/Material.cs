namespace SistemaMateriais.Web.Data;

public enum MaterialTrackingMode
{
    Quantity,
    IndividualAsset
}

public sealed class Material
{
    public int Id { get; set; }
    public required string Code { get; set; }
    public required string NormalizedCode { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Unit { get; set; }
    public MaterialTrackingMode TrackingMode { get; set; }
    public decimal MinimumStock { get; set; }
    public int CategoryId { get; set; }
    public MaterialCategory Category { get; set; } = null!;
    public int SectorId { get; set; }
    public Sector Sector { get; set; } = null!;
    public bool IsActive { get; set; } = true;
}
