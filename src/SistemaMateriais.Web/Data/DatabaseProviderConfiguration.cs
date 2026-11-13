using Microsoft.EntityFrameworkCore;

namespace SistemaMateriais.Web.Data;

public static class DatabaseProviderConfiguration
{
    public static void Configure(
        DbContextOptionsBuilder options,
        IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        if (string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuration.GetConnectionString("PostgreSql")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:PostgreSql must be configured when Database:Provider is PostgreSql.");
            options.UseNpgsql(connectionString);
            return;
        }

        if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:DefaultConnection must be configured when Database:Provider is Sqlite.");
            options.UseSqlite(connectionString);
            return;
        }

        throw new InvalidOperationException($"Unsupported database provider '{provider}'.");
    }
}
