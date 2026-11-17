using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SistemaMateriais.Web.Authorization;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Pages.Materiais;

[Authorize(Policy = InventoryPolicies.OperateStock)]
public sealed class DetalhesModel(ApplicationDbContext db) : PageModel
{
    private const int PilotSectorId = 1;

    public Material Material { get; private set; } = null!;
    public decimal StockBalance { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var material = await db.Materials
            .AsNoTracking()
            .Include(item => item.Category)
            .SingleOrDefaultAsync(item => item.Id == id && item.SectorId == PilotSectorId, cancellationToken);
        if (material is null)
        {
            return NotFound();
        }

        Material = material;
        StockBalance = await db.InventoryMovements
            .AsNoTracking()
            .Where(movement => movement.MaterialId == id && movement.SectorId == PilotSectorId)
            .Select(movement => movement.Type == InventoryMovementType.Entry || movement.Type == InventoryMovementType.Return
                ? movement.Quantity
                : movement.Type == InventoryMovementType.Consumption || movement.Type == InventoryMovementType.Loan
                    ? -movement.Quantity
                    : movement.Quantity)
            .SumAsync(cancellationToken);
        return Page();
    }
}
