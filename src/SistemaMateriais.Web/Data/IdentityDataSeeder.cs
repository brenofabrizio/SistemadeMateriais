using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SistemaMateriais.Web.Authorization;

namespace SistemaMateriais.Web.Data;

public static class IdentityDataSeeder
{
    public static async Task SeedAsync(
        IServiceProvider services,
        IConfiguration configuration,
        bool applyMigrations)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (applyMigrations)
        {
            var provider = configuration["Database:Provider"] ?? "Sqlite";
            if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await context.Database.EnsureCreatedAsync();
            }
            else
            {
                await context.Database.MigrateAsync();
            }
        }

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var roleName in new[]
        {
            InventoryRoles.Administrator,
            InventoryRoles.Manager,
            InventoryRoles.Assistant,
            InventoryRoles.Auxiliary
        })
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var result = await roleManager.CreateAsync(new IdentityRole(roleName));
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException($"Could not create role '{roleName}'.");
                }
            }
        }

        var password = configuration["BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var userName = configuration["BootstrapAdmin:UserName"] ?? "adm";
        if (await userManager.FindByNameAsync(userName) is not null)
        {
            return;
        }

        var administrator = new IdentityUser { UserName = userName };
        var createResult = await userManager.CreateAsync(administrator, password);
        if (!createResult.Succeeded)
        {
            var errors = string.Join("; ", createResult.Errors.Select(error => error.Code));
            throw new InvalidOperationException($"Could not provision bootstrap administrator. Identity errors: {errors}");
        }

        var roleResult = await userManager.AddToRoleAsync(administrator, InventoryRoles.Administrator);
        if (!roleResult.Succeeded)
        {
            throw new InvalidOperationException("Could not assign the administrator role to the bootstrap account.");
        }
    }
}
