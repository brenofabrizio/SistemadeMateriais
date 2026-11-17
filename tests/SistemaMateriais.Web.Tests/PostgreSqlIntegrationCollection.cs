using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SistemaMateriais.Web.Data;
using Xunit;

namespace SistemaMateriais.Web.Tests;

[CollectionDefinition("PostgreSQL integration", DisableParallelization = true)]
public sealed class PostgreSqlIntegrationCollection
{
}

internal static class PostgreSqlIntegrationSupport
{
    public static void AssertDisposableLoopbackDatabase(string connectionString)
    {
        var connection = new DbConnectionStringBuilder { ConnectionString = connectionString };
        Assert.True(connection.TryGetValue("Database", out var databaseName));
        Assert.Equal("sistema_materiais_test", databaseName?.ToString());
        Assert.True(connection.TryGetValue("Host", out var host));
        var hostName = host?.ToString()?.Trim('[', ']');
        var isLoopback = string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(hostName, out var address) && IPAddress.IsLoopback(address);
        Assert.True(isLoopback,
            "Opt-in PostgreSQL tests may connect only to localhost or a loopback IP and the dedicated sistema_materiais_test database.");
    }

    public static async Task DeleteMaterialScenarioAsync(IServiceProvider services, int materialId, int categoryId)
    {
        if (materialId <= 0 && categoryId <= 0)
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (materialId > 0)
        {
            db.InventoryMovements.RemoveRange(await db.InventoryMovements
                .Where(movement => movement.MaterialId == materialId).ToListAsync());
            db.InventoryLoans.RemoveRange(await db.InventoryLoans
                .Where(loan => loan.MaterialId == materialId).ToListAsync());
            db.InventoryAssets.RemoveRange(await db.InventoryAssets
                .Where(asset => asset.MaterialId == materialId).ToListAsync());
            db.InventoryAdjustmentRequests.RemoveRange(await db.InventoryAdjustmentRequests
                .Where(request => request.MaterialId == materialId).ToListAsync());

            var material = await db.Materials.SingleOrDefaultAsync(item => item.Id == materialId);
            if (material is not null)
            {
                categoryId = material.CategoryId;
                db.Materials.Remove(material);
            }

            await db.SaveChangesAsync();
        }

        if (categoryId > 0)
        {
            var category = await db.MaterialCategories.SingleOrDefaultAsync(item => item.Id == categoryId);
            if (category is not null && !await db.Materials.AnyAsync(item => item.CategoryId == categoryId))
            {
                db.MaterialCategories.Remove(category);
                await db.SaveChangesAsync();
            }
        }
    }
}
