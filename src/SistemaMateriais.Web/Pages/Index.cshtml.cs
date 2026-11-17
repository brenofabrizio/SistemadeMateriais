using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Pages;

public sealed class IndexModel(ApplicationDbContext db) : PageModel
{
    private const int PilotSectorId = 1;

    public int MaterialCount { get; private set; }
    public int LowStockCount { get; private set; }
    public int OpenLoanCount { get; private set; }
    public IReadOnlyList<(Material Material, decimal Balance)> LowStockMaterials { get; private set; } = [];
    public IReadOnlyList<InventoryMovement> RecentMovements { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var materials = await LoadActiveMaterialsAsync(cancellationToken);
        MaterialCount = materials.Count;
        LowStockMaterials = await FindLowStockMaterialsAsync(materials, cancellationToken);
        LowStockCount = LowStockMaterials.Count;

        RecentMovements = await db.InventoryMovements
            .AsNoTracking()
            .Include(movement => movement.Material)
            .Where(movement => movement.SectorId == PilotSectorId)
            .OrderByDescending(movement => movement.Id)
            .Take(5)
            .ToListAsync(cancellationToken);

        var loans = await db.InventoryLoans
            .AsNoTracking()
            .Where(loan => loan.SectorId == PilotSectorId)
            .Select(loan => new { loan.Id, loan.Quantity })
            .ToListAsync(cancellationToken);
        var returnedByLoan = await db.InventoryMovements
            .AsNoTracking()
            .Where(movement => movement.SectorId == PilotSectorId
                && movement.InventoryLoanId.HasValue
                && movement.Type == InventoryMovementType.Return)
            .GroupBy(movement => movement.InventoryLoanId!.Value)
            .Select(group => new { LoanId = group.Key, Quantity = group.Sum(movement => movement.Quantity) })
            .ToDictionaryAsync(item => item.LoanId, item => item.Quantity, cancellationToken);
        OpenLoanCount = loans.Count(loan => loan.Quantity > returnedByLoan.GetValueOrDefault(loan.Id));
    }

    public async Task<IActionResult> OnGetLowStockCsvAsync(CancellationToken cancellationToken)
    {
        var materials = await LoadActiveMaterialsAsync(cancellationToken);
        var lowStockMaterials = await FindLowStockMaterialsAsync(materials, cancellationToken);
        var csv = new StringBuilder();
        csv.AppendLine("Código,Material,Saldo atual,Estoque mínimo,Unidade");

        foreach (var item in lowStockMaterials)
        {
            csv.Append(CsvText(item.Material.Code)).Append(',')
                .Append(CsvText(item.Material.Name)).Append(',')
                .Append(item.Balance.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.Material.MinimumStock.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(CsvText(item.Material.Unit)).AppendLine();
        }

        var payload = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return File(payload, "text/csv; charset=utf-8", "estoque-abaixo-minimo.csv");
    }

    private Task<List<Material>> LoadActiveMaterialsAsync(CancellationToken cancellationToken) => db.Materials
        .AsNoTracking()
        .Where(material => material.SectorId == PilotSectorId && material.IsActive)
        .OrderBy(material => material.Name)
        .ToListAsync(cancellationToken);

    private async Task<IReadOnlyList<(Material Material, decimal Balance)>> FindLowStockMaterialsAsync(
        IReadOnlyList<Material> materials,
        CancellationToken cancellationToken)
    {
        var balances = await db.InventoryMovements
            .AsNoTracking()
            .Where(movement => movement.SectorId == PilotSectorId)
            .GroupBy(movement => movement.MaterialId)
            .Select(group => new
            {
                MaterialId = group.Key,
                Balance = group.Sum(movement =>
                    movement.Type == InventoryMovementType.Entry || movement.Type == InventoryMovementType.Return
                        ? movement.Quantity
                        : movement.Type == InventoryMovementType.Consumption || movement.Type == InventoryMovementType.Loan
                            ? -movement.Quantity
                            : movement.Quantity)
            })
            .ToDictionaryAsync(item => item.MaterialId, item => item.Balance, cancellationToken);

        return materials
            .Select(material => (Material: material, Balance: balances.GetValueOrDefault(material.Id)))
            .Where(item => item.Balance < item.Material.MinimumStock)
            .OrderBy(item => item.Balance)
            .ThenBy(item => item.Material.Name)
            .ToList();
    }

    private static string CsvText(string value)
    {
        var firstSignificant = value.TrimStart();
        if (firstSignificant.Length > 0 && firstSignificant[0] is '=' or '+' or '-' or '@')
        {
            value = $"'{value}";
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
