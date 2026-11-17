namespace SistemaMateriais.Web.Data;

public sealed class MaterialCategory
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public int SectorId { get; set; }
    public Sector Sector { get; set; } = null!;
    public bool IsActive { get; set; } = true;
}
