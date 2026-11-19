using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using SistemaMateriais.Web.Authorization;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Pages.Importacao;

[Authorize(Roles = $"{InventoryRoles.Administrator},{InventoryRoles.Manager}")]
[RequestSizeLimit(5 * 1024 * 1024 + 64 * 1024)]
public sealed class ModeloCsvModel(ApplicationDbContext db, IDataProtectionProvider protectionProvider) : PageModel
{
    private const string CsvTemplate = "\uFEFFCodigo;Nome;Descricao;Categoria;Unidade;TipoControle;EstoqueMinimo;SaldoInicial;ReferenciaSaldoInicial\r\n";
    private const int PilotSectorId = 1;
    private const int MaximumUploadBytes = 5 * 1024 * 1024;
    private static readonly string[] ExpectedHeaders =
    [
        "Codigo", "Nome", "Descricao", "Categoria", "Unidade", "TipoControle",
        "EstoqueMinimo", "SaldoInicial", "ReferenciaSaldoInicial"
    ];
    private readonly IDataProtector previewProtector = protectionProvider.CreateProtector("SistemaMateriais.InitialImportPreview.v1");
    private string? currentUploadHash;

    [BindProperty]
    public IFormFile? Upload { get; set; }

    [BindProperty]
    public string? ConfirmationToken { get; set; }

    public int ValidRowCount { get; private set; }
    public int ParsedRowCount { get; private set; }
    public List<ImportValidationError> ValidationErrors { get; private set; } = [];
    public List<ImportRowPreview> PreviewRows { get; private set; } = [];
    public string? PreviewToken { get; private set; }
    private List<ValidatedMaterialRow> ValidatedRows { get; set; } = [];

    public IActionResult OnGetDownload()
    {
        return File(System.Text.Encoding.UTF8.GetBytes(CsvTemplate), "text/csv; charset=utf-8", "modelo-importacao-materiais.csv");
    }

