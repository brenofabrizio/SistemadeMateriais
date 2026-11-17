using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Tests;

[Collection("PostgreSQL integration")]
public sealed class PostgreSqlSectorRelationshipMigrationTests
{
    [PostgreSqlFact]
    public async Task MigrationReportsExistingCrossSectorReferencesBeforeAddingConstraints()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSql");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        PostgreSqlIntegrationSupport.AssertDisposableLoopbackDatabase(connectionString!);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "PostgreSql",
                    ["ConnectionStrings:PostgreSql"] = connectionString
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ApplicationDbContext>();
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
            });
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var migrator = db.Database.GetService<IMigrator>();
        const string previousMigration = "20261115170323_LinkLoansToIndividualAssets";
        const string targetMigration = "20261115193416_EnforceSectorScopedRelationships";

        await migrator.MigrateAsync(previousMigration);
        var sectorId = 0;
        var categoryId = 0;
        var materialId = 0;
        try
        {
            await db.Database.OpenConnectionAsync();
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var unique = Guid.NewGuid().ToString("N");
            await using (var command = new NpgsqlCommand(
                "INSERT INTO \"Sectors\" (\"Name\", \"IsActive\") VALUES (@name, TRUE) RETURNING \"Id\";",
                connection))
            {
                command.Parameters.AddWithValue("name", $"Migration-{unique}");
                sectorId = (int)(await command.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException("Sector insert returned no ID."));
            }
            await using (var command = new NpgsqlCommand(
                "INSERT INTO \"MaterialCategories\" (\"Name\", \"NormalizedName\", \"SectorId\", \"IsActive\") VALUES (@name, @normalizedName, @sectorId, TRUE) RETURNING \"Id\";",
                connection))
            {
                command.Parameters.AddWithValue("name", $"Category-{unique}");
                command.Parameters.AddWithValue("normalizedName", $"CATEGORY-{unique}");
                command.Parameters.AddWithValue("sectorId", sectorId);
                categoryId = (int)(await command.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException("Category insert returned no ID."));
            }
            await using (var command = new NpgsqlCommand(
                "INSERT INTO \"Materials\" (\"Code\", \"NormalizedCode\", \"Name\", \"Description\", \"Unit\", \"TrackingMode\", \"MinimumStock\", \"CategoryId\", \"SectorId\", \"IsActive\") VALUES (@code, @normalizedCode, @name, NULL, 'unit', 'Quantity', 0, @categoryId, 1, TRUE) RETURNING \"Id\";",
                connection))
            {
                command.Parameters.AddWithValue("code", $"MIG-{unique}");
                command.Parameters.AddWithValue("normalizedCode", $"MIG-{unique}");
                command.Parameters.AddWithValue("name", $"Migration material {unique}");
                command.Parameters.AddWithValue("categoryId", categoryId);
                materialId = (int)(await command.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException("Material insert returned no ID."));
            }

            var failure = await Record.ExceptionAsync(() => migrator.MigrateAsync());
            var postgresFailure = Assert.IsType<PostgresException>(failure);
            Assert.Contains("cross-sector references", postgresFailure.MessageText, StringComparison.OrdinalIgnoreCase);

            var recordedMigration = await db.Database.SqlQuery<string>($"""
                SELECT "MigrationId" AS "Value"
                FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = {targetMigration}
                """).SingleOrDefaultAsync();
            Assert.Null(recordedMigration);
        }
        finally
        {
            if (materialId > 0)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Materials\" WHERE \"Id\" = {materialId}");
            }
            if (categoryId > 0)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"MaterialCategories\" WHERE \"Id\" = {categoryId}");
            }
            if (sectorId > 0)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Sectors\" WHERE \"Id\" = {sectorId}");
            }

            await migrator.MigrateAsync();
        }
    }
}
