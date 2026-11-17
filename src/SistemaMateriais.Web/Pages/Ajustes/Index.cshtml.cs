using System.ComponentModel.DataAnnotations;
using System.Data;
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

namespace SistemaMateriais.Web.Pages.Ajustes;

[Authorize(Policy = InventoryPolicies.OperateStock)]
public sealed class IndexModel(ApplicationDbContext db) : PageModel
{
    private const int PilotSectorId = 1;
    private const string SubmissionIndexName = "IX_AdjustmentRequests_SectorId_RequestedByUserId_SubmissionId";

    [BindProperty]
    [Range(1, int.MaxValue, ErrorMessage = "Selecione um material ativo.")]
    public int MaterialId { get; set; }

    [BindProperty]
    [Range(typeof(decimal), "-999999999.999", "999999999.999", ParseLimitsInInvariantCulture = true,
        ErrorMessage = "A diferença excede o limite permitido.")]
    public decimal DeltaQuantity { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Informe a justificativa do ajuste.")]
    [StringLength(500, ErrorMessage = "A justificativa aceita até 500 caracteres.")]
    public string Reason { get; set; } = string.Empty;

    [BindProperty]
    [Range(1, int.MaxValue)]
    public int RequestId { get; set; }

    [BindProperty]
    public Guid SubmissionId { get; set; }

    public bool CanDecide { get; private set; }
    public IReadOnlyList<Material> Materials { get; private set; } = [];
    public IReadOnlyList<InventoryAdjustmentRequest> Requests { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        SubmissionId = Guid.NewGuid();
        await LoadPageDataAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSubmitAsync(CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(RequestId));
        if (DeltaQuantity == 0)
        {
            ModelState.AddModelError(nameof(DeltaQuantity), "Informe uma diferença diferente de zero.");
        }
        if (DeltaQuantity > 999999999.999m || DeltaQuantity < -999999999.999m
            || decimal.Round(DeltaQuantity, 3) != DeltaQuantity)
        {
            ModelState.AddModelError(nameof(DeltaQuantity), "Use no máximo três casas decimais dentro do limite permitido.");
        }

        var material = await db.Materials.SingleOrDefaultAsync(
            item => item.Id == MaterialId && item.SectorId == PilotSectorId && item.IsActive,
            cancellationToken);
        if (material is null)
        {
            ModelState.AddModelError(nameof(MaterialId), "Selecione um material ativo do setor TI.");
        }

        Reason = Reason.Trim();
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorName = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(actorName))
        {
            return Forbid();
        }
        Guid? requestSubmissionId = SubmissionId == Guid.Empty ? null : SubmissionId;
        var submissionFingerprint = CreateSubmissionFingerprint(MaterialId, DeltaQuantity, Reason);
        if (requestSubmissionId.HasValue)
        {
            var existingSubmission = await FindSubmissionAsync(actorId, requestSubmissionId.Value, cancellationToken);
            if (existingSubmission is not null)
            {
                return await ResolveExistingSubmissionAsync(existingSubmission, submissionFingerprint, cancellationToken);
            }
        }
        if (!ModelState.IsValid)
        {
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }

        var canDecide = IsApprover();
        var now = DateTimeOffset.UtcNow;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (canDecide && await GetBalanceAsync(material!.Id, cancellationToken) + DeltaQuantity < 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                ModelState.AddModelError(string.Empty, "O ajuste não pode deixar o saldo abaixo de zero.");
                await LoadPageDataAsync(cancellationToken);
                return Page();
            }

            var request = new InventoryAdjustmentRequest
            {
                MaterialId = material!.Id,
                SectorId = PilotSectorId,
                DeltaQuantity = DeltaQuantity,
                Reason = Reason,
                Status = canDecide ? InventoryAdjustmentStatus.Approved : InventoryAdjustmentStatus.Pending,
                RequestedByUserId = actorId,
                RequestedByName = actorName,
                SubmissionId = requestSubmissionId,
                SubmissionFingerprint = requestSubmissionId.HasValue ? submissionFingerprint : null,
                RequestedAtUtc = now,
                DecidedByUserId = canDecide ? actorId : null,
                DecidedByName = canDecide ? actorName : null,
                DecidedAtUtc = canDecide ? now : null
            };
            db.InventoryAdjustmentRequests.Add(request);
            await db.SaveChangesAsync(cancellationToken);
            if (canDecide)
            {
                AddAdjustmentMovement(request, actorId, actorName, now);
                await db.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return RedirectToPage();
        }
        catch (Exception exception) when (
            PostgreSqlConcurrencyErrors.IsUniqueConstraintViolation(exception, SubmissionIndexName)
            || PostgreSqlConcurrencyErrors.IsSerializationFailure(exception))
        {
            db.ChangeTracker.Clear();
            if (requestSubmissionId.HasValue)
            {
                var concurrentSubmission = await FindSubmissionAsync(actorId, requestSubmissionId.Value, cancellationToken);
                if (concurrentSubmission is not null)
                {
                    return await ResolveExistingSubmissionAsync(concurrentSubmission, submissionFingerprint, cancellationToken);
                }
            }
            ModelState.AddModelError(string.Empty,
                "O saldo foi alterado por outra operação. Atualize a página e tente novamente.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostApproveAsync(CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(MaterialId));
        ModelState.Remove(nameof(DeltaQuantity));
        ModelState.Remove(nameof(Reason));
        if (!IsApprover())
        {
            return Forbid();
        }
        if (!ModelState.IsValid)
        {
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }

        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorName = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(actorName))
        {
            return Forbid();
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var request = await db.InventoryAdjustmentRequests.SingleOrDefaultAsync(
                item => item.Id == RequestId && item.SectorId == PilotSectorId
                    && item.Status == InventoryAdjustmentStatus.Pending,
                cancellationToken);
            if (request is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return NotFound();
            }
            if (request.RequestedByUserId == actorId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Forbid();
            }

            var currentBalance = await GetBalanceAsync(request.MaterialId, cancellationToken);
            if (currentBalance + request.DeltaQuantity < 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                ModelState.AddModelError(string.Empty, "A aprovação deixaria o saldo abaixo de zero; o pedido continua pendente.");
                await LoadPageDataAsync(cancellationToken);
                return Page();
            }

            var now = DateTimeOffset.UtcNow;
            request.Status = InventoryAdjustmentStatus.Approved;
            request.DecidedByUserId = actorId;
            request.DecidedByName = actorName;
            request.DecidedAtUtc = now;
            AddAdjustmentMovement(request, actorId, actorName, now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RedirectToPage();
        }
        catch (Exception exception) when (PostgreSqlConcurrencyErrors.IsSerializationFailure(exception))
        {
            db.ChangeTracker.Clear();
            ModelState.AddModelError(string.Empty,
                "O pedido foi alterado por outra operação. Atualize a página e tente novamente.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRejectAsync(CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(MaterialId));
        ModelState.Remove(nameof(DeltaQuantity));
        ModelState.Remove(nameof(Reason));
        if (!IsApprover())
        {
            return Forbid();
        }
        if (!ModelState.IsValid)
        {
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }

        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorName = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(actorName))
        {
            return Forbid();
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var request = await db.InventoryAdjustmentRequests.SingleOrDefaultAsync(
                item => item.Id == RequestId && item.SectorId == PilotSectorId
                    && item.Status == InventoryAdjustmentStatus.Pending,
                cancellationToken);
            if (request is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return NotFound();
            }
            request.Status = InventoryAdjustmentStatus.Rejected;
            request.DecidedByUserId = actorId;
            request.DecidedByName = actorName;
            request.DecidedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RedirectToPage();
        }
        catch (Exception exception) when (PostgreSqlConcurrencyErrors.IsSerializationFailure(exception))
        {
            db.ChangeTracker.Clear();
            ModelState.AddModelError(string.Empty,
                "O pedido foi alterado por outra operação. Atualize a página e tente novamente.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }
    }

    private Task<InventoryAdjustmentRequest?> FindSubmissionAsync(
        string actorId,
        Guid submissionId,
        CancellationToken cancellationToken) => db.InventoryAdjustmentRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(request => request.SectorId == PilotSectorId
                && request.RequestedByUserId == actorId
                && request.SubmissionId == submissionId, cancellationToken);

    private async Task<IActionResult> ResolveExistingSubmissionAsync(
        InventoryAdjustmentRequest existingSubmission,
        string submissionFingerprint,
        CancellationToken cancellationToken)
    {
        if (string.Equals(existingSubmission.SubmissionFingerprint, submissionFingerprint, StringComparison.Ordinal))
        {
            return RedirectToPage();
        }

        Response.StatusCode = StatusCodes.Status409Conflict;
        ModelState.AddModelError(string.Empty,
            "Esta solicitação já foi processada com outros dados. Atualize a página antes de tentar novamente.");
        await LoadPageDataAsync(cancellationToken);
        return Page();
    }

    private static string CreateSubmissionFingerprint(int materialId, decimal deltaQuantity, string reason)
    {
        var canonicalPayload = JsonSerializer.Serialize(new
        {
            Operation = "inventory-adjustment/v1",
            MaterialId = materialId,
            DeltaQuantity = deltaQuantity.ToString("G29", CultureInfo.InvariantCulture),
            Reason = reason
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload)));
    }

    private void AddAdjustmentMovement(
        InventoryAdjustmentRequest request,
        string actorId,
        string actorName,
        DateTimeOffset createdAtUtc)
    {
        db.InventoryMovements.Add(new InventoryMovement
        {
            MaterialId = request.MaterialId,
            SectorId = PilotSectorId,
            Type = InventoryMovementType.Adjustment,
            Quantity = request.DeltaQuantity,
            Reference = $"Ajuste {request.Id}",
            InventoryAdjustmentRequestId = request.Id,
            SubmissionId = request.RequestedByUserId == actorId ? request.SubmissionId : null,
            SubmissionFingerprint = request.RequestedByUserId == actorId ? request.SubmissionFingerprint : null,
            ActorUserId = actorId,
            ActorName = actorName,
            CreatedAtUtc = createdAtUtc
        });
    }

    private async Task<decimal> GetBalanceAsync(int materialId, CancellationToken cancellationToken) =>
        await db.InventoryMovements
            .Where(movement => movement.MaterialId == materialId && movement.SectorId == PilotSectorId)
            .Select(movement => movement.Type == InventoryMovementType.Entry || movement.Type == InventoryMovementType.Return
                ? movement.Quantity
                : movement.Type == InventoryMovementType.Consumption || movement.Type == InventoryMovementType.Loan
                    ? -movement.Quantity
                    : movement.Quantity)
            .SumAsync(cancellationToken);

    private bool IsApprover() => User.IsInRole(InventoryRoles.Administrator) || User.IsInRole(InventoryRoles.Manager);

    private async Task LoadPageDataAsync(CancellationToken cancellationToken)
    {
        CanDecide = IsApprover();
        Materials = await db.Materials
            .AsNoTracking()
            .Where(material => material.SectorId == PilotSectorId && material.IsActive)
            .OrderBy(material => material.Name)
            .ToListAsync(cancellationToken);

        var query = db.InventoryAdjustmentRequests
            .AsNoTracking()
            .Include(request => request.Material)
            .Where(request => request.SectorId == PilotSectorId);
        if (!CanDecide)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            query = query.Where(request => request.RequestedByUserId == userId);
        }
        Requests = await query
            .OrderByDescending(request => request.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
    }
}
