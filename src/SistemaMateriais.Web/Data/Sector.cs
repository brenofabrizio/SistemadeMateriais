namespace SistemaMateriais.Web.Data;

public sealed class Sector
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
}