    public Task<IActionResult> OnPostPreviewAsync(CancellationToken cancellationToken)
    {
        return ValidateUploadAsync(cancellationToken, issuePreviewToken: true);
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        var submittedConfirmationToken = ConfirmationToken;
        var validationResult = await ValidateUploadAsync(cancellationToken, issuePreviewToken: false);
        if (ValidationErrors.Count > 0 || ParsedRowCount == 0 || ValidatedRows.Count != ParsedRowCount)
        {
            return validationResult;
        }

        if (!IsPreviewTokenValid(submittedConfirmationToken, currentUploadHash, User.FindFirstValue(ClaimTypes.NameIdentifier)))
        {
            ValidationErrors.Add(new ImportValidationError(0, "Arquivo", "A prévia não corresponde ao arquivo enviado ou expirou. Gere uma nova prévia antes de confirmar."));
            return Page();
        }

        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorName = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(actorName))
        {
            return Forbid();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var createdAtUtc = DateTimeOffset.UtcNow;
            var materials = ValidatedRows.Select(row => new Material
            {
                Code = row.Code,
                NormalizedCode = row.Code.ToUpperInvariant(),
                Name = row.Name,
                Description = row.Description,
                Unit = row.Unit,
                TrackingMode = row.TrackingMode,
                MinimumStock = row.MinimumStock,
                CategoryId = row.CategoryId,
                SectorId = PilotSectorId
            }).ToList();

            db.Materials.AddRange(materials);
            await db.SaveChangesAsync(cancellationToken);
            var movements = materials.Zip(ValidatedRows)
                .Where(pair => pair.Second.InitialBalance > 0)
                .Select(pair => new InventoryMovement
                {
                    MaterialId = pair.First.Id,
                    SectorId = PilotSectorId,
                    Type = InventoryMovementType.Entry,
                    Quantity = pair.Second.InitialBalance,
                    Reference = pair.Second.InitialBalanceReference,
                    ActorUserId = actorId,
                    ActorName = actorName,
                    CreatedAtUtc = createdAtUtc
                })
                .ToList();
            db.InventoryMovements.AddRange(movements);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RedirectToPage();
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            ValidationErrors.Add(new ImportValidationError(0, "Arquivo", "A carga não foi aplicada: os dados podem ter sido alterados ou duplicados durante a confirmação. Revise o catálogo e gere uma nova prévia."));
            return Page();
        }
    }

    private async Task<IActionResult> ValidateUploadAsync(CancellationToken cancellationToken, bool issuePreviewToken)
    {
        ValidRowCount = 0;
        ParsedRowCount = 0;
        ValidationErrors = [];
        PreviewRows = [];
        PreviewToken = null;
        currentUploadHash = null;
        ValidatedRows = [];

        if (Upload is null || Upload.Length == 0)
        {
            ValidationErrors.Add(new ImportValidationError(0, "Arquivo", "Selecione um arquivo CSV não vazio."));
            return Page();
        }

        if (Upload.Length > MaximumUploadBytes)
        {
            ValidationErrors.Add(new ImportValidationError(0, "Arquivo", "O arquivo deve ter no máximo 5 MB."));
            return Page();
        }

        string text;
        try
        {
            await using var stream = Upload.OpenReadStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            text = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (DecoderFallbackException)
        {
            ValidationErrors.Add(new ImportValidationError(0, "Arquivo", "O arquivo deve estar codificado em UTF-8."));
            return Page();
        }

        if (text.Contains('\0'))
        {
            ValidationErrors.Add(new ImportValidationError(0, "Arquivo", "O arquivo contém caracteres inválidos."));
            return Page();
        }
        currentUploadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        var rows = new List<ParsedMaterialRow>();
        try
        {
            using var input = new StringReader(text);
            using var parser = new TextFieldParser(input)
            {
                TextFieldType = FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = false
            };
            parser.SetDelimiters(";");
            var headers = parser.ReadFields();
            if (headers is null || !headers.SequenceEqual(ExpectedHeaders, StringComparer.Ordinal))
            {
                ValidationErrors.Add(new ImportValidationError(1, "Cabeçalho", "Use o cabeçalho completo do modelo, sem alterar nomes ou ordem."));
                return Page();
            }

            while (!parser.EndOfData)
            {
                var lineNumber = (int)parser.LineNumber;
                var fields = parser.ReadFields();
                if (fields is null || fields.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                ParsedRowCount++;
                if (fields.Length != ExpectedHeaders.Length)
                {
                    ValidationErrors.Add(new ImportValidationError(lineNumber, "Linha", "A linha deve conter exatamente nove colunas."));
                    continue;
                }

                rows.Add(new ParsedMaterialRow(lineNumber, fields));
            }
        }
        catch (MalformedLineException)
        {
            ValidationErrors.Add(new ImportValidationError(0, "Arquivo", "O CSV contém uma linha com aspas ou separadores inválidos."));
            return Page();
        }

        if (ParsedRowCount == 0)
        {
            ValidationErrors.Add(new ImportValidationError(0, "Arquivo", "O CSV contém o cabeçalho, mas nenhuma linha de material."));
            return Page();
        }

        var categories = await db.MaterialCategories
            .AsNoTracking()
            .Where(category => category.SectorId == PilotSectorId && category.IsActive)
            .ToDictionaryAsync(category => category.NormalizedName, cancellationToken);
        var existingCodes = await db.Materials
            .AsNoTracking()
            .Where(material => material.SectorId == PilotSectorId)
            .Select(material => material.NormalizedCode)
            .ToListAsync(cancellationToken);
        var codesInFile = new HashSet<string>(existingCodes, StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var fields = row.Fields;
            var code = fields[0].Trim();
            var name = fields[1].Trim();
            var description = fields[2].Trim();
            var categoryName = fields[3].Trim();
            var unit = fields[4].Trim();
            var trackingMode = fields[5];
            var reference = fields[8].Trim();
            var valid = true;

            if (code.Length is < 1 or > 40)
                AddError(row.LineNumber, "Codigo", "Informe entre 1 e 40 caracteres.", ref valid);
            else if (!codesInFile.Add(code.ToUpperInvariant()))
                AddError(row.LineNumber, "Codigo", "Código duplicado no setor TI ou no próprio arquivo.", ref valid);

            if (name.Length is < 2 or > 120)
                AddError(row.LineNumber, "Nome", "Informe entre 2 e 120 caracteres.", ref valid);
            if (description.Length > 500)
                AddError(row.LineNumber, "Descricao", "Use no máximo 500 caracteres.", ref valid);
            var category = categories.GetValueOrDefault(categoryName.ToUpperInvariant());
            var categoryExists = categoryName.Length is >= 2 and <= 80 && category is not null;
            if (!categoryExists)
                AddError(row.LineNumber, "Categoria", "Informe uma categoria ativa já cadastrada no setor TI.", ref valid);
            if (unit.Length is < 1 or > 20)
                AddError(row.LineNumber, "Unidade", "Informe entre 1 e 20 caracteres.", ref valid);
            if (trackingMode is not ("Quantity" or "IndividualAsset"))
                AddError(row.LineNumber, "TipoControle", "Use Quantity ou IndividualAsset.", ref valid);

            var minimumIsValid = TryParseQuantity(fields[6], out var minimumStock);
            var initialIsValid = TryParseQuantity(fields[7], out var initialBalance);
            if (!minimumIsValid || minimumStock < 0)
                AddError(row.LineNumber, "EstoqueMinimo", "Use número não negativo, vírgula decimal e no máximo três casas.", ref valid);
            if (!initialIsValid || initialBalance < 0)
                AddError(row.LineNumber, "SaldoInicial", "Use número não negativo, vírgula decimal e no máximo três casas.", ref valid);
            if (trackingMode == "IndividualAsset" && initialIsValid && initialBalance != 0)
                AddError(row.LineNumber, "SaldoInicial", "Ativos individualizados devem iniciar com saldo zero.", ref valid);
            if (initialIsValid && initialBalance > 0 && reference.Length == 0)
                AddError(row.LineNumber, "ReferenciaSaldoInicial", "Informe a referência do movimento de saldo inicial.", ref valid);
            if (reference.Length > 200)
                AddError(row.LineNumber, "ReferenciaSaldoInicial", "Use no máximo 200 caracteres.", ref valid);

            if (valid)
            {
                ValidatedRows.Add(new ValidatedMaterialRow(
                    code, name, string.IsNullOrWhiteSpace(description) ? null : description,
                    unit, category!.Id,
                    trackingMode == "Quantity" ? MaterialTrackingMode.Quantity : MaterialTrackingMode.IndividualAsset,
                    minimumStock, initialBalance, initialBalance > 0 ? reference : null));
                PreviewRows.Add(new ImportRowPreview(
                    row.LineNumber, code, name, description, category!.Name, unit, trackingMode, minimumStock, initialBalance));
                ValidRowCount++;
            }
        }

        if (issuePreviewToken && ValidationErrors.Count == 0 && ParsedRowCount > 0 && ValidatedRows.Count == ParsedRowCount)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrWhiteSpace(userId))
            {
                var payload = new PreviewConfirmationPayload(userId, currentUploadHash!, DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds());
                PreviewToken = previewProtector.Protect(JsonSerializer.Serialize(payload));
            }
        }

        return Page();
    }

    private bool IsPreviewTokenValid(string? token, string? fileHash, string? userId)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(fileHash) || string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<PreviewConfirmationPayload>(previewProtector.Unprotect(token));
            return payload is not null
                   && payload.UserId == userId
                   && payload.FileHash == fileHash
                   && DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnixSeconds) > DateTimeOffset.UtcNow;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseQuantity(string value, out decimal quantity)
    {
        return decimal.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint,
                   CultureInfo.GetCultureInfo("pt-BR"), out quantity)
               && quantity <= 999999999.999m
               && decimal.Round(quantity, 3) == quantity;
    }

    private void AddError(int row, string field, string message, ref bool valid)
    {
        valid = false;
        ValidationErrors.Add(new ImportValidationError(row, field, message));
    }

    private sealed record ParsedMaterialRow(int LineNumber, string[] Fields);
    private sealed record ValidatedMaterialRow(
        string Code, string Name, string? Description, string Unit, int CategoryId,
        MaterialTrackingMode TrackingMode, decimal MinimumStock, decimal InitialBalance,
        string? InitialBalanceReference);
    private sealed record PreviewConfirmationPayload(string UserId, string FileHash, long ExpiresAtUnixSeconds);
}

public sealed record ImportValidationError(int Row, string Field, string Message);
public sealed record ImportRowPreview(
    int Row, string Code, string Name, string? Description, string Category, string Unit, string TrackingMode,
    decimal MinimumStock, decimal InitialBalance);
