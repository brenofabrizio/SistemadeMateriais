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

namespace SistemaMateriais.Web.Pages.Emprestimos;

[Authorize(Policy = InventoryPolicies.OperateStock)]
public sealed class IndexModel(ApplicationDbContext db) : PageModel
{
    private const int PilotSectorId = 1;
    private const string SubmissionIndexName = "IX_InventoryMovements_SectorId_ActorUserId_SubmissionId";

    [BindProperty]
    [Range(1, int.MaxValue, ErrorMessage = "Selecione um material ativo.")]
    public int MaterialId { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Informe quem está recebendo o material.")]
    [StringLength(120, ErrorMessage = "O nome aceita até 120 caracteres.")]
    public string BorrowerName { get; set; } = string.Empty;

    [BindProperty]
    [Range(typeof(decimal), "0.001", "999999999.999", ParseLimitsInInvariantCulture = true,
        ErrorMessage = "A quantidade deve ser maior que zero.")]
    public decimal Quantity { get; set; }

    [BindProperty]
    [DataType(DataType.Date)]
    public DateTime? DueDate { get; set; }

    [BindProperty]
    [StringLength(200, ErrorMessage = "A referência aceita até 200 caracteres.")]
    public string? Reference { get; set; }

    [BindProperty]
    public Guid LoanSubmissionId { get; set; }

    [BindProperty]
    public Guid ReturnSubmissionId { get; set; }

    [BindProperty]
    [Range(1, int.MaxValue)]
    public int LoanId { get; set; }

    [BindProperty]
    [Range(typeof(decimal), "0.001", "999999999.999", ParseLimitsInInvariantCulture = true,
        ErrorMessage = "A quantidade devolvida deve ser maior que zero.")]
    public decimal ReturnQuantity { get; set; }

    public IReadOnlyList<Material> Materials { get; private set; } = [];
    public IReadOnlyList<(InventoryLoan Loan, decimal Outstanding)> OpenLoans { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        LoanSubmissionId = Guid.NewGuid();
        await LoadPageDataAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostLoanAsync(CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(LoanId));
        ModelState.Remove(nameof(ReturnQuantity));
        ModelState.Remove(nameof(ReturnSubmissionId));

        var currentActorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(currentActorId) || string.IsNullOrWhiteSpace(User.Identity?.Name))
        {
            return Forbid();
        }

        if (LoanSubmissionId == Guid.Empty)
        {
            return BadRequest();
        }

        BorrowerName = BorrowerName.Trim();
        Reference = string.IsNullOrWhiteSpace(Reference) ? null : Reference.Trim();
        var submissionFingerprint = CreateLoanSubmissionFingerprint(
            MaterialId, BorrowerName, Quantity, DueDate, Reference);
        var existingSubmission = await FindSubmissionAsync(currentActorId, LoanSubmissionId, cancellationToken);
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
            ModelState.AddModelError(nameof(MaterialId), "Use a página Ativos individualizados para emprestar este patrimônio.");
        }

        BorrowerName = BorrowerName.Trim();
        Reference = string.IsNullOrWhiteSpace(Reference) ? null : Reference.Trim();
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
            var currentBalance = await GetBalanceAsync(material!.Id, cancellationToken);
            if (currentBalance < Quantity)
            {
                await transaction.RollbackAsync(cancellationToken);
                ModelState.AddModelError(nameof(Quantity), $"Saldo insuficiente. Disponível: {currentBalance:0.###}.");
                await LoadPageDataAsync(cancellationToken);
                return Page();
            }

