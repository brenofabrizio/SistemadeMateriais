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

namespace SistemaMateriais.Web.Pages.Ativos;

[Authorize(Policy = InventoryPolicies.OperateStock)]
public sealed class IndexModel(ApplicationDbContext db) : PageModel
{
    private const int PilotSectorId = 1;
    private const string SubmissionIndexName = "IX_InventoryMovements_SectorId_ActorUserId_SubmissionId";

    [BindProperty]
    [Range(1, int.MaxValue, ErrorMessage = "Selecione um material controlado por unidade individual.")]
    public int MaterialId { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Informe o número de série ou patrimônio.")]
    [StringLength(120, ErrorMessage = "O identificador aceita até 120 caracteres.")]
    public string Identifier { get; set; } = string.Empty;

    [BindProperty]
    [StringLength(200, ErrorMessage = "A referência aceita até 200 caracteres.")]
    public string? Reference { get; set; }

    [BindProperty]
    public int AssetId { get; set; }

    [BindProperty]
    public Guid AssetRegistrationSubmissionId { get; set; }

    [BindProperty]
    public Guid AssetLoanSubmissionId { get; set; }

    [BindProperty]
    public Guid AssetConsumptionSubmissionId { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Informe quem está recebendo o ativo.")]
    [StringLength(120, ErrorMessage = "O nome aceita até 120 caracteres.")]
    public string BorrowerName { get; set; } = string.Empty;

    [BindProperty]
    [DataType(DataType.Date)]
    public DateTime? DueDate { get; set; }

    [BindProperty]
    [StringLength(200, ErrorMessage = "A referência aceita até 200 caracteres.")]
    public string? LoanReference { get; set; }

    [BindProperty]
    [Range(1, int.MaxValue)]
    public int LoanId { get; set; }

    [BindProperty]
    public Guid AssetReturnSubmissionId { get; set; }

    public IReadOnlyList<Material> Materials { get; private set; } = [];
    public IReadOnlyList<InventoryAsset> Assets { get; private set; } = [];
    public IReadOnlyList<(InventoryLoan Loan, decimal Outstanding)> OpenAssetLoans { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        AssetRegistrationSubmissionId = Guid.NewGuid();
        await LoadPageDataAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostRegisterAsync(CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(AssetId));
        ModelState.Remove(nameof(BorrowerName));
        ModelState.Remove(nameof(DueDate));
        ModelState.Remove(nameof(LoanReference));
        ModelState.Remove(nameof(LoanId));
        Identifier = Identifier.Trim();
        Reference = string.IsNullOrWhiteSpace(Reference) ? null : Reference.Trim();
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorName = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(actorName))
        {
            return Forbid();
        }
        if (AssetRegistrationSubmissionId == Guid.Empty)
        {
            AssetRegistrationSubmissionId = Guid.NewGuid();
        }

        var normalizedIdentifier = Identifier.ToUpperInvariant();
        var submissionFingerprint = CreateSubmissionFingerprint(new
        {
            Operation = "asset-register/v1",
            MaterialId,
            Identifier = normalizedIdentifier,
            Reference
        });
        var existingSubmission = await FindSubmissionAsync(actorId, AssetRegistrationSubmissionId, cancellationToken);
        if (existingSubmission is not null)
        {
            return await ResolveExistingSubmissionAsync(existingSubmission, submissionFingerprint, cancellationToken);
        }

        var material = await db.Materials.SingleOrDefaultAsync(
            item => item.Id == MaterialId && item.SectorId == PilotSectorId && item.IsActive,
            cancellationToken);
        if (material is null)
        {
            ModelState.AddModelError(nameof(MaterialId), "Selecione um material ativo do setor TI.");
        }
        else if (material.TrackingMode != MaterialTrackingMode.IndividualAsset)
        {
            ModelState.AddModelError(nameof(MaterialId), "Selecione um material controlado por unidade individual.");
        }

        if (string.IsNullOrWhiteSpace(Identifier))
        {
            ModelState.AddModelError(nameof(Identifier), "Informe o número de série ou patrimônio.");
        }
        else if (await db.InventoryAssets.AnyAsync(
            asset => asset.SectorId == PilotSectorId && asset.NormalizedIdentifier == normalizedIdentifier,
            cancellationToken))
        {
            ModelState.AddModelError(nameof(Identifier), "Já existe um ativo com esse número de série ou patrimônio neste setor.");
        }
        if (!ModelState.IsValid)
        {
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (await db.InventoryAssets.AnyAsync(
                asset => asset.SectorId == PilotSectorId && asset.NormalizedIdentifier == normalizedIdentifier,
                cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                ModelState.AddModelError(nameof(Identifier), "Já existe um ativo com esse número de série ou patrimônio neste setor.");
                await LoadPageDataAsync(cancellationToken);
                return Page();
            }

            var now = DateTimeOffset.UtcNow;
            var assetRecord = new InventoryAsset
            {
                MaterialId = material!.Id,
                SectorId = PilotSectorId,
                Identifier = Identifier,
                NormalizedIdentifier = normalizedIdentifier,
                Status = InventoryAssetStatus.Available,
                RegisteredAtUtc = now,
                RegisteredByUserId = actorId,
                RegisteredByName = actorName
            };
            db.InventoryAssets.Add(assetRecord);
            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = material.Id,
                SectorId = PilotSectorId,
                Type = InventoryMovementType.Entry,
                Quantity = 1m,
                Reference = Reference,
                Asset = assetRecord,
                SubmissionId = AssetRegistrationSubmissionId,
                SubmissionFingerprint = submissionFingerprint,
                ActorUserId = actorId,
                ActorName = actorName,
                CreatedAtUtc = now
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RedirectToPage();
        }
        catch (Exception exception) when (
            PostgreSqlConcurrencyErrors.IsSerializationFailure(exception)
            || PostgreSqlConcurrencyErrors.IsUniqueConstraintViolation(
                exception,
                "IX_InventoryAssets_SectorId_NormalizedIdentifier")
            || PostgreSqlConcurrencyErrors.IsUniqueConstraintViolation(exception, SubmissionIndexName))
        {
            db.ChangeTracker.Clear();
            var concurrentSubmission = await FindSubmissionAsync(actorId, AssetRegistrationSubmissionId, cancellationToken);
            if (concurrentSubmission is not null)
            {
                return await ResolveExistingSubmissionAsync(concurrentSubmission, submissionFingerprint, cancellationToken);
            }

            ModelState.AddModelError(nameof(Identifier), "Já existe um ativo com esse número de série ou patrimônio neste setor.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostLoanAssetAsync(CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(MaterialId));
        ModelState.Remove(nameof(Identifier));
        ModelState.Remove(nameof(Reference));
        ModelState.Remove(nameof(LoanId));
        BorrowerName = BorrowerName.Trim();
        LoanReference = string.IsNullOrWhiteSpace(LoanReference) ? null : LoanReference.Trim();
        if (AssetId < 1)
        {
            ModelState.AddModelError(nameof(AssetId), "Selecione um ativo disponível.");
        }
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorName = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(actorName))
        {
            return Forbid();
        }
        if (AssetLoanSubmissionId == Guid.Empty)
        {
            AssetLoanSubmissionId = Guid.NewGuid();
        }
        var submissionFingerprint = CreateSubmissionFingerprint(new
        {
            Operation = "asset-loan/v1",
            AssetId,
            BorrowerName,
            DueDate = DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            LoanReference
        });
        var existingSubmission = await FindSubmissionAsync(actorId, AssetLoanSubmissionId, cancellationToken);
        if (existingSubmission is not null)
        {
            return await ResolveExistingSubmissionAsync(existingSubmission, submissionFingerprint, cancellationToken);
        }
        if (!ModelState.IsValid)
        {
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var asset = await db.InventoryAssets
                .Include(item => item.Material)
                .SingleOrDefaultAsync(item => item.Id == AssetId
                    && item.SectorId == PilotSectorId
                    && item.Status == InventoryAssetStatus.Available,
                    cancellationToken);
            if (asset is null || !asset.Material.IsActive || asset.Material.TrackingMode != MaterialTrackingMode.IndividualAsset)
            {
                await transaction.RollbackAsync(cancellationToken);
                ModelState.AddModelError(nameof(AssetId), "O ativo não está disponível para empréstimo.");
                await LoadPageDataAsync(cancellationToken);
                return Page();
            }

            var now = DateTimeOffset.UtcNow;
            var loan = new InventoryLoan
            {
                MaterialId = asset.MaterialId,
                SectorId = PilotSectorId,
                InventoryAssetId = asset.Id,
                BorrowerName = BorrowerName,
                Quantity = 1m,
                CreatedAtUtc = now,
                DueAtUtc = DueDate.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(DueDate.Value.Date, DateTimeKind.Utc))
                    : null,
                Reference = LoanReference,
                CreatedByUserId = actorId,
                CreatedByName = actorName
            };
            db.InventoryLoans.Add(loan);
            asset.Status = InventoryAssetStatus.Loaned;
            await db.SaveChangesAsync(cancellationToken);
            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = asset.MaterialId,
                SectorId = PilotSectorId,
                Type = InventoryMovementType.Loan,
                Quantity = 1m,
                Reference = loan.Reference ?? $"EM-{loan.Id}",
                InventoryLoanId = loan.Id,
                InventoryAssetId = asset.Id,
                SubmissionId = AssetLoanSubmissionId,
                SubmissionFingerprint = submissionFingerprint,
                ActorUserId = actorId,
                ActorName = actorName,
                CreatedAtUtc = now
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RedirectToPage();
        }
        catch (Exception exception) when (
            PostgreSqlConcurrencyErrors.IsSerializationFailure(exception)
            || PostgreSqlConcurrencyErrors.IsUniqueConstraintViolation(exception, SubmissionIndexName))
        {
            db.ChangeTracker.Clear();
            var concurrentSubmission = await FindSubmissionAsync(actorId, AssetLoanSubmissionId, cancellationToken);
            if (concurrentSubmission is not null)
            {
                return await ResolveExistingSubmissionAsync(concurrentSubmission, submissionFingerprint, cancellationToken);
            }
            ModelState.AddModelError(nameof(AssetId), "O ativo foi alterado por outra operação. Atualize a página e tente novamente.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostReturnAssetAsync(CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(MaterialId));
        ModelState.Remove(nameof(Identifier));
        ModelState.Remove(nameof(Reference));
        ModelState.Remove(nameof(AssetId));
        ModelState.Remove(nameof(BorrowerName));
        ModelState.Remove(nameof(DueDate));
        ModelState.Remove(nameof(LoanReference));
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

        if (AssetReturnSubmissionId == Guid.Empty)
        {
            AssetReturnSubmissionId = Guid.NewGuid();
        }
        var submissionFingerprint = CreateSubmissionFingerprint(new
        {
            Operation = "asset-return/v1",
            LoanId
        });
        var existingSubmission = await FindSubmissionAsync(actorId, AssetReturnSubmissionId, cancellationToken);
        if (existingSubmission is not null)
        {
            return await ResolveExistingSubmissionAsync(existingSubmission, submissionFingerprint, cancellationToken);
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var loan = await db.InventoryLoans
                .Include(item => item.Asset)
                .SingleOrDefaultAsync(item => item.Id == LoanId
                    && item.SectorId == PilotSectorId
                    && item.InventoryAssetId.HasValue,
                    cancellationToken);
            if (loan?.Asset is null || loan.Asset.Status != InventoryAssetStatus.Loaned)
            {
                await transaction.RollbackAsync(cancellationToken);
                return NotFound();
            }
            var alreadyReturned = await db.InventoryMovements
                .Where(movement => movement.InventoryLoanId == loan.Id && movement.Type == InventoryMovementType.Return)
                .SumAsync(movement => movement.Quantity, cancellationToken);
            if (alreadyReturned >= 1m)
            {
                await transaction.RollbackAsync(cancellationToken);
                ModelState.AddModelError(string.Empty, "Esse ativo já foi devolvido.");
                await LoadPageDataAsync(cancellationToken);
                return Page();
            }

            loan.Asset.Status = InventoryAssetStatus.Available;
            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = loan.MaterialId,
                SectorId = PilotSectorId,
                Type = InventoryMovementType.Return,
                Quantity = 1m,
                Reference = loan.Reference ?? $"EM-{loan.Id}",
                InventoryLoanId = loan.Id,
                InventoryAssetId = loan.Asset.Id,
                SubmissionId = AssetReturnSubmissionId,
                SubmissionFingerprint = submissionFingerprint,
                ActorUserId = actorId,
                ActorName = actorName,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RedirectToPage();
        }
        catch (Exception exception) when (
            PostgreSqlConcurrencyErrors.IsSerializationFailure(exception)
            || PostgreSqlConcurrencyErrors.IsUniqueConstraintViolation(exception, SubmissionIndexName))
        {
            db.ChangeTracker.Clear();
            var concurrentSubmission = await FindSubmissionAsync(actorId, AssetReturnSubmissionId, cancellationToken);
            if (concurrentSubmission is not null)
            {
                return await ResolveExistingSubmissionAsync(concurrentSubmission, submissionFingerprint, cancellationToken);
            }
            ModelState.AddModelError(nameof(LoanId), "O empréstimo foi alterado por outra operação. Atualize a página e tente novamente.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostConsumeAssetAsync(CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(MaterialId));
        ModelState.Remove(nameof(Identifier));
        ModelState.Remove(nameof(BorrowerName));
        ModelState.Remove(nameof(DueDate));
        ModelState.Remove(nameof(LoanReference));
        ModelState.Remove(nameof(LoanId));
        Reference = string.IsNullOrWhiteSpace(Reference) ? null : Reference.Trim();
        if (AssetId < 1)
        {
            ModelState.AddModelError(nameof(AssetId), "Selecione um ativo disponível.");
        }
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorName = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(actorName))
        {
            return Forbid();
        }
        if (AssetConsumptionSubmissionId == Guid.Empty)
        {
            AssetConsumptionSubmissionId = Guid.NewGuid();
        }
        var submissionFingerprint = CreateSubmissionFingerprint(new
        {
            Operation = "asset-consume/v1",
            AssetId,
            Reference
        });
        var existingSubmission = await FindSubmissionAsync(actorId, AssetConsumptionSubmissionId, cancellationToken);
        if (existingSubmission is not null)
        {
            return await ResolveExistingSubmissionAsync(existingSubmission, submissionFingerprint, cancellationToken);
        }
        if (!ModelState.IsValid)
        {
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var asset = await db.InventoryAssets
                .Include(item => item.Material)
                .SingleOrDefaultAsync(item => item.Id == AssetId
                    && item.SectorId == PilotSectorId
                    && item.Status == InventoryAssetStatus.Available,
                    cancellationToken);
            if (asset is null || !asset.Material.IsActive || asset.Material.TrackingMode != MaterialTrackingMode.IndividualAsset)
            {
                await transaction.RollbackAsync(cancellationToken);
                ModelState.AddModelError(nameof(AssetId), "O ativo não está disponível para consumo.");
                await LoadPageDataAsync(cancellationToken);
                return Page();
            }

            var now = DateTimeOffset.UtcNow;
            asset.Status = InventoryAssetStatus.Consumed;
            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = asset.MaterialId,
                SectorId = PilotSectorId,
                Type = InventoryMovementType.Consumption,
                Quantity = 1m,
                Reference = Reference,
                InventoryAssetId = asset.Id,
                SubmissionId = AssetConsumptionSubmissionId,
                SubmissionFingerprint = submissionFingerprint,
                ActorUserId = actorId,
                ActorName = actorName,
                CreatedAtUtc = now
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RedirectToPage();
        }
        catch (Exception exception) when (
            PostgreSqlConcurrencyErrors.IsSerializationFailure(exception)
            || PostgreSqlConcurrencyErrors.IsUniqueConstraintViolation(exception, SubmissionIndexName))
        {
            db.ChangeTracker.Clear();
            var concurrentSubmission = await FindSubmissionAsync(actorId, AssetConsumptionSubmissionId, cancellationToken);
            if (concurrentSubmission is not null)
            {
                return await ResolveExistingSubmissionAsync(concurrentSubmission, submissionFingerprint, cancellationToken);
            }
            ModelState.AddModelError(nameof(AssetId), "O ativo foi alterado por outra operação. Atualize a página e tente novamente.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }
    }

    private static string CreateSubmissionFingerprint<T>(T payload)
    {
        var serializedPayload = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serializedPayload)));
    }

    private Task<InventoryMovement?> FindSubmissionAsync(
        string actorId,
        Guid submissionId,
        CancellationToken cancellationToken) => db.InventoryMovements
        .AsNoTracking()
        .SingleOrDefaultAsync(movement => movement.SectorId == PilotSectorId
            && movement.ActorUserId == actorId
            && movement.SubmissionId == submissionId,
            cancellationToken);

    private async Task<IActionResult> ResolveExistingSubmissionAsync(
        InventoryMovement existingSubmission,
        string submissionFingerprint,
        CancellationToken cancellationToken)
    {
        if (existingSubmission.SubmissionFingerprint == submissionFingerprint)
        {
            return RedirectToPage();
        }

        Response.StatusCode = StatusCodes.Status409Conflict;
        ModelState.AddModelError(string.Empty, "A chave de envio já foi usada com dados diferentes.");
        await LoadPageDataAsync(cancellationToken);
        return Page();
    }

    private async Task LoadPageDataAsync(CancellationToken cancellationToken)
    {
        Materials = await db.Materials
            .AsNoTracking()
            .Where(material => material.SectorId == PilotSectorId
                && material.IsActive
                && material.TrackingMode == MaterialTrackingMode.IndividualAsset)
            .OrderBy(material => material.Name)
            .ToListAsync(cancellationToken);
        Assets = await db.InventoryAssets
            .AsNoTracking()
            .Include(asset => asset.Material)
            .Where(asset => asset.SectorId == PilotSectorId)
            .OrderByDescending(asset => asset.Id)
            .Take(500)
            .ToListAsync(cancellationToken);

        var loans = await db.InventoryLoans
            .AsNoTracking()
            .Include(loan => loan.Asset)
            .Where(loan => loan.SectorId == PilotSectorId && loan.InventoryAssetId.HasValue)
            .OrderByDescending(loan => loan.Id)
            .ToListAsync(cancellationToken);
        var returnedByLoan = await db.InventoryMovements
            .AsNoTracking()
            .Where(movement => movement.SectorId == PilotSectorId
                && movement.InventoryLoanId.HasValue
                && movement.Type == InventoryMovementType.Return)
            .GroupBy(movement => movement.InventoryLoanId!.Value)
            .Select(group => new { LoanId = group.Key, Quantity = group.Sum(movement => movement.Quantity) })
            .ToDictionaryAsync(item => item.LoanId, item => item.Quantity, cancellationToken);
        OpenAssetLoans = loans
            .Select(loan => (Loan: loan, Outstanding: loan.Quantity - returnedByLoan.GetValueOrDefault(loan.Id)))
            .Where(item => item.Outstanding > 0)
            .ToList();
    }
}
