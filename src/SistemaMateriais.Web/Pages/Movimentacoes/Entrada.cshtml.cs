using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SistemaMateriais.Web.Authorization;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Pages.Movimentacoes;

[Authorize(Policy = InventoryPolicies.OperateStock)]
public sealed class EntradaModel(ApplicationDbContext db) : PageModel
{
    private const int PilotSectorId = 1;

    [BindProperty]
    [Range(1, int.MaxValue, ErrorMessage = "Selecione um material ativo.")]
    public int MaterialId { get; set; }

    [BindProperty]
    [Range(typeof(decimal), "0.001", "999999999.999", ParseLimitsInInvariantCulture = true,
        ErrorMessage = "A quantidade deve ser maior que zero.")]
    public decimal Quantity { get; set; }

    [BindProperty]
    [StringLength(200, ErrorMessage = "A referência aceita até 200 caracteres.")]
    public string? Reference { get; set; }

    [BindProperty]
    public Guid SubmissionId { get; set; }

    public IReadOnlyList<Material> Materials { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        SubmissionId = Guid.NewGuid();
        await LoadMaterialsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var actorName = User.Identity?.Name ?? actorId;
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(actorName))
        {
            return Forbid();
        }

        if (SubmissionId == Guid.Empty)
        {
            return BadRequest();
        }

        var normalizedReference = string.IsNullOrWhiteSpace(Reference) ? null : Reference.Trim();
        var submissionFingerprint = CreateSubmissionFingerprint(normalizedReference);
        var existingSubmission = await db.InventoryMovements
            .AsNoTracking()
            .SingleOrDefaultAsync(movement => movement.SectorId == PilotSectorId
                && movement.ActorUserId == actorId
                && movement.SubmissionId == SubmissionId, cancellationToken);
        if (existingSubmission is not null)
        {
            return await ResolveExistingSubmissionAsync(existingSubmission, submissionFingerprint, cancellationToken);
        }

        if (Quantity > 999999999.999m || decimal.Round(Quantity, 3) != Quantity)
        {
            ModelState.AddModelError(nameof(Quantity), "Use no máximo três casas decimais.");
        }

        var material = await db.Materials.SingleOrDefaultAsync(
            item => item.Id == MaterialId && item.SectorId == PilotSectorId && item.IsActive,
            cancellationToken);
        if (material is null)
        {
            ModelState.AddModelError(nameof(MaterialId), "Selecione um material ativo do setor TI.");
        }
        else if (material.TrackingMode != MaterialTrackingMode.Quantity)
        {
            ModelState.AddModelError(nameof(MaterialId), "Informe o número de série ou patrimônio na página de ativos.");
        }

        if (!ModelState.IsValid)
        {
            await LoadMaterialsAsync(cancellationToken);
            return Page();
        }

        var movement = new InventoryMovement
        {
            MaterialId = material!.Id,
            SectorId = PilotSectorId,
            Type = InventoryMovementType.Entry,
            Quantity = Quantity,
            Reference = normalizedReference,
            SubmissionId = SubmissionId,
            SubmissionFingerprint = submissionFingerprint,
            ActorUserId = actorId,
            ActorName = actorName,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.InventoryMovements.Add(movement);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgreSqlConcurrencyErrors.IsUniqueConstraintViolation(
            exception, "IX_InventoryMovements_SectorId_ActorUserId_SubmissionId"))
        {
            db.Entry(movement).State = EntityState.Detached;
            var concurrentSubmission = await db.InventoryMovements
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.SectorId == PilotSectorId
                    && item.ActorUserId == actorId
                    && item.SubmissionId == SubmissionId, cancellationToken);
            if (concurrentSubmission is null)
            {
                throw;
            }

            return await ResolveExistingSubmissionAsync(concurrentSubmission, submissionFingerprint, cancellationToken);
        }

        return RedirectToPage();
    }

    private async Task<IActionResult> ResolveExistingSubmissionAsync(
        InventoryMovement existingSubmission,
        string submissionFingerprint,
        CancellationToken cancellationToken)
    {
        if (string.Equals(existingSubmission.SubmissionFingerprint, submissionFingerprint, StringComparison.Ordinal))
        {
            return RedirectToPage();
        }

        Response.StatusCode = StatusCodes.Status409Conflict;
        ModelState.AddModelError(nameof(SubmissionId),
            "Esta solicitação já foi processada com outros dados. Atualize a página antes de tentar novamente.");
        await LoadMaterialsAsync(cancellationToken);
        return Page();
    }

    private string CreateSubmissionFingerprint(string? normalizedReference)
    {
        var canonicalPayload = JsonSerializer.Serialize(new
        {
            Operation = "stock-entry/v1",
            MaterialId,
            Quantity = Quantity.ToString("G29", CultureInfo.InvariantCulture),
            Reference = normalizedReference
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload));
        return Convert.ToHexString(hash);
    }

    private async Task LoadMaterialsAsync(CancellationToken cancellationToken)
    {
        Materials = await db.Materials
            .AsNoTracking()
            .Where(item => item.SectorId == PilotSectorId
                && item.IsActive
                && item.TrackingMode == MaterialTrackingMode.Quantity)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
    }
}
