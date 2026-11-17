using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SistemaMateriais.Web.Authorization;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Pages.Materiais;

public sealed class IndexModel(ApplicationDbContext db, IAuthorizationService authorization) : PageModel
{
    private const int PilotSectorId = 1;

    private const int PageSize = 10;

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? CategoryFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool? ActiveFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public MaterialTrackingMode? TrackingFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public int TotalPages { get; private set; } = 1;
    public IReadOnlyDictionary<int, decimal> StockBalances { get; private set; } = new Dictionary<int, decimal>();

    [BindProperty]
    [Required(ErrorMessage = "Informe o código do material.")]
    [StringLength(40, ErrorMessage = "O código aceita até 40 caracteres.")]
    public string Code { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Informe o nome do material.")]
    [StringLength(120, MinimumLength = 2, ErrorMessage = "Use entre 2 e 120 caracteres.")]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    [StringLength(500, ErrorMessage = "A descrição aceita até 500 caracteres.")]
    public string? Description { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Informe a unidade de medida.")]
    [StringLength(20, ErrorMessage = "A unidade aceita até 20 caracteres.")]
    public string Unit { get; set; } = "unidade";

    [BindProperty]
    [Range(1, int.MaxValue, ErrorMessage = "Selecione uma categoria ativa.")]
    public int CategoryId { get; set; }

    [BindProperty]
    public int DeactivateId { get; set; }

    [BindProperty]
    public int ActivateId { get; set; }

    [BindProperty]
    [EnumDataType(typeof(MaterialTrackingMode), ErrorMessage = "Selecione um tipo de controle válido.")]
    public MaterialTrackingMode TrackingMode { get; set; } = MaterialTrackingMode.Quantity;

    [BindProperty]
    [Range(typeof(decimal), "0", "999999999.999", ParseLimitsInInvariantCulture = true,
        ErrorMessage = "O estoque mínimo deve ser zero ou maior.")]
    public decimal MinimumStock { get; set; }

    public IReadOnlyList<Material> Materials { get; private set; } = [];
    public IReadOnlyList<MaterialCategory> Categories { get; private set; } = [];
    public IReadOnlyList<MaterialCategory> FilterCategories { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadPageDataAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        var authorized = await authorization.AuthorizeAsync(User, InventoryPolicies.ManageCatalog);
        if (!authorized.Succeeded)
        {
            return Forbid();
        }

        var displayCode = Code?.Trim() ?? string.Empty;
        var displayName = Name?.Trim() ?? string.Empty;
        var displayUnit = Unit?.Trim() ?? string.Empty;
        var normalizedCode = displayCode.ToUpperInvariant();
        if (!ModelState.IsValid || displayCode.Length is < 1 or > 40
            || displayName.Length is < 2 or > 120 || displayUnit.Length is < 1 or > 20
            || !Enum.IsDefined(TrackingMode))
        {
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }

        var categoryIsAvailable = await db.MaterialCategories.AnyAsync(
            category => category.Id == CategoryId
                && category.SectorId == PilotSectorId
                && category.IsActive,
            cancellationToken);
        if (!categoryIsAvailable)
        {
            ModelState.AddModelError(nameof(CategoryId), "Selecione uma categoria ativa do setor TI.");
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }

        var codeAlreadyExists = await db.Materials.AnyAsync(
            material => material.SectorId == PilotSectorId && material.NormalizedCode == normalizedCode,
            cancellationToken);
        if (codeAlreadyExists)
        {
            ModelState.AddModelError(nameof(Code), "Já existe um material com esse código no setor TI.");
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }

        db.Materials.Add(new Material
        {
            Code = displayCode,
            NormalizedCode = normalizedCode,
            Name = displayName,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Unit = displayUnit,
            CategoryId = CategoryId,
            SectorId = PilotSectorId,
            TrackingMode = TrackingMode,
            MinimumStock = MinimumStock
        });
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeactivateAsync(CancellationToken cancellationToken)
    {
        var authorized = await authorization.AuthorizeAsync(User, InventoryPolicies.ManageCatalog);
        if (!authorized.Succeeded)
        {
            return Forbid();
        }

        var material = await db.Materials.SingleOrDefaultAsync(
            item => item.Id == DeactivateId && item.SectorId == PilotSectorId,
            cancellationToken);
        if (material is null)
        {
            return NotFound();
        }

        material.IsActive = false;
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostActivateAsync(CancellationToken cancellationToken)
    {
        var authorized = await authorization.AuthorizeAsync(User, InventoryPolicies.ManageCatalog);
        if (!authorized.Succeeded)
        {
            return Forbid();
        }

        var material = await db.Materials.SingleOrDefaultAsync(
            item => item.Id == ActivateId && item.SectorId == PilotSectorId,
            cancellationToken);
        if (material is null)
        {
            return NotFound();
        }

        material.IsActive = true;
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage();
    }

    private async Task LoadPageDataAsync(CancellationToken cancellationToken)
    {
        Categories = await db.MaterialCategories
            .AsNoTracking()
            .Where(category => category.SectorId == PilotSectorId && category.IsActive)
            .OrderBy(category => category.Name)
            .ToListAsync(cancellationToken);
        FilterCategories = await db.MaterialCategories
            .AsNoTracking()
            .Where(category => category.SectorId == PilotSectorId)
            .OrderBy(category => category.Name)
            .ToListAsync(cancellationToken);
        var materialsQuery = db.Materials
            .AsNoTracking()
            .Include(material => material.Category)
            .Where(material => material.SectorId == PilotSectorId);
        var search = Search?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(search))
        {
            materialsQuery = materialsQuery.Where(material =>
                material.Code.ToUpper().Contains(search)
                || material.Name.ToUpper().Contains(search)
                || (material.Description != null && material.Description.ToUpper().Contains(search)));
        }
        if (CategoryFilter.HasValue)
        {
            materialsQuery = materialsQuery.Where(material => material.CategoryId == CategoryFilter.Value);
        }
        if (TrackingFilter.HasValue)
        {
            materialsQuery = materialsQuery.Where(material => material.TrackingMode == TrackingFilter.Value);
        }
        if (ActiveFilter.HasValue)
        {
            materialsQuery = materialsQuery.Where(material => material.IsActive == ActiveFilter.Value);
        }

        var matchingCount = await materialsQuery.CountAsync(cancellationToken);
        TotalPages = Math.Max(1, (int)Math.Ceiling(matchingCount / (double)PageSize));
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
        Materials = await materialsQuery
            .OrderBy(material => material.Name)
            .ThenBy(material => material.Id)
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(cancellationToken);

        StockBalances = await db.InventoryMovements
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
    }
}
