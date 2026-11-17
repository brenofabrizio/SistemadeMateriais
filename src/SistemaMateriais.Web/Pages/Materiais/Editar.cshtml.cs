using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SistemaMateriais.Web.Authorization;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Pages.Materiais;

[Authorize(Policy = InventoryPolicies.ManageCatalog)]
public sealed class EditarModel(ApplicationDbContext db) : PageModel
{
    private const int PilotSectorId = 1;

    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

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
    [EnumDataType(typeof(MaterialTrackingMode), ErrorMessage = "Selecione um tipo de controle válido.")]
    public MaterialTrackingMode TrackingMode { get; set; } = MaterialTrackingMode.Quantity;

    [BindProperty]
    [Range(typeof(decimal), "0", "999999999.999", ParseLimitsInInvariantCulture = true,
        ErrorMessage = "O estoque mínimo deve ser zero ou maior.")]
    public decimal MinimumStock { get; set; }

    public IReadOnlyList<MaterialCategory> Categories { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var material = await FindMaterialAsync(cancellationToken);
        if (material is null)
        {
            return NotFound();
        }

        Code = material.Code;
        Name = material.Name;
        Description = material.Description;
        Unit = material.Unit;
        CategoryId = material.CategoryId;
        TrackingMode = material.TrackingMode;
        MinimumStock = material.MinimumStock;
        await LoadCategoriesAsync(material.CategoryId, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var material = await FindMaterialAsync(cancellationToken);
        if (material is null)
        {
            return NotFound();
        }

        var displayCode = Code?.Trim() ?? string.Empty;
        var displayName = Name?.Trim() ?? string.Empty;
        var displayUnit = Unit?.Trim() ?? string.Empty;
        var normalizedCode = displayCode.ToUpperInvariant();
        if (!ModelState.IsValid || displayCode.Length is < 1 or > 40
            || displayName.Length is < 2 or > 120 || displayUnit.Length is < 1 or > 20
            || !Enum.IsDefined(TrackingMode))
        {
            await LoadCategoriesAsync(material.CategoryId, cancellationToken);
            return Page();
        }

        var categoryIsAvailable = await db.MaterialCategories.AnyAsync(
            category => category.Id == CategoryId
                && category.SectorId == PilotSectorId
                && (category.IsActive || category.Id == material.CategoryId),
            cancellationToken);
        if (!categoryIsAvailable)
        {
            ModelState.AddModelError(nameof(CategoryId), "Selecione uma categoria ativa do setor TI.");
            await LoadCategoriesAsync(material.CategoryId, cancellationToken);
            return Page();
        }

        var codeAlreadyExists = await db.Materials.AnyAsync(
            item => item.SectorId == PilotSectorId
                && item.Id != material.Id
                && item.NormalizedCode == normalizedCode,
            cancellationToken);
        if (codeAlreadyExists)
        {
            ModelState.AddModelError(nameof(Code), "Já existe um material com esse código no setor TI.");
            await LoadCategoriesAsync(material.CategoryId, cancellationToken);
            return Page();
        }

        if (TrackingMode != material.TrackingMode)
        {
            var hasInventoryHistory = await db.InventoryMovements
                .AnyAsync(movement => movement.MaterialId == material.Id
                    && movement.SectorId == PilotSectorId, cancellationToken);
            var hasIndividualAssets = await db.InventoryAssets
                .AnyAsync(asset => asset.MaterialId == material.Id
                    && asset.SectorId == PilotSectorId, cancellationToken);
            if (hasInventoryHistory || hasIndividualAssets)
            {
                ModelState.AddModelError(nameof(TrackingMode),
                    "O tipo de controle não pode ser alterado após movimentações ou cadastro de ativos.");
                await LoadCategoriesAsync(material.CategoryId, cancellationToken);
                return Page();
            }
        }

        material.Code = displayCode;
        material.NormalizedCode = normalizedCode;
        material.Name = displayName;
        material.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        material.Unit = displayUnit;
        material.CategoryId = CategoryId;
        material.TrackingMode = TrackingMode;
        material.MinimumStock = MinimumStock;
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage("/Materiais/Index");
    }

    private Task<Material?> FindMaterialAsync(CancellationToken cancellationToken) =>
        db.Materials.SingleOrDefaultAsync(
            material => material.Id == Id && material.SectorId == PilotSectorId,
            cancellationToken);

    private async Task LoadCategoriesAsync(int currentCategoryId, CancellationToken cancellationToken)
    {
        Categories = await db.MaterialCategories
            .AsNoTracking()
            .Where(category => category.SectorId == PilotSectorId
                && (category.IsActive || category.Id == currentCategoryId))
            .OrderBy(category => category.Name)
            .ToListAsync(cancellationToken);
    }
}
