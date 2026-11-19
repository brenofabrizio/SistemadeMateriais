using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Tests;

[Collection("PostgreSQL integration")]
public sealed class PostgreSqlInitialImportTests
{
    [PostgreSqlFact]
    public async Task InitialImportPersistsCatalogAndOpeningLedgerRowsOnPostgreSql()
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
            builder.ConfigureTestServices(services => services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "PostgreSqlImportTest";
                    options.DefaultChallengeScheme = "PostgreSqlImportTest";
                })
                .AddScheme<AuthenticationSchemeOptions, PostgreSqlImportTestAuthenticationHandler>("PostgreSqlImportTest", _ => { }));
        });

        var unique = Guid.NewGuid().ToString("N");
        var categoryName = $"Import-{unique}";
        var quantityCode = $"IQ-{unique}";
        var assetCode = $"IA-{unique}";
        var categoryId = 0;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
                Assert.Equal(connectionString, db.Database.GetConnectionString());
                var category = new MaterialCategory
                {
                    Name = categoryName,
                    NormalizedName = categoryName.ToUpperInvariant(),
                    SectorId = 1
                };
                db.MaterialCategories.Add(category);
                await db.SaveChangesAsync();
                categoryId = category.Id;
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var page = await client.GetAsync("/Importacao/ModeloCsv");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var pageHtml = await page.Content.ReadAsStringAsync();
            var token = Regex.Match(pageHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
            Assert.True(token.Success, "The import page should emit an antiforgery token.");

            var csv = $"Codigo;Nome;Descricao;Categoria;Unidade;TipoControle;EstoqueMinimo;SaldoInicial;ReferenciaSaldoInicial\r\n" +
                $"{quantityCode};Mouse;USB;{categoryName};unidade;Quantity;1,000;3,500;Inventário de teste\r\n" +
                $"{assetCode};Monitor;HDMI;{categoryName};unidade;IndividualAsset;0,000;0,000;";
            using var previewForm = CreateUpload(csv, token.Groups[1].Value);
            using var previewResponse = await client.PostAsync("/Importacao/ModeloCsv?handler=Preview", previewForm);
            Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
            var previewHtml = await previewResponse.Content.ReadAsStringAsync();
            var confirmation = Regex.Match(previewHtml, @"name=""ConfirmationToken""[^>]*value=""([^""]+)""");
            Assert.True(confirmation.Success, "A valid PostgreSQL preview should issue a protected confirmation token.");
            var previewCsrf = Regex.Match(previewHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
            Assert.True(previewCsrf.Success, "The import form should emit an antiforgery token.");

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(previewCsrf.Groups[1].Value), "__RequestVerificationToken");
            form.Add(new StringContent(confirmation.Groups[1].Value), "ConfirmationToken");
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
            var upload = new ByteArrayContent(bytes);
            upload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
            form.Add(upload, "Upload", "initial.csv");

            using var response = await client.PostAsync("/Importacao/ModeloCsv?handler=Import", form);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var importedMaterials = await verifyDb.Materials
                .Where(material => material.Code == quantityCode || material.Code == assetCode)
                .ToListAsync();
            Assert.Equal(2, importedMaterials.Count);
            Assert.All(importedMaterials, material => Assert.Equal(1, material.SectorId));
            Assert.Equal(MaterialTrackingMode.IndividualAsset,
                importedMaterials.Single(material => material.Code == assetCode).TrackingMode);

            var quantityMaterialId = importedMaterials.Single(material => material.Code == quantityCode).Id;
            var assetMaterialId = importedMaterials.Single(material => material.Code == assetCode).Id;
            var movements = await verifyDb.InventoryMovements
                .Where(movement => movement.MaterialId == quantityMaterialId || movement.MaterialId == assetMaterialId)
                .ToListAsync();
            var opening = Assert.Single(movements);
            Assert.Equal(InventoryMovementType.Entry, opening.Type);
            Assert.Equal(3.5m, opening.Quantity);
            Assert.Equal("Inventário de teste", opening.Reference);
            Assert.Equal("postgres-test-user", opening.ActorUserId);
            Assert.Equal(categoryId, importedMaterials.Single(material => material.Id == quantityMaterialId).CategoryId);
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var ids = await cleanupDb.Materials
                .Where(material => material.Code == quantityCode || material.Code == assetCode)
                .Select(material => material.Id)
                .ToListAsync();
            cleanupDb.InventoryMovements.RemoveRange(await cleanupDb.InventoryMovements
                .Where(movement => ids.Contains(movement.MaterialId)).ToListAsync());
            cleanupDb.Materials.RemoveRange(await cleanupDb.Materials.Where(material => ids.Contains(material.Id)).ToListAsync());
            await cleanupDb.SaveChangesAsync();
            var category = await cleanupDb.MaterialCategories.SingleOrDefaultAsync(item => item.Id == categoryId);
            if (category is not null)
            {
                cleanupDb.MaterialCategories.Remove(category);
                await cleanupDb.SaveChangesAsync();
            }
        }
    }

    private static MultipartFormDataContent CreateUpload(string csv, string csrfToken)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(csrfToken), "__RequestVerificationToken");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        var upload = new ByteArrayContent(bytes);
        upload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        form.Add(upload, "Upload", "initial.csv");
        return form;
    }
}

internal sealed class PostgreSqlImportTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "postgres-test-user"),
            new Claim(ClaimTypes.Name, "postgres-test-user"),
            new Claim(ClaimTypes.Role, "Administrador")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
