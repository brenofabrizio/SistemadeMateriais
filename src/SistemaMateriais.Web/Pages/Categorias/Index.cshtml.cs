using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SistemaMateriais.Web.Authorization;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Pages.Categorias;

public sealed class IndexModel(ApplicationDbContext db, IAuthorizationService authorization) : PageModel
{
    private const int PilotSectorId = 1;

    [BindProperty]
    [Required(ErrorMessage = "Informe o nome da categoria.")]
    [StringLength(80, MinimumLength = 2, ErrorMessage = "Use entre 2 e 80 caracteres.")]
    public string CategoryName { get; set; } = string.Empty;

    [BindProperty]
    public int EditId { get; set; }

    [BindProperty]
    public int DeactivateId { get; set; }

    [BindProperty]
    public int ActivateId { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Informe o nome da categoria.")]
    [StringLength(80, MinimumLength = 2, ErrorMessage = "Use entre 2 e 80 caracteres.")]
    public string EditName { get; set; } = string.Empty;

    public IReadOnlyList<MaterialCategory> Categories { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadCategoriesAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        var authorized = await authorization.AuthorizeAsync(User, InventoryPolicies.ManageCatalog);
        if (!authorized.Succeeded)
        {
            return Forbid();
        }

        var displayName = CategoryName?.Trim() ?? string.Empty;
        ModelState.Remove(nameof(EditName));
        if (!ModelState.IsValid || displayName.Length is < 2 or > 80)
        {
            if (displayName.Length is < 2 or > 80 && ModelState.IsValid)
            {
                ModelState.AddModelError(nameof(CategoryName), "Use entre 2 e 80 caracteres.");
            }

            await LoadCategoriesAsync(cancellationToken);
            return Page();
        }

        var normalizedName = displayName.ToUpperInvariant();
        var alreadyExists = await db.MaterialCategories.AnyAsync(
            category => category.SectorId == PilotSectorId && category.NormalizedName == normalizedName,
            cancellationToken);
        if (alreadyExists)
        {
            ModelState.AddModelError(nameof(CategoryName), "Já existe uma categoria com esse nome neste setor.");
            await LoadCategoriesAsync(cancellationToken);
            return Page();
        }

        db.MaterialCategories.Add(new MaterialCategory
        {
            Name = displayName,
            NormalizedName = normalizedName,
            SectorId = PilotSectorId
        });
        await db.SaveChangesAsync(cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEditAsync(CancellationToken cancellationToken)
    {
        var authorized = await authorization.AuthorizeAsync(User, InventoryPolicies.ManageCatalog);
        if (!authorized.Succeeded)
        {
            return Forbid();
        }

        ModelState.Remove(nameof(CategoryName));
        var displayName = EditName?.Trim() ?? string.Empty;
        if (!ModelState.IsValid || displayName.Length is < 2 or > 80)
        {
            if (displayName.Length is < 2 or > 80 && ModelState.IsValid)
            {
                ModelState.AddModelError(string.Empty, "Use entre 2 e 80 caracteres.");
            }

            await LoadCategoriesAsync(cancellationToken);
            return Page();
        }

        var category = await db.MaterialCategories.SingleOrDefaultAsync(
            item => item.Id == EditId && item.SectorId == PilotSectorId,
            cancellationToken);
        if (category is null)
        {
            return NotFound();
        }

        var normalizedName = displayName.ToUpperInvariant();
        var duplicate = await db.MaterialCategories.AnyAsync(
            item => item.SectorId == PilotSectorId
                && item.Id != EditId
                && item.NormalizedName == normalizedName,
            cancellationToken);
        if (duplicate)
        {
            ModelState.AddModelError(string.Empty, "Já existe uma categoria com esse nome neste setor.");
            await LoadCategoriesAsync(cancellationToken);
            return Page();
        }

        category.Name = displayName;
        category.NormalizedName = normalizedName;
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

        var category = await db.MaterialCategories.SingleOrDefaultAsync(
            item => item.Id == DeactivateId && item.SectorId == PilotSectorId,
            cancellationToken);
        if (category is null)
        {
            return NotFound();
        }

        category.IsActive = false;
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

        var category = await db.MaterialCategories.SingleOrDefaultAsync(
            item => item.Id == ActivateId && item.SectorId == PilotSectorId,
            cancellationToken);
        if (category is null)
        {
            return NotFound();
        }

        category.IsActive = true;
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage();
    }

    private async Task LoadCategoriesAsync(CancellationToken cancellationToken)
    {
        Categories = await db.MaterialCategories
            .AsNoTracking()
            .Where(category => category.SectorId == PilotSectorId)
            .OrderBy(category => category.Name)
            .ToListAsync(cancellationToken);
    }
}
