using Microsoft.AspNetCore.Authorization;

namespace SistemaMateriais.Web.Authorization;

public static class InventoryRoles
{
    public const string Administrator = "Administrador";
    public const string Manager = "Gestor";
    public const string Assistant = "Assistente";
    public const string Auxiliary = "Auxiliar";
}

public static class InventoryPolicies
{
    public const string OperateStock = "OperateStock";
    public const string AdjustStock = "AdjustStock";
    public const string ApproveAdjustments = "ApproveAdjustments";
    public const string ManageCatalog = "ManageCatalog";

    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(OperateStock, policy => policy.RequireRole(
            InventoryRoles.Administrator,
            InventoryRoles.Manager,
            InventoryRoles.Assistant,
            InventoryRoles.Auxiliary));

        options.AddPolicy(AdjustStock, policy => policy.RequireRole(
            InventoryRoles.Administrator,
            InventoryRoles.Manager));

        options.AddPolicy(ApproveAdjustments, policy => policy.RequireRole(
            InventoryRoles.Administrator,
            InventoryRoles.Manager));

        options.AddPolicy(ManageCatalog, policy => policy.RequireRole(
            InventoryRoles.Administrator,
            InventoryRoles.Manager));
    }
}
