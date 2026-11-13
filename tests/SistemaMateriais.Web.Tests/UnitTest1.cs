using System.Security.Claims;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SistemaMateriais.Web.Authorization;
using SistemaMateriais.Web.Data;
using SistemaMateriais.Web.Infrastructure;

namespace SistemaMateriais.Web.Tests;

public class InventoryAuthorizationTests
{
    [Theory]
    [InlineData(InventoryRoles.Administrator)]
    [InlineData(InventoryRoles.Manager)]
    public async Task AdministratorAndManagerCanAdjustStock(string role)
    {
        var result = await Authorize(role, InventoryPolicies.AdjustStock);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(InventoryRoles.Assistant)]
    [InlineData(InventoryRoles.Auxiliary)]
    public async Task AssistantAndAuxiliaryCannotAdjustStock(string role)
    {
        var result = await Authorize(role, InventoryPolicies.AdjustStock);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(InventoryRoles.Administrator)]
    [InlineData(InventoryRoles.Manager)]
    [InlineData(InventoryRoles.Assistant)]
    [InlineData(InventoryRoles.Auxiliary)]
    public async Task AllPilotRolesCanOperateStock(string role)
    {
        var result = await Authorize(role, InventoryPolicies.OperateStock);

        Assert.True(result.Succeeded);
    }

    private static async Task<AuthorizationResult> Authorize(string role, string policy)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options => InventoryPolicies.Configure(options));

        await using var provider = services.BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "test-user"), new Claim(ClaimTypes.Role, role)],
            authenticationType: "test"));

        return await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, resource: null, policy);
    }
}

public class DatabaseProviderTests
{
    [Fact]
    public void ConfiguresPostgreSqlProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "PostgreSql",
                ["ConnectionStrings:PostgreSql"] = "Host=localhost;Database=sistema_materiais;Username=app;Password=test"
            })
            .Build();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();

        DatabaseProviderConfiguration.Configure(options, configuration);

        using var context = new ApplicationDbContext(options.Options);
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
    }
}

public class ForwardedHeaderConfigurationTests
{
    [Fact]
    public void UsesLoopbackAsTheOnlyReverseProxyAddress()
    {
        Assert.Equal(IPAddress.Loopback, ForwardedHeaderConfiguration.TrustedProxyAddress);
    }
}


