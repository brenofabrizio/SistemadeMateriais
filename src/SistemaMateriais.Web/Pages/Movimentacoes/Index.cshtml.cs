using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SistemaMateriais.Web.Authorization;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Pages.Movimentacoes;

[Authorize(Policy = InventoryPolicies.OperateStock)]
public sealed class IndexModel(ApplicationDbContext db) : PageModel
{
    private const int PilotSectorId = 1;

    [BindProperty(SupportsGet = true)]
    public int? MaterialId { get; set; }

    [BindProperty(SupportsGet = true)]
    public InventoryMovementType? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? To { get; set; }

    [BindProperty(SupportsGet = true)]
    [StringLength(120)]
    public string? Actor { get; set; }

    public IReadOnlyList<InventoryMovement> Movements { get; private set; } = [];
    public IReadOnlyList<Material> Materials { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Materials = await db.Materials
            .AsNoTracking()
            .Where(material => material.SectorId == PilotSectorId)
            .OrderBy(material => material.Name)
            .ToListAsync(cancellationToken);

        Movements = await BuildMovementQuery()
            .OrderByDescending(movement => movement.Id)
            .Take(500)
            .ToListAsync(cancellationToken);
    }

    public async Task<IActionResult> OnGetExportCsvAsync(CancellationToken cancellationToken)
    {
        var movements = await BuildMovementQuery()
            .OrderByDescending(movement => movement.Id)
            .ToListAsync(cancellationToken);
        var csv = new StringBuilder();
        csv.AppendLine("Data UTC,Código,Material,Tipo,Quantidade,Referência,Usuário");

        foreach (var movement in movements)
        {
            csv.Append(CsvText(movement.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',')
                .Append(CsvText(movement.Material.Code)).Append(',')
                .Append(CsvText(movement.Material.Name)).Append(',')
                .Append(CsvText(FormatType(movement.Type))).Append(',')
                .Append(movement.Quantity.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(CsvText(movement.Reference ?? string.Empty)).Append(',')
                .Append(CsvText(movement.ActorName)).AppendLine();
        }

        var payload = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return File(payload, "text/csv; charset=utf-8", "movimentacoes.csv");
    }

    private IQueryable<InventoryMovement> BuildMovementQuery()
    {
        var query = db.InventoryMovements
            .AsNoTracking()
            .Include(movement => movement.Material)
            .Where(movement => movement.SectorId == PilotSectorId);

        if (MaterialId.HasValue)
        {
            query = query.Where(movement => movement.MaterialId == MaterialId.Value);
        }

        if (Type.HasValue)
        {
            query = query.Where(movement => movement.Type == Type.Value);
        }

        if (From.HasValue)
        {
            var fromUtc = DateTime.SpecifyKind(From.Value.Date, DateTimeKind.Utc);
            query = query.Where(movement => movement.CreatedAtUtc >= fromUtc);
        }

        if (To.HasValue && To.Value.Date < DateTime.MaxValue.Date)
        {
            var toExclusiveUtc = DateTime.SpecifyKind(To.Value.Date.AddDays(1), DateTimeKind.Utc);
            query = query.Where(movement => movement.CreatedAtUtc < toExclusiveUtc);
        }

        var actor = Actor?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(actor))
        {
            query = query.Where(movement => movement.ActorName.ToUpper().Contains(actor));
        }

        return query;
    }

    private static string CsvText(string value)
    {
        var firstSignificant = value.TrimStart();
        if (firstSignificant.Length > 0 && firstSignificant[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n')
        {
            value = $"'{value}";
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string FormatType(InventoryMovementType type) => type switch
    {
        InventoryMovementType.Entry => "Entrada",
        InventoryMovementType.Consumption => "Consumo",
        InventoryMovementType.Loan => "Empréstimo",
        InventoryMovementType.Return => "Devolução",
        InventoryMovementType.Adjustment => "Ajuste",
        _ => type.ToString()
    };
}