            var loan = new InventoryLoan
            {
                MaterialId = material.Id,
                SectorId = PilotSectorId,
                BorrowerName = BorrowerName,
                Quantity = Quantity,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                DueAtUtc = DueDate.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(DueDate.Value.Date, DateTimeKind.Utc))
                    : null,
                Reference = Reference,
                CreatedByUserId = actorId,
                CreatedByName = actorName
            };
            db.InventoryLoans.Add(loan);
            await db.SaveChangesAsync(cancellationToken);

            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = material.Id,
                SectorId = PilotSectorId,
                Type = InventoryMovementType.Loan,
                Quantity = Quantity,
                Reference = loan.Reference ?? $"EM-{loan.Id}",
                InventoryLoanId = loan.Id,
                SubmissionId = LoanSubmissionId,
                SubmissionFingerprint = submissionFingerprint,
                ActorUserId = actorId,
                ActorName = actorName,
                CreatedAtUtc = loan.CreatedAtUtc
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RedirectToPage();
        }
        catch (Exception exception) when (
            PostgreSqlConcurrencyErrors.IsUniqueConstraintViolation(exception, SubmissionIndexName)
            || PostgreSqlConcurrencyErrors.IsSerializationFailure(exception))
        {
            db.ChangeTracker.Clear();
            var concurrentSubmission = await FindSubmissionAsync(currentActorId, LoanSubmissionId, cancellationToken);
            if (concurrentSubmission is not null)
            {
                return await ResolveExistingSubmissionAsync(concurrentSubmission, submissionFingerprint, cancellationToken);
            }

            ModelState.AddModelError(string.Empty, "O estoque foi alterado por outra operação. Atualize a página e tente novamente.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostReturnAsync(CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(MaterialId));
        ModelState.Remove(nameof(BorrowerName));
        ModelState.Remove(nameof(Quantity));
        ModelState.Remove(nameof(DueDate));
        ModelState.Remove(nameof(Reference));
        ModelState.Remove(nameof(LoanSubmissionId));

        var submissionActorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(submissionActorId) || string.IsNullOrWhiteSpace(User.Identity?.Name))
        {
            return Forbid();
        }

        if (ReturnSubmissionId == Guid.Empty)
        {
            return BadRequest();
        }

        var submissionFingerprint = CreateReturnSubmissionFingerprint(LoanId, ReturnQuantity);
        var existingSubmission = await FindSubmissionAsync(submissionActorId, ReturnSubmissionId, cancellationToken);
        if (existingSubmission is not null)
        {
            return await ResolveExistingSubmissionAsync(existingSubmission, submissionFingerprint, cancellationToken);
        }

        if (ReturnQuantity > 999999999.999m || decimal.Round(ReturnQuantity, 3) != ReturnQuantity)
        {
            ModelState.AddModelError(nameof(ReturnQuantity), "Use no máximo três casas decimais.");
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
            var loan = await db.InventoryLoans
                .SingleOrDefaultAsync(item => item.Id == LoanId
                    && item.SectorId == PilotSectorId
                    && item.InventoryAssetId == null,
                    cancellationToken);
            if (loan is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return NotFound();
            }

            var alreadyReturned = await db.InventoryMovements
                .Where(movement => movement.InventoryLoanId == loan.Id && movement.Type == InventoryMovementType.Return)
                .SumAsync(movement => movement.Quantity, cancellationToken);
            var outstanding = loan.Quantity - alreadyReturned;
            if (outstanding <= 0 || ReturnQuantity > outstanding)
            {
                await transaction.RollbackAsync(cancellationToken);
                ModelState.AddModelError(string.Empty,
                    $"Devolução excede o saldo emprestado. Em aberto: {Math.Max(0, outstanding):0.###}.");
                await LoadPageDataAsync(cancellationToken);
                return Page();
            }

            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = loan.MaterialId,
                SectorId = PilotSectorId,
                Type = InventoryMovementType.Return,
                Quantity = ReturnQuantity,
                Reference = loan.Reference ?? $"EM-{loan.Id}",
                InventoryLoanId = loan.Id,
                SubmissionId = ReturnSubmissionId,
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
            PostgreSqlConcurrencyErrors.IsUniqueConstraintViolation(exception, SubmissionIndexName)
            || PostgreSqlConcurrencyErrors.IsSerializationFailure(exception))
        {
            db.ChangeTracker.Clear();
            var concurrentSubmission = await FindSubmissionAsync(submissionActorId, ReturnSubmissionId, cancellationToken);
            if (concurrentSubmission is not null)
            {
                return await ResolveExistingSubmissionAsync(concurrentSubmission, submissionFingerprint, cancellationToken);
            }

            ModelState.AddModelError(string.Empty, "A devolução foi processada por outra operação. Atualize a página e confira o saldo em aberto.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }
    }

    private Task<InventoryMovement?> FindSubmissionAsync(
        string actorId,
        Guid submissionId,
        CancellationToken cancellationToken) => db.InventoryMovements
            .AsNoTracking()
            .SingleOrDefaultAsync(movement => movement.SectorId == PilotSectorId
                && movement.ActorUserId == actorId
                && movement.SubmissionId == submissionId, cancellationToken);

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
        ModelState.AddModelError(string.Empty,
            "Esta solicitação já foi processada com outros dados. Atualize a página antes de tentar novamente.");
        await LoadPageDataAsync(cancellationToken);
        return Page();
    }

    private static string CreateLoanSubmissionFingerprint(
        int materialId,
        string borrowerName,
        decimal quantity,
        DateTime? dueDate,
        string? reference) => CreateSubmissionFingerprint(new
        {
            Operation = "quantity-loan/v1",
            MaterialId = materialId,
            BorrowerName = borrowerName,
            Quantity = quantity.ToString("G29", CultureInfo.InvariantCulture),
            DueDate = dueDate?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Reference = reference
        });

    private static string CreateReturnSubmissionFingerprint(int loanId, decimal quantity) =>
        CreateSubmissionFingerprint(new
        {
            Operation = "quantity-loan-return/v1",
            LoanId = loanId,
            Quantity = quantity.ToString("G29", CultureInfo.InvariantCulture)
        });

    private static string CreateSubmissionFingerprint<T>(T canonicalPayload) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonicalPayload))));

    private async Task<decimal> GetBalanceAsync(int materialId, CancellationToken cancellationToken) =>
        await db.InventoryMovements
            .Where(movement => movement.MaterialId == materialId && movement.SectorId == PilotSectorId)
            .Select(movement => movement.Type == InventoryMovementType.Entry || movement.Type == InventoryMovementType.Return
                ? movement.Quantity
                : movement.Type == InventoryMovementType.Consumption || movement.Type == InventoryMovementType.Loan
                    ? -movement.Quantity
                    : movement.Quantity)
            .SumAsync(cancellationToken);

    private async Task LoadPageDataAsync(CancellationToken cancellationToken)
    {
        Materials = await db.Materials
            .AsNoTracking()
            .Where(material => material.SectorId == PilotSectorId
                && material.IsActive
                && material.TrackingMode == MaterialTrackingMode.Quantity)
            .OrderBy(material => material.Name)
            .ToListAsync(cancellationToken);

        var loans = await db.InventoryLoans
            .AsNoTracking()
            .Include(loan => loan.Material)
            .Where(loan => loan.SectorId == PilotSectorId && loan.InventoryAssetId == null)
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

        OpenLoans = loans
            .Select(loan => (Loan: loan, Outstanding: loan.Quantity - returnedByLoan.GetValueOrDefault(loan.Id)))
            .Where(item => item.Outstanding > 0)
            .ToList();
    }
}
