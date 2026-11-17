using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Tests;

public sealed class MaterialsCatalogTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _databasePath = null!;

    public Task InitializeAsync()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var hermesScratch = Path.Combine(localData, "hermes", "cache", "scratch");
        var scratch = Directory.Exists(hermesScratch) ? hermesScratch : Path.GetTempPath();
        _databasePath = Path.Combine(scratch, $"materiais-material-test-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["ConnectionStrings:DefaultConnection"] = $"Data Source={_databasePath}"
                }));
            builder.ConfigureTestServices(services => services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { }));
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public async Task CreatingMaterialRedirectsAfterAValidSubmission()
    {
        var materialsPage = await _client.GetAsync("/Materiais");
        Assert.Equal(HttpStatusCode.OK, materialsPage.StatusCode);

        var categoriesPage = await _client.GetAsync("/Categorias");
        var html = await categoriesPage.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The page should emit an antiforgery token.");

        int categoryId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var category = new MaterialCategory
            {
                Name = "Periféricos",
                NormalizedName = "PERIFÉRICOS",
                SectorId = 1
            };
            db.MaterialCategories.Add(category);
            await db.SaveChangesAsync();
            categoryId = category.Id;
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = "TI-001",
            ["Name"] = "Teclado",
            ["Description"] = "Teclado USB",
            ["Unit"] = "unidade",
            ["CategoryId"] = categoryId.ToString(),
            ["TrackingMode"] = "Quantity",
            ["MinimumStock"] = "5",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Materiais?handler=Create", form);
        var responseBody = await response.Content.ReadAsStringAsync();
        var allowHeader = response.Headers.TryGetValues("Allow", out var allowValues)
            ? string.Join(",", allowValues)
            : "none";

        Assert.True(response.StatusCode == HttpStatusCode.Redirect,
            $"Expected redirect; GET /Materiais returned {(int)materialsPage.StatusCode}; Allow={allowHeader}; body={responseBody}");
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var material = await verifyDb.Materials.SingleAsync(item => item.Code == "TI-001");
        Assert.Equal("TI-001", material.NormalizedCode);
        Assert.Equal("Teclado", material.Name);
        Assert.Equal("Teclado USB", material.Description);
        Assert.Equal("unidade", material.Unit);
        Assert.Equal(categoryId, material.CategoryId);
        Assert.Equal(1, material.SectorId);
        Assert.Equal(MaterialTrackingMode.Quantity, material.TrackingMode);
        Assert.Equal(5m, material.MinimumStock);
    }

    [Fact]
    public async Task DeactivatingMaterialRetainsItsRecordAndMarksItInactive()
    {
        var categoryId = await SeedCategoryAsync();
        var (createResponse, _) = await SubmitMaterialAsync(categoryId, "TI-020", "Webcam");
        Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var page = await _client.GetAsync("/Materiais");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The materials page should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["DeactivateId"] = materialId.ToString(),
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Materiais?handler=Deactivate", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var material = await verifyDb.Materials.SingleAsync(item => item.Id == materialId);
        Assert.False(material.IsActive);
        Assert.Equal(1, await verifyDb.Materials.CountAsync());
    }

    [Fact]
    public async Task EditingMaterialUpdatesItsPersistedFields()
    {
        var categoryId = await SeedCategoryAsync();
        var (createResponse, _) = await SubmitMaterialAsync(categoryId, "TI-003", "Monitor");
        Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var page = await _client.GetAsync($"/Materiais/Editar/{materialId}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The edit form should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = "TI-004",
            ["Name"] = "Monitor 27 polegadas",
            ["Description"] = "Monitor USB-C",
            ["Unit"] = "unidade",
            ["CategoryId"] = categoryId.ToString(),
            ["TrackingMode"] = "IndividualAsset",
            ["MinimumStock"] = "2",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync($"/Materiais/Editar/{materialId}", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var material = await verifyDb.Materials.SingleAsync(item => item.Id == materialId);
        Assert.Equal("TI-004", material.Code);
        Assert.Equal("TI-004", material.NormalizedCode);
        Assert.Equal("Monitor 27 polegadas", material.Name);
        Assert.Equal("Monitor USB-C", material.Description);
        Assert.Equal("unidade", material.Unit);
        Assert.Equal(MaterialTrackingMode.IndividualAsset, material.TrackingMode);
        Assert.Equal(2m, material.MinimumStock);
    }

    [Fact]
    public async Task EditingMaterialCannotChangeTrackingModeAfterInventoryMovementExists()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "MODE-001", "Material rastreado");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var material = await db.Materials.SingleAsync();
            materialId = material.Id;
            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = material.Id,
                SectorId = material.SectorId,
                Type = InventoryMovementType.Entry,
                Quantity = 4m,
                ActorUserId = "pilot-user",
                ActorName = "pilot-user",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var editPage = await _client.GetAsync($"/Materiais/Editar/{materialId}");
        var html = await editPage.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The edit form should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = "MODE-001",
            ["Name"] = "Material rastreado",
            ["Unit"] = "unidade",
            ["CategoryId"] = categoryId.ToString(),
            ["TrackingMode"] = "IndividualAsset",
            ["MinimumStock"] = "5",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });

        var response = await _client.PostAsync($"/Materiais/Editar/{materialId}", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseHtml = await response.Content.ReadAsStringAsync();
        var trackingModeError = Regex.Match(responseHtml,
            @"<span[^>]*data-valmsg-for=""TrackingMode""[^>]*>.*?</span>", RegexOptions.Singleline);
        Assert.Contains("controle", trackingModeError.Value, StringComparison.OrdinalIgnoreCase);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(MaterialTrackingMode.Quantity, (await verifyDb.Materials.SingleAsync()).TrackingMode);
        Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task EditingMaterialCannotReuseAnotherCodeInTheSameSector()
    {
        var categoryId = await SeedCategoryAsync();
        var (firstCreate, _) = await SubmitMaterialAsync(categoryId, "TI-010", "Teclado");
        var (secondCreate, _) = await SubmitMaterialAsync(categoryId, "TI-011", "Mouse");
        Assert.Equal(HttpStatusCode.Redirect, firstCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, secondCreate.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Where(material => material.Code == "TI-010")
                .Select(material => material.Id).SingleAsync();
        }

        var page = await _client.GetAsync($"/Materiais/Editar/{materialId}");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The edit form should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = "TI-011",
            ["Name"] = "Teclado atualizado",
            ["Unit"] = "unidade",
            ["CategoryId"] = categoryId.ToString(),
            ["TrackingMode"] = "Quantity",
            ["MinimumStock"] = "0",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync($"/Materiais/Editar/{materialId}", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Já existe um material com esse código no setor TI.",
            WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("TI-010", (await verifyDb.Materials.SingleAsync(item => item.Id == materialId)).Code);
        Assert.Equal(2, await verifyDb.Materials.CountAsync());
    }

    [Fact]
    public async Task CreatingIndividuallyTrackedMaterialPersistsItsTrackingMode()
    {
        var categoryId = await SeedCategoryAsync();
        var (response, _) = await SubmitMaterialAsync(categoryId, "LT-001", "Notebook", "IndividualAsset");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var material = await db.Materials.SingleAsync();
        Assert.Equal(MaterialTrackingMode.IndividualAsset, material.TrackingMode);
    }

    [Fact]
    public async Task DuplicateMaterialCodeIgnoringCaseIsRejectedWithinTheSector()
    {
        var categoryId = await SeedCategoryAsync();
        var (firstResponse, _) = await SubmitMaterialAsync(categoryId, "TI-001", "Teclado");
        Assert.Equal(HttpStatusCode.Redirect, firstResponse.StatusCode);

        var (duplicateResponse, html) = await SubmitMaterialAsync(categoryId, "ti-001", "Teclado reserva");

        Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);
        Assert.Contains("Já existe um material com esse código no setor TI.", WebUtility.HtmlDecode(html));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.Materials.CountAsync());
    }

    [Fact]
    public async Task AssistantCannotCreateMaterials()
    {
        var categoryId = await SeedCategoryAsync();
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Assistente");
        var (response, _) = await SubmitMaterialAsync(categoryId, "TI-002", "Mouse");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.Materials.ToListAsync());
    }

    [Fact]
    public async Task MaterialDetailsShowsCatalogDataAndCurrentStockBalance()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "TI-DETAIL-001", "Teclado mecânico");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "4", "NF-DETAIL-001")).StatusCode);
        var catalogHtml = WebUtility.HtmlDecode(await _client.GetStringAsync("/Materiais"));
        Assert.Contains($"/Materiais/Detalhes/{materialId}", catalogHtml);

        var response = await _client.GetAsync($"/Materiais/Detalhes/{materialId}");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("TI-DETAIL-001", html);
        Assert.Contains("Teclado mecânico", html);
        Assert.Contains("Periféricos", html);
        Assert.Contains("unidade", html);
        Assert.Contains("Quantidade", html);
        Assert.Contains(">4</dd>", html);
        Assert.Contains($"/Movimentacoes?MaterialId={materialId}", html);
    }

    [Fact]
    public async Task MaterialDetailsDoesNotExposeAnotherSectorsMaterial()
    {
        var (materialId, _) = await SeedMaterialInAnotherSectorAsync("FOREIGN-DETAIL-001", 0m);

        var response = await _client.GetAsync($"/Materiais/Detalhes/{materialId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MaterialCatalogFiltersByCategory()
    {
        var peripheralsCategory = await SeedCategoryAsync();
        int monitorsCategory;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var category = new MaterialCategory
            {
                Name = "Monitores",
                NormalizedName = "MONITORES",
                SectorId = 1
            };
            db.MaterialCategories.Add(category);
            await db.SaveChangesAsync();
            monitorsCategory = category.Id;
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitMaterialAsync(peripheralsCategory, "FILTER-KEYBOARD", "Teclado")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitMaterialAsync(monitorsCategory, "FILTER-MONITOR", "Monitor")).Response.StatusCode);

        var response = await _client.GetAsync($"/Materiais?CategoryFilter={monitorsCategory}");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Monitor", html);
        Assert.DoesNotContain("Teclado", html);
        Assert.Contains($"value=\"{monitorsCategory}\" selected", html);
    }

    [Fact]
    public async Task MaterialCatalogFiltersByActiveStatus()
    {
        var categoryId = await SeedCategoryAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitMaterialAsync(categoryId, "ACT-001", "Ativo")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitMaterialAsync(categoryId, "INACTIVE-001", "Inativo")).Response.StatusCode);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var inactive = await db.Materials.SingleAsync(material => material.Code == "INACTIVE-001");
            inactive.IsActive = false;
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/Materiais?ActiveFilter=false");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("INACTIVE-001", html);
        Assert.DoesNotContain("ACT-001", html);
    }

    [Fact]
    public async Task MaterialCatalogFiltersByTrackingMode()
    {
        var categoryId = await SeedCategoryAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitMaterialAsync(categoryId, "QTY-001", "Material por quantidade")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitMaterialAsync(categoryId, "ASSET-001", "Bem individual", "IndividualAsset")).Response.StatusCode);

        var response = await _client.GetAsync("/Materiais?TrackingFilter=IndividualAsset");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Bem individual", html);
        Assert.DoesNotContain("Material por quantidade", html);
    }

    [Fact]
    public async Task MaterialSearchFiltersByCodeNameAndDescription()
    {
        var categoryId = await SeedCategoryAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitMaterialAsync(categoryId, "TI-030", "Teclado", "Quantity")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitMaterialAsync(categoryId, "TI-031", "Mouse", "Quantity")).Response.StatusCode);

        var response = await _client.GetAsync("/Materiais?Search=mouse");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Mouse", html);
        Assert.DoesNotContain("Teclado", html);
    }

    [Fact]
    public async Task MaterialCatalogPaginatesResultsAndClampsInvalidPageNumbers()
    {
        var categoryId = await SeedCategoryAsync();
        for (var index = 1; index <= 12; index++)
        {
            var response = await SubmitMaterialAsync(categoryId, $"PG-{index:00}", $"Item {index:00}");
            Assert.Equal(HttpStatusCode.Redirect, response.Response.StatusCode);
        }

        var secondPage = await _client.GetAsync("/Materiais?PageNumber=2");
        var html = WebUtility.HtmlDecode(await secondPage.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
        Assert.Contains("Item 11", html);
        Assert.Contains("Item 12", html);
        Assert.DoesNotContain("Item 01", html);

        var filteredFirstPage = await _client.GetAsync($"/Materiais?CategoryFilter={categoryId}");
        var filteredHtml = WebUtility.HtmlDecode(await filteredFirstPage.Content.ReadAsStringAsync());
        Assert.Contains($"CategoryFilter={categoryId}", filteredHtml);
        var filteredSecondPage = await _client.GetAsync($"/Materiais?PageNumber=2&CategoryFilter={categoryId}");
        var filteredSecondHtml = WebUtility.HtmlDecode(await filteredSecondPage.Content.ReadAsStringAsync());
        Assert.Contains("Item 12", filteredSecondHtml);
        Assert.DoesNotContain("Item 01", filteredSecondHtml);

        var excessivePage = await _client.GetAsync("/Materiais?PageNumber=999");
        var lastPageHtml = WebUtility.HtmlDecode(await excessivePage.Content.ReadAsStringAsync());
        Assert.Contains("Item 12", lastPageHtml);
        Assert.DoesNotContain("Item 01", lastPageHtml);
    }

    [Fact]
    public async Task StockReceiptPageIsAvailableToStockOperators()
    {
        var response = await _client.GetAsync("/Movimentacoes/Entrada");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Registrar entrada", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StockReceiptFormRendersANewSubmissionKey()
    {
        var categoryId = await SeedCategoryAsync();
        Assert.Equal(HttpStatusCode.Redirect,
            (await SubmitMaterialAsync(categoryId, "IDEMP-001", "Material idempotente")).Response.StatusCode);

        var response = await _client.GetAsync("/Movimentacoes/Entrada");
        var html = await response.Content.ReadAsStringAsync();
        var submissionKey = Regex.Match(html,
            @"name=""SubmissionId""[^>]*value=""([^""]+)""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(submissionKey.Success, "The receipt form should include its idempotency key.");
        Assert.True(Guid.TryParse(submissionKey.Groups[1].Value, out var parsedKey));
        Assert.NotEqual(Guid.Empty, parsedKey);
    }

    [Fact]
    public async Task StockReceiptPageIsForbiddenToRolesWithoutStockOperationPermission()
    {
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Visitante");

        var response = await _client.GetAsync("/Movimentacoes/Entrada");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StockReceiptPersistsAnAuditablePositiveEntry()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "RC-001", "Cabo de rede");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var page = await _client.GetAsync("/Movimentacoes/Entrada");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submissionKey = Regex.Match(html, @"name=""SubmissionId""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The receipt form should emit an antiforgery token.");
        Assert.True(submissionKey.Success, "The receipt form should emit an idempotency key.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Quantity"] = "12",
            ["Reference"] = "NF 2026-001",
            ["SubmissionId"] = submissionKey.Groups[1].Value,
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });

        var response = await _client.PostAsync("/Movimentacoes/Entrada", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var movementCount = await verifyDb.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS Value FROM InventoryMovements WHERE MaterialId = {materialId}").SingleAsync();
        var quantity = await verifyDb.Database.SqlQuery<decimal>(
            $"SELECT Quantity AS Value FROM InventoryMovements WHERE MaterialId = {materialId}").SingleAsync();
        var movementType = await verifyDb.Database.SqlQuery<string>(
            $"SELECT Type AS Value FROM InventoryMovements WHERE MaterialId = {materialId}").SingleAsync();
        var actor = await verifyDb.Database.SqlQuery<string>(
            $"SELECT ActorUserId AS Value FROM InventoryMovements WHERE MaterialId = {materialId}").SingleAsync();
        var reference = await verifyDb.Database.SqlQuery<string>(
            $"SELECT Reference AS Value FROM InventoryMovements WHERE MaterialId = {materialId}").SingleAsync();
        Assert.Equal(1, movementCount);
        Assert.Equal(12m, quantity);
        Assert.Equal("Entry", movementType);
        Assert.Equal("pilot-user", actor);
        Assert.Equal("NF 2026-001", reference);

        var catalog = await _client.GetAsync("/Materiais");
        var catalogHtml = WebUtility.HtmlDecode(await catalog.Content.ReadAsStringAsync());
        Assert.Contains(">12</td>", catalogHtml);
    }

    [Fact]
    public async Task StockReceiptWithoutSubmissionKeyIsRejectedWithoutLedgerWrite()
    {
        var categoryId = await SeedCategoryAsync();
        Assert.Equal(HttpStatusCode.Redirect,
            (await SubmitMaterialAsync(categoryId, "IDEMP-MISSING-001", "Sem chave")).Response.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var page = await _client.GetAsync("/Movimentacoes/Entrada");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Quantity"] = "1",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });

        var response = await _client.PostAsync("/Movimentacoes/Entrada", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(0, await verifyDb.InventoryMovements.CountAsync(movement => movement.MaterialId == materialId));
    }

    [Fact]
    public async Task StockReceiptWithMalformedSubmissionKeyIsRejectedWithoutLedgerWrite()
    {
        var categoryId = await SeedCategoryAsync();
        Assert.Equal(HttpStatusCode.Redirect,
            (await SubmitMaterialAsync(categoryId, "IDEMP-INVALID-001", "Chave inválida")).Response.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var page = await _client.GetAsync("/Movimentacoes/Entrada");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Quantity"] = "1",
            ["SubmissionId"] = "not-a-guid",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });

        var response = await _client.PostAsync("/Movimentacoes/Entrada", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(0, await verifyDb.InventoryMovements.CountAsync(movement => movement.MaterialId == materialId));
    }

    [Fact]
    public async Task ReplayingReceiptWithSameSubmissionKeyAndPayloadCreatesOnlyOneMovement()
    {
        var categoryId = await SeedCategoryAsync();
        Assert.Equal(HttpStatusCode.Redirect,
            (await SubmitMaterialAsync(categoryId, "IDEMP-REPLAY-001", "Entrada com repetição")).Response.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var page = await _client.GetAsync("/Movimentacoes/Entrada");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submissionKey = Regex.Match(html, @"name=""SubmissionId""[^>]*value=""([^""]+)""");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(token.Success);
        Assert.True(submissionKey.Success);

        FormUrlEncodedContent CreateForm(string quantity = "2") => new(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Quantity"] = quantity,
            ["Reference"] = "NF-IDEMP-001",
            ["SubmissionId"] = submissionKey.Groups[1].Value,
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });

        using var firstForm = CreateForm();
        using var replayForm = CreateForm();
        using var conflictingForm = CreateForm("3");
        var firstResponse = await _client.PostAsync("/Movimentacoes/Entrada", firstForm);
        var replayResponse = await _client.PostAsync("/Movimentacoes/Entrada", replayForm);
        var conflictResponse = await _client.PostAsync("/Movimentacoes/Entrada", conflictingForm);

        Assert.Equal(HttpStatusCode.Redirect, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, replayResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync(movement => movement.MaterialId == materialId));
        Assert.Equal(2m, await verifyDb.InventoryMovements
            .Where(movement => movement.MaterialId == materialId)
            .SumAsync(movement => movement.Quantity));
    }

    [Fact]
    public async Task StockReceiptPersistsCanonicalSubmissionFingerprintWithTheMovement()
    {
        var categoryId = await SeedCategoryAsync();
        Assert.Equal(HttpStatusCode.Redirect,
            (await SubmitMaterialAsync(categoryId, "IDEMP-FINGERPRINT-001", "Entrada com impressão digital")).Response.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var page = await _client.GetAsync("/Movimentacoes/Entrada");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submissionKey = Regex.Match(html, @"name=""SubmissionId""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Quantity"] = "2.500",
            ["Reference"] = "NF-FINGERPRINT-001",
            ["SubmissionId"] = submissionKey.Groups[1].Value,
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        Assert.Equal(HttpStatusCode.Redirect,
            (await _client.PostAsync("/Movimentacoes/Entrada", form)).StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await verifyDb.Database.OpenConnectionAsync();
        var connection = verifyDb.Database.GetDbConnection();
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('InventoryMovements') WHERE name = 'SubmissionFingerprint'";
        Assert.Equal(1L, Convert.ToInt64(await schemaCommand.ExecuteScalarAsync()));

        var fingerprint = await verifyDb.InventoryMovements
            .Where(movement => movement.SubmissionId == Guid.Parse(submissionKey.Groups[1].Value))
            .Select(movement => movement.SubmissionFingerprint)
            .SingleAsync();
        Assert.Matches("^[0-9A-F]{64}$", fingerprint);
    }

    [Fact]
    public async Task ZeroQuantityReceiptIsRejectedWithoutLedgerWrite()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "RC-002", "Adaptador");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var page = await _client.GetAsync("/Movimentacoes/Entrada");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submissionKey = Regex.Match(html, @"name=""SubmissionId""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Quantity"] = "0",
            ["SubmissionId"] = submissionKey.Groups[1].Value,
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });

        var response = await _client.PostAsync("/Movimentacoes/Entrada", form);
        var responseHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("A quantidade deve ser maior que zero.", responseHtml);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var movementCount = await verifyDb.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS Value FROM InventoryMovements WHERE MaterialId = {materialId}").SingleAsync();
        Assert.Equal(0, movementCount);
    }

    [Fact]
    public async Task MovementHistoryListsReceiptAndFiltersByMaterialTypeActorAndDate()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "HS-001", "Hub USB");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var receipt = await SubmitReceiptAsync(materialId, "4", "NF-HIST-001");
        Assert.Equal(HttpStatusCode.Redirect, receipt.StatusCode);

        var history = await _client.GetAsync("/Movimentacoes");
        var html = WebUtility.HtmlDecode(await history.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Contains("Hub USB", html);
        Assert.Contains("NF-HIST-001", html);
        Assert.Contains("pilot-user", html);

        var filtered = await _client.GetAsync($"/Movimentacoes?MaterialId={materialId}");
        var filteredHtml = WebUtility.HtmlDecode(await filtered.Content.ReadAsStringAsync());
        Assert.Contains("NF-HIST-001", filteredHtml);

        var typed = await _client.GetAsync("/Movimentacoes?Type=Entry");
        Assert.Contains("NF-HIST-001", WebUtility.HtmlDecode(await typed.Content.ReadAsStringAsync()));
        var wrongType = await _client.GetAsync("/Movimentacoes?Type=Loan");
        Assert.DoesNotContain("NF-HIST-001", WebUtility.HtmlDecode(await wrongType.Content.ReadAsStringAsync()));

        var actorFiltered = await _client.GetAsync("/Movimentacoes?Actor=pilot-user");
        Assert.Contains("NF-HIST-001", WebUtility.HtmlDecode(await actorFiltered.Content.ReadAsStringAsync()));
        var unknownActor = await _client.GetAsync("/Movimentacoes?Actor=missing-user");
        Assert.DoesNotContain("NF-HIST-001", WebUtility.HtmlDecode(await unknownActor.Content.ReadAsStringAsync()));

        var outOfRange = await _client.GetAsync("/Movimentacoes?From=2000-01-01&To=2000-01-01");
        Assert.DoesNotContain("NF-HIST-001", WebUtility.HtmlDecode(await outOfRange.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task DashboardOmitsLowStockAlertWhenNoMaterialsAreBelowMinimum()
    {
        var response = await _client.GetAsync("/");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-metric=\"low-stock\">0", html);
        Assert.DoesNotContain("data-stock-alert", html);
    }

    [Fact]
    public async Task DashboardReflectsOpenLoanCountAfterReturn()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "DB-LOAN-001", "Notebook");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "3", "NF-DB-LOAN-001")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitLoanAsync(materialId, "João Pereira", "2", "EM-DB-LOAN-001")).StatusCode);

        var openDashboard = WebUtility.HtmlDecode(await _client.GetStringAsync("/"));
        Assert.Contains("data-metric=\"open-loans\">1", openDashboard);
        int loanId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            loanId = await db.InventoryLoans.Select(loan => loan.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReturnAsync(loanId, "2")).StatusCode);

        var returnedDashboard = WebUtility.HtmlDecode(await _client.GetStringAsync("/"));
        Assert.Contains("data-metric=\"open-loans\">0", returnedDashboard);
    }

    [Fact]
    public async Task DashboardShowsMaterialCountLowStockAndRecentMovement()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "DB-001", "Dock USB-C");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "2", "NF-DASH-001")).StatusCode);

        var response = await _client.GetAsync("/");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-metric=\"materials\">1", html);
        Assert.Contains("data-metric=\"low-stock\">1", html);
        Assert.Contains("data-stock-alert", html);
        Assert.Contains("role=\"status\"", html);
        Assert.Contains("1 material está abaixo do estoque mínimo", html);
        Assert.Contains("Ver materiais para reposição", html);
        Assert.Contains("NF-DASH-001", html);
        Assert.Contains("Dock USB-C", html);
    }

    [Fact]
    public async Task LowStockCsvExportsOnlyBelowMinimumMaterialsAndNeutralizesFormulas()
    {
        var categoryId = await SeedCategoryAsync();
        var (lowCreated, _) = await SubmitMaterialAsync(categoryId, "LOW-001", "=HYPERLINK(\"https://invalid\",\"click\")");
        var (stockedCreated, _) = await SubmitMaterialAsync(categoryId, "FULL-001", "Mouse stocked");
        Assert.Equal(HttpStatusCode.Redirect, lowCreated.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, stockedCreated.StatusCode);

        int stockedMaterialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            stockedMaterialId = await db.Materials.Where(material => material.Code == "FULL-001").Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(stockedMaterialId, "6", "NF-FULL-001")).StatusCode);

        var response = await _client.GetAsync("/?handler=LowStockCsv");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var csv = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("filename=estoque-abaixo-minimo.csv", response.Content.Headers.ContentDisposition?.ToString());
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "CSV must begin with a UTF-8 BOM.");
        Assert.Contains("\"'=HYPERLINK(\"\"https://invalid\"\",\"\"click\"\")\"", csv);
        Assert.Contains(",0,5", csv);
        Assert.Contains("LOW-001", csv);
        Assert.DoesNotContain("FULL-001", csv);
        Assert.DoesNotContain("NF-FULL-001", csv);
        Assert.Equal(2, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        var dashboardHtml = WebUtility.HtmlDecode(await _client.GetStringAsync("/"));
        Assert.Contains("Exportar lista de reposição em CSV", dashboardHtml);
        Assert.Contains("handler=LowStockCsv", dashboardHtml);
    }

    [Fact]
    public async Task MovementCsvExportUsesCurrentFiltersAndNeutralizesSpreadsheetFormulas()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "CSV-001", "Monitor; 27\"");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var (otherCreated, _) = await SubmitMaterialAsync(categoryId, "CSV-002", "Mouse");
        Assert.Equal(HttpStatusCode.Redirect, otherCreated.StatusCode);
        int materialId;
        int otherMaterialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Where(material => material.Code == "CSV-001").Select(material => material.Id).SingleAsync();
            otherMaterialId = await db.Materials.Where(material => material.Code == "CSV-002").Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(
            materialId, "2", "=HYPERLINK(\"https://invalid\",\"click\")")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(otherMaterialId, "1", "OTHER-001")).StatusCode);

        var response = await _client.GetAsync($"/Movimentacoes?handler=ExportCsv&MaterialId={materialId}");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var csv = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("filename=movimentacoes.csv", response.Content.Headers.ContentDisposition?.ToString());
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "CSV must begin with a UTF-8 BOM.");
        Assert.Contains("'=HYPERLINK", csv);
        Assert.Contains("\"Monitor; 27\"\"\"", csv);
        Assert.Contains("CSV-001", csv);
        Assert.DoesNotContain("CSV-002", csv);
        Assert.DoesNotContain("OTHER-001", csv);
        Assert.Equal(2, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task LoanCannotExceedAvailableBalance()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "LOAN-002", "Mouse");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var response = await SubmitLoanAsync(materialId, "Ana Silva", "1", "EM-SEM-SALDO");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Saldo insuficiente", html);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.InventoryLoans.ToListAsync());
        Assert.Empty(await verifyDb.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task IndividualAssetCannotBeReceivedThroughQuantityOnlyForm()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ASSET-ENTRY-001", "Projetor", "IndividualAsset");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var createdMaterial = await db.Materials.SingleAsync(material => material.Id == materialId);
            Assert.Equal(MaterialTrackingMode.IndividualAsset, createdMaterial.TrackingMode);
        }
        var response = await SubmitReceiptAsync(materialId, "1", "NF-ASSET-ENTRY");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Informe o número de série ou patrimônio na página de ativos.", html);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task ReplayingIndividualAssetRegistrationWithSameKeyDoesNotDuplicateAndChangedPayloadConflicts()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ASSET-REPLAY-001", "Projetor", "IndividualAsset");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var page = await _client.GetAsync("/Ativos");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submission = Regex.Match(html, @"name=""AssetRegistrationSubmissionId""[^>]*value=""([^""]+)""");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(token.Success, "The asset registry should emit an antiforgery token.");
        Assert.True(submission.Success, "The registration form should emit an idempotency key.");
        var submissionId = Guid.Parse(submission.Groups[1].Value);

        async Task<HttpResponseMessage> SubmitRegistrationAsync(string identifier, string reference)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["MaterialId"] = materialId.ToString(),
                ["Identifier"] = identifier,
                ["Reference"] = reference,
                ["AssetRegistrationSubmissionId"] = submissionId.ToString(),
                ["__RequestVerificationToken"] = token.Groups[1].Value
            });
            return await _client.PostAsync("/Ativos?handler=Register", form);
        }

        using var first = await SubmitRegistrationAsync("SN-REPLAY-001", "NF-REPLAY-001");
        using var replay = await SubmitRegistrationAsync("SN-REPLAY-001", "NF-REPLAY-001");
        using var changedPayload = await SubmitRegistrationAsync("SN-REPLAY-002", "NF-REPLAY-001");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changedPayload.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var asset = await verifyDb.InventoryAssets.SingleAsync(item => item.MaterialId == materialId);
        Assert.Equal("SN-REPLAY-001", asset.Identifier);
        var movement = await verifyDb.InventoryMovements.SingleAsync(item => item.MaterialId == materialId);
        Assert.Equal(submissionId, movement.SubmissionId);
        Assert.Matches("^[0-9A-F]{64}$", movement.SubmissionFingerprint);
    }

    [Fact]
    public async Task RegisteringIndividualAssetCreatesItsAuditEntry()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ASSET-REG-001", "Projetor", "IndividualAsset");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var page = await _client.GetAsync("/Ativos");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Ativos individualizados", WebUtility.HtmlDecode(html));
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The asset registry should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Identifier"] = " SN-REG-001 ",
            ["Reference"] = "NF-REG-001",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ativos?handler=Register", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var asset = await verifyDb.InventoryAssets.SingleAsync(item => item.MaterialId == materialId);
        Assert.Equal("SN-REG-001", asset.Identifier);
        Assert.Equal("SN-REG-001", asset.NormalizedIdentifier);
        Assert.Equal(InventoryAssetStatus.Available, asset.Status);
        Assert.Equal("pilot-user", asset.RegisteredByUserId);
        var movement = await verifyDb.InventoryMovements.SingleAsync(item => item.MaterialId == materialId);
        Assert.Equal(InventoryMovementType.Entry, movement.Type);
        Assert.Equal(1m, movement.Quantity);
        Assert.Equal(asset.Id, movement.InventoryAssetId);
        Assert.Equal("NF-REG-001", movement.Reference);
        Assert.Equal("pilot-user", movement.ActorUserId);
    }

    [Fact]
    public async Task IndividualAssetCannotBeConsumedThroughQuantityOnlyForm()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ASSET-CONS-001", "Projetor", "IndividualAsset");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        int assetId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var material = await db.Materials.SingleAsync();
            materialId = material.Id;
            var asset = new InventoryAsset
            {
                MaterialId = material.Id,
                SectorId = material.SectorId,
                Identifier = "PAT-CONS-001",
                NormalizedIdentifier = "PAT-CONS-001",
                Status = InventoryAssetStatus.Available,
                RegisteredAtUtc = DateTimeOffset.UtcNow,
                RegisteredByUserId = "pilot-user",
                RegisteredByName = "pilot-user"
            };
            db.InventoryAssets.Add(asset);
            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = material.Id,
                SectorId = material.SectorId,
                Type = InventoryMovementType.Entry,
                Quantity = 1m,
                Asset = asset,
                ActorUserId = "pilot-user",
                ActorName = "pilot-user",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            assetId = asset.Id;
        }

        var response = await SubmitConsumptionAsync(materialId, "1", "BAIXA-ATIVO");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Selecione um material controlado por quantidade.", html);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync());
        Assert.Equal(InventoryAssetStatus.Available,
            (await verifyDb.InventoryAssets.SingleAsync(asset => asset.Id == assetId)).Status);
    }

    [Fact]
    public async Task ReplayingIndividualAssetConsumptionWithSameKeyDoesNotDuplicateAndChangedPayloadConflicts()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ASSET-CONSUME-REPLAY-001", "Projetor", "IndividualAsset");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        int assetId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var material = await db.Materials.SingleAsync();
            materialId = material.Id;
            var asset = new InventoryAsset
            {
                MaterialId = material.Id,
                SectorId = material.SectorId,
                Identifier = "PAT-CONSUME-REPLAY-001",
                NormalizedIdentifier = "PAT-CONSUME-REPLAY-001",
                Status = InventoryAssetStatus.Available,
                RegisteredAtUtc = DateTimeOffset.UtcNow,
                RegisteredByUserId = "pilot-user",
                RegisteredByName = "pilot-user"
            };
            db.InventoryAssets.Add(asset);
            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = material.Id,
                SectorId = material.SectorId,
                Type = InventoryMovementType.Entry,
                Quantity = 1m,
                Asset = asset,
                ActorUserId = "pilot-user",
                ActorName = "pilot-user",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            assetId = asset.Id;
        }

        var page = await _client.GetAsync("/Ativos");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submission = Regex.Match(html, @"name=""AssetConsumptionSubmissionId""[^>]*value=""([^""]+)""");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(submission.Success, "The consumption form should emit an idempotency key.");
        var submissionId = Guid.Parse(submission.Groups[1].Value);

        async Task<HttpResponseMessage> SubmitConsumptionAsync(string reference)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["AssetId"] = assetId.ToString(),
                ["Reference"] = reference,
                ["AssetConsumptionSubmissionId"] = submissionId.ToString(),
                ["__RequestVerificationToken"] = token.Groups[1].Value
            });
            return await _client.PostAsync("/Ativos?handler=ConsumeAsset", form);
        }

        using var first = await SubmitConsumptionAsync("BAIXA-REPLAY-001");
        using var replay = await SubmitConsumptionAsync("BAIXA-REPLAY-001");
        using var changedPayload = await SubmitConsumptionAsync("BAIXA-REPLAY-002");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changedPayload.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(InventoryAssetStatus.Consumed,
            (await verifyDb.InventoryAssets.SingleAsync(asset => asset.Id == assetId)).Status);
        var movement = await verifyDb.InventoryMovements.SingleAsync(item => item.InventoryAssetId == assetId
            && item.Type == InventoryMovementType.Consumption);
        Assert.Equal("BAIXA-REPLAY-001", movement.Reference);
        Assert.Equal(submissionId, movement.SubmissionId);
        Assert.Matches("^[0-9A-F]{64}$", movement.SubmissionFingerprint);
        Assert.Equal(2, await verifyDb.InventoryMovements.CountAsync(item => item.MaterialId == materialId));
    }

    [Fact]
    public async Task IndividualAssetConsumptionMarksItConsumedAndRecordsTheDebit()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ASSET-CONSUME-001", "Projetor", "IndividualAsset");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        int assetId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var material = await db.Materials.SingleAsync();
            materialId = material.Id;
            var asset = new InventoryAsset
            {
                MaterialId = material.Id,
                SectorId = material.SectorId,
                Identifier = "PAT-CONSUME-001",
                NormalizedIdentifier = "PAT-CONSUME-001",
                Status = InventoryAssetStatus.Available,
                RegisteredAtUtc = DateTimeOffset.UtcNow,
                RegisteredByUserId = "pilot-user",
                RegisteredByName = "pilot-user"
            };
            db.InventoryAssets.Add(asset);
            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = material.Id,
                SectorId = material.SectorId,
                Type = InventoryMovementType.Entry,
                Quantity = 1m,
                Asset = asset,
                ActorUserId = "pilot-user",
                ActorName = "pilot-user",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            assetId = asset.Id;
        }

        var page = await _client.GetAsync("/Ativos");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["AssetId"] = assetId.ToString(),
            ["Reference"] = "BAIXA-PAT-001",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ativos?handler=ConsumeAsset", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(InventoryAssetStatus.Consumed,
            (await verifyDb.InventoryAssets.SingleAsync(asset => asset.Id == assetId)).Status);
        var movements = await verifyDb.InventoryMovements.Where(movement => movement.MaterialId == materialId).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Contains(movements, movement => movement.Type == InventoryMovementType.Consumption
            && movement.InventoryAssetId == assetId && movement.Quantity == 1m && movement.Reference == "BAIXA-PAT-001");
    }

    [Fact]
    public async Task ReplayingIndividualAssetLoanWithSameKeyDoesNotDuplicateAndChangedPayloadConflicts()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ASSET-LOAN-REPLAY-001", "Projetor", "IndividualAsset");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        int assetId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var material = await db.Materials.SingleAsync();
            materialId = material.Id;
            var asset = new InventoryAsset
            {
                MaterialId = material.Id,
                SectorId = material.SectorId,
                Identifier = "PAT-LOAN-REPLAY-001",
                NormalizedIdentifier = "PAT-LOAN-REPLAY-001",
                Status = InventoryAssetStatus.Available,
                RegisteredAtUtc = DateTimeOffset.UtcNow,
                RegisteredByUserId = "pilot-user",
                RegisteredByName = "pilot-user"
            };
            db.InventoryAssets.Add(asset);
            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = material.Id,
                SectorId = material.SectorId,
                Type = InventoryMovementType.Entry,
                Quantity = 1m,
                Asset = asset,
                ActorUserId = "pilot-user",
                ActorName = "pilot-user",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            assetId = asset.Id;
        }

        var page = await _client.GetAsync("/Ativos");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submission = Regex.Match(html, @"name=""AssetLoanSubmissionId""[^>]*value=""([^""]+)""");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(submission.Success, "The loan form should emit an idempotency key.");
        var submissionId = Guid.Parse(submission.Groups[1].Value);

        async Task<HttpResponseMessage> SubmitLoanAsync(string borrowerName)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["AssetId"] = assetId.ToString(),
                ["BorrowerName"] = borrowerName,
                ["LoanReference"] = "EM-REPLAY-001",
                ["AssetLoanSubmissionId"] = submissionId.ToString(),
                ["__RequestVerificationToken"] = token.Groups[1].Value
            });
            return await _client.PostAsync("/Ativos?handler=LoanAsset", form);
        }

        using var first = await SubmitLoanAsync("Rafael Lima");
        using var replay = await SubmitLoanAsync("Rafael Lima");
        using var changedPayload = await SubmitLoanAsync("Marina Souza");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changedPayload.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(InventoryAssetStatus.Loaned,
            (await verifyDb.InventoryAssets.SingleAsync(asset => asset.Id == assetId)).Status);
        var loan = await verifyDb.InventoryLoans.SingleAsync(item => item.InventoryAssetId == assetId);
        Assert.Equal("Rafael Lima", loan.BorrowerName);
        var movement = await verifyDb.InventoryMovements.SingleAsync(item => item.InventoryAssetId == assetId
            && item.Type == InventoryMovementType.Loan);
        Assert.Equal(submissionId, movement.SubmissionId);
        Assert.Matches("^[0-9A-F]{64}$", movement.SubmissionFingerprint);
        Assert.Equal(2, await verifyDb.InventoryMovements.CountAsync(item => item.MaterialId == materialId));
    }

    [Fact]
    public async Task IndividualAssetLoanRecordsOneUnitAndChangesItsStatus()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ASSET-LOAN-001", "Projetor", "IndividualAsset");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var registerPage = await _client.GetAsync("/Ativos");
        var registerHtml = await registerPage.Content.ReadAsStringAsync();
        var registerToken = Regex.Match(registerHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using (var registerForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Identifier"] = "PAT-LOAN-001",
            ["__RequestVerificationToken"] = registerToken.Groups[1].Value
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, (await _client.PostAsync("/Ativos?handler=Register", registerForm)).StatusCode);
        }
        int assetId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            assetId = await db.InventoryAssets.Select(asset => asset.Id).SingleAsync();
        }

        var loanPage = await _client.GetAsync("/Ativos");
        var loanHtml = await loanPage.Content.ReadAsStringAsync();
        var loanToken = Regex.Match(loanHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var loanForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["AssetId"] = assetId.ToString(),
            ["BorrowerName"] = "Rafael Lima",
            ["LoanReference"] = "EM-PAT-001",
            ["__RequestVerificationToken"] = loanToken.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ativos?handler=LoanAsset", loanForm);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(InventoryAssetStatus.Loaned,
            (await verifyDb.InventoryAssets.SingleAsync(asset => asset.Id == assetId)).Status);
        var loan = await verifyDb.InventoryLoans.SingleAsync(item => item.InventoryAssetId == assetId);
        Assert.Equal(1m, loan.Quantity);
        Assert.Equal("Rafael Lima", loan.BorrowerName);
        Assert.Equal("EM-PAT-001", loan.Reference);
        var movements = await verifyDb.InventoryMovements.Where(movement => movement.InventoryAssetId == assetId).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Contains(movements, movement => movement.Type == InventoryMovementType.Loan
            && movement.InventoryLoanId == loan.Id && movement.Quantity == 1m && movement.ActorName == "pilot-user");
    }

    [Fact]
    public async Task ReplayingIndividualAssetReturnWithSameKeyDoesNotDuplicateAndChangedPayloadConflicts()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ASSET-RETURN-REPLAY-001", "Projetor", "IndividualAsset");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int assetId;
        int loanId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var material = await db.Materials.SingleAsync();
            var asset = new InventoryAsset
            {
                MaterialId = material.Id,
                SectorId = material.SectorId,
                Identifier = "PAT-RETURN-REPLAY-001",
                NormalizedIdentifier = "PAT-RETURN-REPLAY-001",
                Status = InventoryAssetStatus.Loaned,
                RegisteredAtUtc = DateTimeOffset.UtcNow,
                RegisteredByUserId = "pilot-user",
                RegisteredByName = "pilot-user"
            };
            db.InventoryAssets.Add(asset);
            var loan = new InventoryLoan
            {
                Material = material,
                SectorId = material.SectorId,
                Asset = asset,
                BorrowerName = "Rafael Lima",
                Quantity = 1m,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Reference = "EM-RETURN-REPLAY-001",
                CreatedByUserId = "pilot-user",
                CreatedByName = "pilot-user"
            };
            db.InventoryLoans.Add(loan);
            db.InventoryMovements.Add(new InventoryMovement
            {
                Material = material,
                SectorId = material.SectorId,
                Type = InventoryMovementType.Loan,
                Quantity = 1m,
                Reference = loan.Reference,
                Loan = loan,
                Asset = asset,
                ActorUserId = "pilot-user",
                ActorName = "pilot-user",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            assetId = asset.Id;
            loanId = loan.Id;
        }

        var page = await _client.GetAsync("/Ativos");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submission = Regex.Match(html, @"name=""AssetReturnSubmissionId""[^>]*value=""([^""]+)""");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(submission.Success, "The return form should emit an idempotency key.");
        var submissionId = Guid.Parse(submission.Groups[1].Value);

        async Task<HttpResponseMessage> SubmitReturnAsync(int postedLoanId)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["LoanId"] = postedLoanId.ToString(),
                ["AssetReturnSubmissionId"] = submissionId.ToString(),
                ["__RequestVerificationToken"] = token.Groups[1].Value
            });
            return await _client.PostAsync("/Ativos?handler=ReturnAsset", form);
        }

        using var first = await SubmitReturnAsync(loanId);
        using var replay = await SubmitReturnAsync(loanId);
        using var changedPayload = await SubmitReturnAsync(loanId + 1);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changedPayload.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(InventoryAssetStatus.Available,
            (await verifyDb.InventoryAssets.SingleAsync(item => item.Id == assetId)).Status);
        var movement = await verifyDb.InventoryMovements.SingleAsync(item => item.InventoryLoanId == loanId
            && item.Type == InventoryMovementType.Return);
        Assert.Equal(submissionId, movement.SubmissionId);
        Assert.Matches("^[0-9A-F]{64}$", movement.SubmissionFingerprint);
        Assert.Equal(2, await verifyDb.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task IndividualAssetReturnRestoresAvailabilityAndAddsReturnMovement()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ASSET-RETURN-001", "Projetor", "IndividualAsset");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var assetsPage = await _client.GetAsync("/Ativos");
        var assetsHtml = await assetsPage.Content.ReadAsStringAsync();
        var registerToken = Regex.Match(assetsHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using (var registerForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Identifier"] = "PAT-RETURN-001",
            ["__RequestVerificationToken"] = registerToken.Groups[1].Value
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect,
                (await _client.PostAsync("/Ativos?handler=Register", registerForm)).StatusCode);
        }

        int assetId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            assetId = await db.InventoryAssets.Select(asset => asset.Id).SingleAsync();
        }
        var loanPage = await _client.GetAsync("/Ativos");
        var loanHtml = await loanPage.Content.ReadAsStringAsync();
        var loanToken = Regex.Match(loanHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using (var loanForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["AssetId"] = assetId.ToString(),
            ["BorrowerName"] = "Marina Souza",
            ["LoanReference"] = "EM-RETURN-001",
            ["__RequestVerificationToken"] = loanToken.Groups[1].Value
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect,
                (await _client.PostAsync("/Ativos?handler=LoanAsset", loanForm)).StatusCode);
        }

        int loanId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            loanId = await db.InventoryLoans.Select(loan => loan.Id).SingleAsync();
        }
        var returnPage = await _client.GetAsync("/Ativos");
        var returnHtml = await returnPage.Content.ReadAsStringAsync();
        var returnToken = Regex.Match(returnHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var returnForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["LoanId"] = loanId.ToString(),
            ["__RequestVerificationToken"] = returnToken.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ativos?handler=ReturnAsset", returnForm);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(InventoryAssetStatus.Available,
            (await verifyDb.InventoryAssets.SingleAsync(asset => asset.Id == assetId)).Status);
        var movements = await verifyDb.InventoryMovements.Where(movement => movement.MaterialId == materialId).ToListAsync();
        Assert.Equal(3, movements.Count);
        Assert.Contains(movements, movement => movement.Type == InventoryMovementType.Return
            && movement.InventoryLoanId == loanId && movement.InventoryAssetId == assetId && movement.Quantity == 1m);
    }

    [Fact]
    public async Task AssetOperationsCannotAddressAnotherSectorAssetId()
    {
        var page = await _client.GetAsync("/Ativos");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success);

        int assetId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var sector = new Sector { Name = "Outro Setor" };
            db.Sectors.Add(sector);
            await db.SaveChangesAsync();
            var category = new MaterialCategory
            {
                Name = "Equipamentos",
                NormalizedName = "EQUIPAMENTOS",
                SectorId = sector.Id
            };
            db.MaterialCategories.Add(category);
            await db.SaveChangesAsync();
            var material = new Material
            {
                Code = "FOREIGN-ASSET-001",
                NormalizedCode = "FOREIGN-ASSET-001",
                Name = "Projetor externo",
                Unit = "unidade",
                TrackingMode = MaterialTrackingMode.IndividualAsset,
                MinimumStock = 0m,
                CategoryId = category.Id,
                SectorId = sector.Id
            };
            db.Materials.Add(material);
            await db.SaveChangesAsync();
            var asset = new InventoryAsset
            {
                MaterialId = material.Id,
                SectorId = sector.Id,
                Identifier = "PAT-FOREIGN-001",
                NormalizedIdentifier = "PAT-FOREIGN-001",
                Status = InventoryAssetStatus.Available,
                RegisteredAtUtc = DateTimeOffset.UtcNow,
                RegisteredByUserId = "foreign-user",
                RegisteredByName = "Foreign User"
            };
            db.InventoryAssets.Add(asset);
            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = material.Id,
                SectorId = sector.Id,
                Type = InventoryMovementType.Entry,
                Quantity = 1m,
                Asset = asset,
                ActorUserId = "foreign-user",
                ActorName = "Foreign User",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            assetId = asset.Id;
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["AssetId"] = assetId.ToString(),
            ["BorrowerName"] = "Tentativa de acesso cruzado",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ativos?handler=LoanAsset", form);
        var responseHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("O ativo não está disponível para empréstimo.", responseHtml);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.InventoryLoans.ToListAsync());
        Assert.Equal(InventoryAssetStatus.Available,
            (await verifyDb.InventoryAssets.SingleAsync(asset => asset.Id == assetId)).Status);
        Assert.DoesNotContain("PAT-FOREIGN-001", responseHtml);
    }

    [Fact]
    public async Task IndividualAssetCannotBeBorrowedThroughQuantityLoanRoute()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "LOAN-ASSET-001", "Projector", "IndividualAsset");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        var assetsPage = await _client.GetAsync("/Ativos");
        var assetsHtml = await assetsPage.Content.ReadAsStringAsync();
        var token = Regex.Match(assetsHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using (var registrationForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Identifier"] = "PAT-LOAN-ASSET-001",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect,
                (await _client.PostAsync("/Ativos?handler=Register", registrationForm)).StatusCode);
        }

        var response = await SubmitLoanAsync(materialId, "Bruno Costa", "1", "EM-ASSET-001");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Use a página Ativos individualizados para emprestar este patrimônio.", html);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.InventoryLoans.ToListAsync());
        Assert.Single(await verifyDb.InventoryMovements.ToListAsync());
        Assert.Equal(InventoryAssetStatus.Available,
            (await verifyDb.InventoryAssets.SingleAsync()).Status);
    }

    [Fact]
    public async Task AdjustmentRequiresNonzeroDifference()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-ZERO-001", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Gestor");
        var page = await _client.GetAsync("/Ajustes");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["DeltaQuantity"] = "0",
            ["Reason"] = "Diferença nula",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ajustes?handler=Submit", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("diferente de zero", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.InventoryAdjustmentRequests.ToListAsync());
        Assert.Empty(await verifyDb.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task AdjustmentRequiresReasonBeforeSaving()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-NO-REASON-001", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Assistente");
        var page = await _client.GetAsync("/Ajustes");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["DeltaQuantity"] = "1",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ajustes?handler=Submit", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Informe a justificativa do ajuste", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.InventoryAdjustmentRequests.ToListAsync());
        Assert.Empty(await verifyDb.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task ManagerCannotApplyAdjustmentThatWouldMakeBalanceNegative()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-NO-STOCK-001", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Gestor");
        var page = await _client.GetAsync("/Ajustes");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["DeltaQuantity"] = "-1",
            ["Reason"] = "Contagem sem saldo disponível",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ajustes?handler=Submit", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("não pode deixar o saldo abaixo de zero", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.InventoryAdjustmentRequests.ToListAsync());
        Assert.Empty(await verifyDb.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task ManagerAppliesSignedAdjustmentToLedgerAndBalance()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-MANAGER-001", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "5", "NF-ADJ-MANAGER-001")).StatusCode);

        _client.DefaultRequestHeaders.Add("X-Test-Role", "Gestor");
        var page = await _client.GetAsync("/Ajustes");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var pageHtml = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(pageHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The adjustment page should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["DeltaQuantity"] = "-2",
            ["Reason"] = "Diferença de inventário confirmada",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ajustes?handler=Submit", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = await verifyDb.InventoryAdjustmentRequests.SingleAsync();
        Assert.Equal(InventoryAdjustmentStatus.Approved, request.Status);
        Assert.Equal(-2m, request.DeltaQuantity);
        Assert.Equal("pilot-user", request.DecidedByUserId);
        var movement = await verifyDb.InventoryMovements.SingleAsync(item => item.Type == InventoryMovementType.Adjustment);
        Assert.Equal(-2m, movement.Quantity);
        Assert.Equal(request.Id, movement.InventoryAdjustmentRequestId);
        Assert.Contains(">3</td>", WebUtility.HtmlDecode(await _client.GetStringAsync("/Materiais")));
    }

    [Fact]
    public async Task ManagerApprovalAppliesPendingAdjustmentAndRecordsDecision()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-APPROVAL-001", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "5", "NF-ADJ-APPROVAL-001")).StatusCode);

        _client.DefaultRequestHeaders.Add("X-Test-Role", "Assistente");
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", "assistant-1");
        var assistantPage = await _client.GetAsync("/Ajustes");
        Assert.Equal(HttpStatusCode.OK, assistantPage.StatusCode);
        var assistantHtml = await assistantPage.Content.ReadAsStringAsync();
        var assistantToken = Regex.Match(assistantHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["DeltaQuantity"] = "-2",
            ["Reason"] = "Contagem física aprovada",
            ["__RequestVerificationToken"] = assistantToken.Groups[1].Value
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, (await _client.PostAsync("/Ajustes?handler=Submit", form)).StatusCode);
        }
        int requestId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            requestId = await db.InventoryAdjustmentRequests.Select(request => request.Id).SingleAsync();
        }

        _client.DefaultRequestHeaders.Remove("X-Test-Role");
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Gestor");
        _client.DefaultRequestHeaders.Remove("X-Test-User-Id");
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", "manager-1");
        var managerPage = await _client.GetAsync("/Ajustes");
        Assert.Equal(HttpStatusCode.OK, managerPage.StatusCode);
        var managerHtml = await managerPage.Content.ReadAsStringAsync();
        var managerToken = Regex.Match(managerHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var approvalForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["RequestId"] = requestId.ToString(),
            ["__RequestVerificationToken"] = managerToken.Groups[1].Value
        });
        var approval = await _client.PostAsync("/Ajustes?handler=Approve", approvalForm);

        Assert.Equal(HttpStatusCode.Redirect, approval.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = await verifyDb.InventoryAdjustmentRequests.SingleAsync();
        Assert.Equal(InventoryAdjustmentStatus.Approved, request.Status);
        Assert.Equal("manager-1", request.DecidedByUserId);
        var movement = await verifyDb.InventoryMovements.SingleAsync(item => item.Type == InventoryMovementType.Adjustment);
        Assert.Equal(-2m, movement.Quantity);
        Assert.Equal(request.Id, movement.InventoryAdjustmentRequestId);
        Assert.Contains(">3</td>", WebUtility.HtmlDecode(await _client.GetStringAsync("/Materiais")));
    }

    [Fact]
    public async Task ManagerRejectionClosesRequestWithoutChangingStock()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-REJECT-001", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "5", "NF-ADJ-REJECT-001")).StatusCode);

        _client.DefaultRequestHeaders.Add("X-Test-Role", "Assistente");
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", "assistant-2");
        var assistantPage = await _client.GetAsync("/Ajustes");
        var assistantHtml = await assistantPage.Content.ReadAsStringAsync();
        var assistantToken = Regex.Match(assistantHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["DeltaQuantity"] = "-1",
            ["Reason"] = "Pedido para revisão",
            ["__RequestVerificationToken"] = assistantToken.Groups[1].Value
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, (await _client.PostAsync("/Ajustes?handler=Submit", form)).StatusCode);
        }
        int requestId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            requestId = await db.InventoryAdjustmentRequests.Select(request => request.Id).SingleAsync();
        }

        _client.DefaultRequestHeaders.Remove("X-Test-Role");
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Gestor");
        _client.DefaultRequestHeaders.Remove("X-Test-User-Id");
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", "manager-2");
        var managerPage = await _client.GetAsync("/Ajustes");
        var managerHtml = await managerPage.Content.ReadAsStringAsync();
        var managerToken = Regex.Match(managerHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var rejectForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["RequestId"] = requestId.ToString(),
            ["__RequestVerificationToken"] = managerToken.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ajustes?handler=Reject", rejectForm);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = await verifyDb.InventoryAdjustmentRequests.SingleAsync();
        Assert.Equal(InventoryAdjustmentStatus.Rejected, request.Status);
        Assert.Equal("manager-2", request.DecidedByUserId);
        Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync());
        Assert.Contains(">5</td>", WebUtility.HtmlDecode(await _client.GetStringAsync("/Materiais")));
        _client.DefaultRequestHeaders.Remove("X-Test-Role");
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Assistente");
        _client.DefaultRequestHeaders.Remove("X-Test-User-Id");
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", "assistant-2");
        var requesterHistory = WebUtility.HtmlDecode(await _client.GetStringAsync("/Ajustes"));
        Assert.Contains("Rejeitado", requesterHistory);
        Assert.Contains("manager-2", requesterHistory);
    }

    [Fact]
    public async Task RequesterCannotApproveOwnAdjustment()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-SELF-001", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "5", "NF-ADJ-SELF-001")).StatusCode);
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Assistente");
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", "same-user");
        var assistantPage = await _client.GetAsync("/Ajustes");
        var assistantHtml = await assistantPage.Content.ReadAsStringAsync();
        var assistantToken = Regex.Match(assistantHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["DeltaQuantity"] = "-1",
            ["Reason"] = "Ajuste solicitado pelo próprio usuário",
            ["__RequestVerificationToken"] = assistantToken.Groups[1].Value
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, (await _client.PostAsync("/Ajustes?handler=Submit", form)).StatusCode);
        }
        int requestId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            requestId = await db.InventoryAdjustmentRequests.Select(request => request.Id).SingleAsync();
        }

        _client.DefaultRequestHeaders.Remove("X-Test-Role");
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Gestor");
        var managerPage = await _client.GetAsync("/Ajustes");
        var managerHtml = await managerPage.Content.ReadAsStringAsync();
        var managerToken = Regex.Match(managerHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var approvalForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["RequestId"] = requestId.ToString(),
            ["__RequestVerificationToken"] = managerToken.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ajustes?handler=Approve", approvalForm);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(InventoryAdjustmentStatus.Pending, (await verifyDb.InventoryAdjustmentRequests.SingleAsync()).Status);
        Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task ManagerCannotApproveAdjustmentThatWouldMakeBalanceNegative()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-NEGATIVE-001", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "1", "NF-ADJ-NEGATIVE-001")).StatusCode);
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Assistente");
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", "assistant-negative");
        var assistantPage = await _client.GetAsync("/Ajustes");
        var assistantHtml = await assistantPage.Content.ReadAsStringAsync();
        var assistantToken = Regex.Match(assistantHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["DeltaQuantity"] = "-2",
            ["Reason"] = "Perda superior ao saldo",
            ["__RequestVerificationToken"] = assistantToken.Groups[1].Value
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, (await _client.PostAsync("/Ajustes?handler=Submit", form)).StatusCode);
        }
        int requestId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            requestId = await db.InventoryAdjustmentRequests.Select(request => request.Id).SingleAsync();
        }

        _client.DefaultRequestHeaders.Remove("X-Test-Role");
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Gestor");
        _client.DefaultRequestHeaders.Remove("X-Test-User-Id");
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", "manager-negative");
        var managerPage = await _client.GetAsync("/Ajustes");
        var managerHtml = await managerPage.Content.ReadAsStringAsync();
        var managerToken = Regex.Match(managerHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var approvalForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["RequestId"] = requestId.ToString(),
            ["__RequestVerificationToken"] = managerToken.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ajustes?handler=Approve", approvalForm);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("deixaria o saldo abaixo de zero", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(InventoryAdjustmentStatus.Pending, (await verifyDb.InventoryAdjustmentRequests.SingleAsync()).Status);
        Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync());
        Assert.Contains(">1</td>", WebUtility.HtmlDecode(await _client.GetStringAsync("/Materiais")));
    }

    [Fact]
    public async Task AssistantCannotApproveAnotherUsersAdjustment()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-DENIED-001", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "2", "NF-ADJ-DENIED-001")).StatusCode);
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Assistente");
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", "requester-denied");
        var assistantPage = await _client.GetAsync("/Ajustes");
        var assistantHtml = await assistantPage.Content.ReadAsStringAsync();
        var assistantToken = Regex.Match(assistantHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["DeltaQuantity"] = "-1",
            ["Reason"] = "Pedido aguardando gestor",
            ["__RequestVerificationToken"] = assistantToken.Groups[1].Value
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, (await _client.PostAsync("/Ajustes?handler=Submit", form)).StatusCode);
        }
        int requestId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            requestId = await db.InventoryAdjustmentRequests.Select(request => request.Id).SingleAsync();
        }

        _client.DefaultRequestHeaders.Remove("X-Test-User-Id");
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", "operator-denied");
        var operatorPage = await _client.GetAsync("/Ajustes");
        var operatorHtml = await operatorPage.Content.ReadAsStringAsync();
        var operatorToken = Regex.Match(operatorHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var approvalForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["RequestId"] = requestId.ToString(),
            ["__RequestVerificationToken"] = operatorToken.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ajustes?handler=Approve", approvalForm);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(InventoryAdjustmentStatus.Pending, (await verifyDb.InventoryAdjustmentRequests.SingleAsync()).Status);
        Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task AssistantCannotRejectAnotherUsersAdjustment()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-REJECT-DENIED-001", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "2", "NF-ADJ-REJECT-DENIED-001")).StatusCode);
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Assistente");
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", "requester-reject-denied");
        var requesterPage = await _client.GetAsync("/Ajustes");
        var requesterHtml = await requesterPage.Content.ReadAsStringAsync();
        var requesterToken = Regex.Match(requesterHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["DeltaQuantity"] = "-1",
            ["Reason"] = "Pedido de ajuste",
            ["__RequestVerificationToken"] = requesterToken.Groups[1].Value
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, (await _client.PostAsync("/Ajustes?handler=Submit", form)).StatusCode);
        }
        int requestId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            requestId = await db.InventoryAdjustmentRequests.Select(request => request.Id).SingleAsync();
        }

        _client.DefaultRequestHeaders.Remove("X-Test-User-Id");
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", "operator-reject-denied");
        var operatorPage = await _client.GetAsync("/Ajustes");
        var operatorHtml = await operatorPage.Content.ReadAsStringAsync();
        var operatorToken = Regex.Match(operatorHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var rejectForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["RequestId"] = requestId.ToString(),
            ["__RequestVerificationToken"] = operatorToken.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ajustes?handler=Reject", rejectForm);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(InventoryAdjustmentStatus.Pending, (await verifyDb.InventoryAdjustmentRequests.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("Assistente")]
    [InlineData("Auxiliar")]
    public async Task AssistantAndAuxiliaryAdjustmentRequestsStayPendingWithoutChangingStock(string role)
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-001", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "5", "NF-ADJ-001")).StatusCode);

        _client.DefaultRequestHeaders.Add("X-Test-Role", role);
        var page = await _client.GetAsync("/Ajustes");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var pageHtml = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(pageHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The adjustments page should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["DeltaQuantity"] = "-2",
            ["Reason"] = "Ajuste por contagem física",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ajustes?handler=Submit", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var listedHtml = WebUtility.HtmlDecode(await _client.GetStringAsync("/Ajustes"));
        Assert.Contains("Aguardando aprovação", listedHtml);
        Assert.Contains("Ajuste por contagem física", listedHtml);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pendingCount = await verifyDb.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM InventoryAdjustmentRequests WHERE Reason = {0} AND Status = 'Pending'",
            "Ajuste por contagem física").SingleAsync();
        Assert.Equal(1, pendingCount);
        Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync());
        Assert.Contains(">5</td>", WebUtility.HtmlDecode(await _client.GetStringAsync("/Materiais")));
    }

    [Fact]
    public async Task OtherSectorMaterialCannotBeLoanedByPilotUser()
    {
        var (materialId, foreignSectorId) = await SeedMaterialInAnotherSectorAsync("OTHER-LOAN-001", 2m);

        var response = await SubmitLoanAsync(materialId, "Pessoa de outro setor", "1", "EM-OUTRO-SETOR");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("setor TI", html);
        Assert.DoesNotContain("OTHER-LOAN-001", html);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.InventoryLoans.ToListAsync());
        var movement = await verifyDb.InventoryMovements.SingleAsync();
        Assert.Equal(InventoryMovementType.Entry, movement.Type);
        Assert.Equal(foreignSectorId, movement.SectorId);
    }

    [Fact]
    public async Task AdjustmentFormRendersANewSubmissionKey()
    {
        var page = await _client.GetAsync("/Ajustes");
        var html = await page.Content.ReadAsStringAsync();
        var submissionKey = Regex.Match(html, @"name=""SubmissionId""[^>]*value=""([^""]+)""");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(submissionKey.Success, "The adjustment form should emit a submission idempotency key.");
        Assert.True(Guid.TryParse(submissionKey.Groups[1].Value, out var parsedKey));
        Assert.NotEqual(Guid.Empty, parsedKey);
    }

    [Fact]
    public async Task ReplayingPendingAdjustmentWithSameSubmissionKeyCreatesOneRequest()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-IDEM-001", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Assistente");
        var page = await _client.GetAsync("/Ajustes");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submissionKey = Regex.Match(html, @"name=""SubmissionId""[^>]*value=""([^""]+)""");
        Assert.True(token.Success);
        Assert.True(submissionKey.Success);

        async Task<HttpStatusCode> PostAdjustmentAsync()
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["MaterialId"] = materialId.ToString(),
                ["DeltaQuantity"] = "1",
                ["Reason"] = "Contagem física",
                ["SubmissionId"] = submissionKey.Groups[1].Value,
                ["__RequestVerificationToken"] = token.Groups[1].Value
            });
            using var response = await _client.PostAsync("/Ajustes?handler=Submit", form);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Redirect, await PostAdjustmentAsync());
        Assert.Equal(HttpStatusCode.Redirect, await PostAdjustmentAsync());

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = await verifyDb.InventoryAdjustmentRequests.SingleAsync(item => item.MaterialId == materialId);
        Assert.Equal(Guid.Parse(submissionKey.Groups[1].Value), request.SubmissionId);
        Assert.Matches("^[0-9A-F]{64}$", request.SubmissionFingerprint);
        Assert.Equal(0, await verifyDb.InventoryMovements.CountAsync(movement =>
            movement.MaterialId == materialId && movement.Type == InventoryMovementType.Adjustment));
    }

    [Fact]
    public async Task ReusingAdjustmentSubmissionKeyWithDifferentPayloadReturnsConflict()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-IDEM-002", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Assistente");
        var page = await _client.GetAsync("/Ajustes");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submissionKey = Regex.Match(html, @"name=""SubmissionId""[^>]*value=""([^""]+)""");
        Assert.True(token.Success);
        Assert.True(submissionKey.Success);

        async Task<HttpStatusCode> PostAdjustmentAsync(string reason)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["MaterialId"] = materialId.ToString(),
                ["DeltaQuantity"] = "1",
                ["Reason"] = reason,
                ["SubmissionId"] = submissionKey.Groups[1].Value,
                ["__RequestVerificationToken"] = token.Groups[1].Value
            });
            using var response = await _client.PostAsync("/Ajustes?handler=Submit", form);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Redirect, await PostAdjustmentAsync("Contagem física"));
        Assert.Equal(HttpStatusCode.Conflict, await PostAdjustmentAsync("Motivo diferente"));

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = await verifyDb.InventoryAdjustmentRequests.SingleAsync(item => item.MaterialId == materialId);
        Assert.Equal("Contagem física", request.Reason);
        Assert.Equal(1, await verifyDb.InventoryAdjustmentRequests.CountAsync(item => item.MaterialId == materialId));
    }

    [Fact]
    public async Task ReplayingManagerAdjustmentWithSameKeyCreatesOneLedgerMovement()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "ADJ-IDEM-003", "Keyboard");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "5", "NF-ADJ-IDEM")).StatusCode);
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Gestor");
        var page = await _client.GetAsync("/Ajustes");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submissionKey = Regex.Match(html, @"name=""SubmissionId""[^>]*value=""([^""]+)""");
        Assert.True(token.Success);
        Assert.True(submissionKey.Success);

        async Task<HttpStatusCode> PostAdjustmentAsync()
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["MaterialId"] = materialId.ToString(),
                ["DeltaQuantity"] = "1",
                ["Reason"] = "Ajuste de gestor",
                ["SubmissionId"] = submissionKey.Groups[1].Value,
                ["__RequestVerificationToken"] = token.Groups[1].Value
            });
            using var response = await _client.PostAsync("/Ajustes?handler=Submit", form);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Redirect, await PostAdjustmentAsync());
        Assert.Equal(HttpStatusCode.Redirect, await PostAdjustmentAsync());

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = await verifyDb.InventoryAdjustmentRequests.SingleAsync(item => item.MaterialId == materialId);
        var movement = await verifyDb.InventoryMovements.SingleAsync(item => item.Type == InventoryMovementType.Adjustment);
        Assert.Equal(Guid.Parse(submissionKey.Groups[1].Value), request.SubmissionId);
        Assert.Equal(request.SubmissionId, movement.SubmissionId);
        Assert.Equal(6m, await verifyDb.InventoryMovements
            .Where(item => item.MaterialId == materialId)
            .SumAsync(item => item.Quantity));
    }

    [Fact]
    public async Task OtherSectorMaterialCannotBeAdjustedByPilotManager()
    {
        var (materialId, foreignSectorId) = await SeedMaterialInAnotherSectorAsync("OTHER-ADJ-001", 2m);
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Gestor");
        var page = await _client.GetAsync("/Ajustes");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["DeltaQuantity"] = "1",
            ["Reason"] = "Tentativa em outro setor",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Ajustes?handler=Submit", form);
        var responseHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("setor TI", responseHtml);
        Assert.DoesNotContain("OTHER-ADJ-001", html);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.InventoryAdjustmentRequests.ToListAsync());
        var movement = await verifyDb.InventoryMovements.SingleAsync();
        Assert.Equal(InventoryMovementType.Entry, movement.Type);
        Assert.Equal(foreignSectorId, movement.SectorId);
    }

    [Fact]
    public async Task OtherSectorLoanCannotBeReturnedByPilotUser()
    {
        var (materialId, foreignSectorId) = await SeedMaterialInAnotherSectorAsync("OTHER-RETURN-001", 2m);
        int loanId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var loan = new InventoryLoan
            {
                MaterialId = materialId,
                SectorId = foreignSectorId,
                BorrowerName = "Outra pessoa",
                Quantity = 1m,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedByUserId = "foreign-user",
                CreatedByName = "Foreign User"
            };
            db.InventoryLoans.Add(loan);
            await db.SaveChangesAsync();
            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = materialId,
                SectorId = foreignSectorId,
                Type = InventoryMovementType.Loan,
                Quantity = 1m,
                InventoryLoanId = loan.Id,
                ActorUserId = "foreign-user",
                ActorName = "Foreign User",
                CreatedAtUtc = loan.CreatedAtUtc
            });
            await db.SaveChangesAsync();
            loanId = loan.Id;
        }
        var page = await _client.GetAsync("/Emprestimos");
        var pageHtml = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(pageHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["LoanId"] = loanId.ToString(),
            ["ReturnQuantity"] = "1",
            ["ReturnSubmissionId"] = Guid.NewGuid().ToString(),
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Emprestimos?handler=Return", form);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.InventoryMovements.Where(item => item.Type == InventoryMovementType.Return).ToListAsync());
    }

    [Fact]
    public async Task OtherSectorAdjustmentCannotBeApprovedByPilotManager()
    {
        var (materialId, sectorId) = await SeedMaterialInAnotherSectorAsync("OTHER-APPROVAL-001", 2m);
        var requestId = await SeedPendingAdjustmentAsync(materialId, sectorId, "foreign-requester");
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Gestor");
        var page = await _client.GetAsync("/Ajustes");
        var html = await page.Content.ReadAsStringAsync();
        Assert.DoesNotContain("OTHER-APPROVAL-001", html);
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["RequestId"] = requestId.ToString(),
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });

        var response = await _client.PostAsync("/Ajustes?handler=Approve", form);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(InventoryAdjustmentStatus.Pending, (await verifyDb.InventoryAdjustmentRequests.SingleAsync()).Status);
        Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task OtherSectorAdjustmentCannotBeRejectedByPilotManager()
    {
        var (materialId, sectorId) = await SeedMaterialInAnotherSectorAsync("OTHER-REJECT-001", 2m);
        var requestId = await SeedPendingAdjustmentAsync(materialId, sectorId, "foreign-requester");
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Gestor");
        var page = await _client.GetAsync("/Ajustes");
        var html = await page.Content.ReadAsStringAsync();
        Assert.DoesNotContain("OTHER-REJECT-001", html);
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["RequestId"] = requestId.ToString(),
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });

        var response = await _client.PostAsync("/Ajustes?handler=Reject", form);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(InventoryAdjustmentStatus.Pending, (await verifyDb.InventoryAdjustmentRequests.SingleAsync()).Status);
        Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task QuantityLoanFormRendersANewSubmissionKey()
    {
        var page = await _client.GetAsync("/Emprestimos");
        var html = await page.Content.ReadAsStringAsync();
        var submissionKey = Regex.Match(html, @"name=""LoanSubmissionId""[^>]*value=""([^""]+)""");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(submissionKey.Success, "The loan form should emit a submission idempotency key.");
        Assert.True(Guid.TryParse(submissionKey.Groups[1].Value, out var parsedKey));
        Assert.NotEqual(Guid.Empty, parsedKey);
    }

    [Fact]
    public async Task ReplayingQuantityLoanWithSameKeyAndPayloadCreatesOneLoanAndMovement()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "LOAN-IDEM-001", "Notebook");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "5", "NF-LOAN-IDEM")).StatusCode);

        var page = await _client.GetAsync("/Emprestimos");
        var html = await page.Content.ReadAsStringAsync();
        var submissionKey = Regex.Match(html, @"name=""LoanSubmissionId""[^>]*value=""([^""]+)""");
        Assert.True(submissionKey.Success, "The loan form should emit a submission key.");
        var key = submissionKey.Groups[1].Value;

        Assert.Equal(HttpStatusCode.Redirect,
            (await SubmitLoanAsync(materialId, "Ana Silva", "2", "EM-LOAN-IDEM", key)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,
            (await SubmitLoanAsync(materialId, "Ana Silva", "2", "EM-LOAN-IDEM", key)).StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(await verifyDb.InventoryLoans.ToListAsync());
        var loanMovement = await verifyDb.InventoryMovements.SingleAsync(movement => movement.Type == InventoryMovementType.Loan);
        Assert.Equal(Guid.Parse(key), loanMovement.SubmissionId);
        Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync(movement => movement.Type == InventoryMovementType.Loan));
        Assert.Equal(3m, await verifyDb.InventoryMovements
            .Where(movement => movement.MaterialId == materialId)
            .Select(movement => movement.Type == InventoryMovementType.Entry || movement.Type == InventoryMovementType.Return
                ? movement.Quantity
                : movement.Type == InventoryMovementType.Consumption || movement.Type == InventoryMovementType.Loan
                    ? -movement.Quantity
                    : movement.Quantity)
            .SumAsync());
    }

    [Fact]
    public async Task QuantityLoanRouteAcceptsAValidSubmission()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "LOAN-001", "Notebook");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "5", "NF-LOAN-001")).StatusCode);

        var loansPage = await _client.GetAsync("/Emprestimos");
        Assert.Equal(HttpStatusCode.OK, loansPage.StatusCode);
        var loansPageHtml = await loansPage.Content.ReadAsStringAsync();

        var token = Regex.Match(loansPageHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submissionKey = Regex.Match(loansPageHtml, @"name=""LoanSubmissionId""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The loan page should emit an antiforgery token.");
        Assert.True(submissionKey.Success, "The loan form should emit a submission key.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["BorrowerName"] = "Ana Silva",
            ["Quantity"] = "2",
            ["Reference"] = "EM-2026-001",
            ["LoanSubmissionId"] = submissionKey.Groups[1].Value,
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });

        var response = await _client.PostAsync("/Emprestimos?handler=Loan", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var listed = await _client.GetAsync("/Emprestimos");
        var listedHtml = WebUtility.HtmlDecode(await listed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Contains("Ana Silva", listedHtml);
        Assert.Contains("EM-2026-001", listedHtml);
        Assert.Contains("2", listedHtml);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var loan = await verifyDb.InventoryLoans.SingleAsync();
        Assert.Equal("Ana Silva", loan.BorrowerName);
        Assert.Equal(2m, loan.Quantity);
        Assert.Equal("EM-2026-001", loan.Reference);
        var loanMovement = await verifyDb.InventoryMovements.SingleAsync(movement => movement.InventoryLoanId == loan.Id);
        Assert.Equal(InventoryMovementType.Loan, loanMovement.Type);
        Assert.Equal(2m, loanMovement.Quantity);
        Assert.Equal("pilot-user", loanMovement.ActorUserId);
        Assert.Equal(3m, await verifyDb.InventoryMovements
            .Where(movement => movement.MaterialId == materialId)
            .Select(movement => movement.Type == InventoryMovementType.Entry || movement.Type == InventoryMovementType.Return
                ? movement.Quantity
                : movement.Type == InventoryMovementType.Consumption || movement.Type == InventoryMovementType.Loan
                    ? -movement.Quantity
                    : movement.Quantity)
            .SumAsync());
    }

    [Fact]
    public async Task PartialLoanReturnIsAcceptedAndKeepsTheRemainingAmountOpen()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "RET-001", "Notebook");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "5", "NF-RET-001")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitLoanAsync(materialId, "Ana Silva", "2", "EM-RET-001")).StatusCode);

        int loanId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            loanId = await db.InventoryLoans.Select(loan => loan.Id).SingleAsync();
        }
        var returnPage = await _client.GetAsync("/Emprestimos");
        var returnHtml = await returnPage.Content.ReadAsStringAsync();
        var token = Regex.Match(returnHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var returnSubmissionKey = Regex.Match(returnHtml,
            @"name=""ReturnSubmissionId""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The loan page should emit an antiforgery token.");
        Assert.True(returnSubmissionKey.Success, "The loan return form should emit a submission key.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["LoanId"] = loanId.ToString(),
            ["ReturnQuantity"] = "1",
            ["ReturnSubmissionId"] = returnSubmissionKey.Groups[1].Value,
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });

        var response = await _client.PostAsync("/Emprestimos?handler=Return", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var listing = WebUtility.HtmlDecode(await _client.GetStringAsync("/Emprestimos"));
        Assert.Contains("Ana Silva", listing);
        Assert.Contains("1", listing);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var returnMovement = await verifyDb.InventoryMovements.SingleAsync(movement =>
            movement.InventoryLoanId == loanId && movement.Type == InventoryMovementType.Return);
        Assert.Equal(1m, returnMovement.Quantity);
        Assert.Equal(1m, (await verifyDb.InventoryLoans.SingleAsync(loan => loan.Id == loanId)).Quantity
            - await verifyDb.InventoryMovements
                .Where(movement => movement.InventoryLoanId == loanId && movement.Type == InventoryMovementType.Return)
                .SumAsync(movement => movement.Quantity));
    }

    [Fact]
    public async Task ReplayingPartialLoanReturnWithSameKeyAndPayloadRestoresStockOnlyOnce()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "RET-IDEM-001", "Notebook");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "5", "NF-RET-IDEM")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitLoanAsync(materialId, "Ana Silva", "2", "EM-RET-IDEM")).StatusCode);

        int loanId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            loanId = await db.InventoryLoans.Select(loan => loan.Id).SingleAsync();
        }
        var page = await _client.GetAsync("/Emprestimos");
        var html = await page.Content.ReadAsStringAsync();
        var submissionKey = Regex.Match(html, @"name=""ReturnSubmissionId""[^>]*value=""([^""]+)""");
        Assert.True(submissionKey.Success, "The loan return form should emit a submission key.");
        var key = submissionKey.Groups[1].Value;

        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReturnAsync(loanId, "1", key)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReturnAsync(loanId, "1", key)).StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var returnMovement = await verifyDb.InventoryMovements.SingleAsync(movement =>
            movement.InventoryLoanId == loanId && movement.Type == InventoryMovementType.Return);
        Assert.Equal(Guid.Parse(key), returnMovement.SubmissionId);
        Assert.Equal(1m, returnMovement.Quantity);
        Assert.Equal(1m, (await verifyDb.InventoryLoans.SingleAsync(loan => loan.Id == loanId)).Quantity
            - await verifyDb.InventoryMovements
                .Where(movement => movement.InventoryLoanId == loanId && movement.Type == InventoryMovementType.Return)
                .SumAsync(movement => movement.Quantity));
        Assert.Equal(4m, await verifyDb.InventoryMovements
            .Where(movement => movement.MaterialId == materialId)
            .Select(movement => movement.Type == InventoryMovementType.Entry || movement.Type == InventoryMovementType.Return
                ? movement.Quantity
                : movement.Type == InventoryMovementType.Consumption || movement.Type == InventoryMovementType.Loan
                    ? -movement.Quantity
                    : movement.Quantity)
            .SumAsync());
    }

    [Fact]
    public async Task FullLoanReturnClosesTheLoanAndRestoresStock()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "RET-002", "Monitor");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "2", "NF-RET-002")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitLoanAsync(materialId, "Carlos Lima", "2", "EM-RET-002")).StatusCode);

        int loanId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            loanId = await db.InventoryLoans.Select(loan => loan.Id).SingleAsync();
        }

        var response = await SubmitReturnAsync(loanId, "2");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var listing = WebUtility.HtmlDecode(await _client.GetStringAsync("/Emprestimos"));
        Assert.DoesNotContain("Carlos Lima", listing);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(2m, await verifyDb.InventoryMovements
            .Where(movement => movement.InventoryLoanId == loanId && movement.Type == InventoryMovementType.Return)
            .SumAsync(movement => movement.Quantity));
        var balance = await verifyDb.InventoryMovements
            .Where(movement => movement.MaterialId == materialId)
            .Select(movement => movement.Type == InventoryMovementType.Entry || movement.Type == InventoryMovementType.Return
                ? movement.Quantity
                : movement.Type == InventoryMovementType.Consumption || movement.Type == InventoryMovementType.Loan
                    ? -movement.Quantity
                    : movement.Quantity)
            .SumAsync();
        Assert.Equal(2m, balance);
    }

    [Fact]
    public async Task ReturnQuantityCannotExceedTheOpenLoan()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "RET-003", "Headset");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "2", "NF-RET-003")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitLoanAsync(materialId, "Rita Alves", "2", "EM-RET-003")).StatusCode);
        int loanId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            loanId = await db.InventoryLoans.Select(loan => loan.Id).SingleAsync();
        }

        var response = await SubmitReturnAsync(loanId, "3");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Devolução excede o saldo emprestado", html);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.InventoryMovements.Where(movement =>
            movement.InventoryLoanId == loanId && movement.Type == InventoryMovementType.Return).ToListAsync());
        Assert.Contains("Rita Alves", WebUtility.HtmlDecode(await _client.GetStringAsync("/Emprestimos")));
    }

    [Fact]
    public async Task NegativeLoanReturnIsRejectedWithoutChangingStockOrLoanBalance()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "RET-NEG-001", "Adaptador");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "5", "NF-RET-NEG-001")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitLoanAsync(materialId, "Rita Alves", "2", "EM-RET-NEG-001")).StatusCode);
        int loanId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            loanId = await db.InventoryLoans.Select(loan => loan.Id).SingleAsync();
        }

        var response = await SubmitReturnAsync(loanId, "-1");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("A quantidade devolvida deve ser maior que zero.", html);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.InventoryMovements.Where(movement =>
            movement.InventoryLoanId == loanId && movement.Type == InventoryMovementType.Return).ToListAsync());
        Assert.Equal(2m, (await verifyDb.InventoryLoans.SingleAsync(loan => loan.Id == loanId)).Quantity);
        Assert.Equal(3m, await verifyDb.InventoryMovements
            .Where(movement => movement.MaterialId == materialId)
            .Select(movement => movement.Type == InventoryMovementType.Entry || movement.Type == InventoryMovementType.Return
                ? movement.Quantity
                : movement.Type == InventoryMovementType.Consumption || movement.Type == InventoryMovementType.Loan
                    ? -movement.Quantity
                    : movement.Quantity)
            .SumAsync());
    }

    [Fact]
    public async Task ConsumptionFormRendersANewSubmissionKey()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "IDEMP-CONSUMPTION-001", "Consumo idempotente");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect,
            (await SubmitReceiptAsync(materialId, "10", "NF-CONSUMPTION-001")).StatusCode);

        var page = await _client.GetAsync("/Movimentacoes/Saida");
        var html = await page.Content.ReadAsStringAsync();
        var submissionKey = Regex.Match(html,
            @"name=""SubmissionId""[^>]*value=""([^""]+)""");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(submissionKey.Success, "The consumption form should include its idempotency key.");
        Assert.True(Guid.TryParse(submissionKey.Groups[1].Value, out var parsedKey));
        Assert.NotEqual(Guid.Empty, parsedKey);
    }

    [Fact]
    public async Task ReplayingConsumptionWithSameSubmissionKeyAndPayloadCreatesOnlyOneMovement()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "IDEMP-CONSUMPTION-REPLAY-001", "Consumo repetido");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect,
            (await SubmitReceiptAsync(materialId, "10", "NF-CONSUMPTION-REPLAY-001")).StatusCode);

        var page = await _client.GetAsync("/Movimentacoes/Saida");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submissionKey = Regex.Match(html, @"name=""SubmissionId""[^>]*value=""([^""]+)""");
        Assert.True(token.Success);
        Assert.True(submissionKey.Success);
        FormUrlEncodedContent CreateForm() => new(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Quantity"] = "2",
            ["Reference"] = "BAIXA-CONSUMPTION-REPLAY-001",
            ["SubmissionId"] = submissionKey.Groups[1].Value,
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        using var firstForm = CreateForm();
        using var replayForm = CreateForm();

        var firstResponse = await _client.PostAsync("/Movimentacoes/Saida", firstForm);
        var replayResponse = await _client.PostAsync("/Movimentacoes/Saida", replayForm);

        Assert.Equal(HttpStatusCode.Redirect, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, replayResponse.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync(movement =>
            movement.MaterialId == materialId && movement.Type == InventoryMovementType.Consumption));
        Assert.Equal(8m, await verifyDb.InventoryMovements
            .Where(movement => movement.MaterialId == materialId)
            .SumAsync(movement => movement.Type == InventoryMovementType.Entry ? movement.Quantity : -movement.Quantity));
    }

    [Fact]
    public async Task ConsumptionCannotExceedAvailableStock()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "OUT-001", "Mouse sem fio");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "5", "NF-OUT-001")).StatusCode);
        var response = await SubmitConsumptionAsync(materialId, "6", "Consumo maior que saldo");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Saldo insuficiente", html);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task ConsumptionPersistsDebitAndUpdatesCatalogBalance()
    {
        var categoryId = await SeedCategoryAsync();
        var (created, _) = await SubmitMaterialAsync(categoryId, "OUT-002", "Teclado compacto");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        int materialId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            materialId = await db.Materials.Select(material => material.Id).SingleAsync();
        }

        Assert.Equal(HttpStatusCode.Redirect, (await SubmitReceiptAsync(materialId, "5", "NF-OUT-002")).StatusCode);
        var response = await SubmitConsumptionAsync(materialId, "3", "Reposição de usuário");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var debitQuantity = await verifyDb.Database.SqlQuery<decimal>(
            $"SELECT Quantity AS Value FROM InventoryMovements WHERE MaterialId = {materialId} AND Type = 'Consumption'").SingleAsync();
        Assert.Equal(3m, debitQuantity);
        var catalog = await _client.GetAsync("/Materiais");
        Assert.Contains(">2</td>", WebUtility.HtmlDecode(await catalog.Content.ReadAsStringAsync()));
    }

    private async Task<HttpResponseMessage> SubmitConsumptionAsync(int materialId, string quantity, string reference)
    {
        var page = await _client.GetAsync("/Movimentacoes/Saida");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submissionKey = Regex.Match(html, @"name=""SubmissionId""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Quantity"] = quantity,
            ["Reference"] = reference,
            ["SubmissionId"] = submissionKey.Success ? submissionKey.Groups[1].Value : Guid.NewGuid().ToString(),
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        return await _client.PostAsync("/Movimentacoes/Saida", form);
    }

    private async Task<HttpResponseMessage> SubmitLoanAsync(
        int materialId,
        string borrowerName,
        string quantity,
        string reference,
        string? submissionId = null)
    {
        var page = await _client.GetAsync("/Emprestimos");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submissionKey = Regex.Match(html, @"name=""LoanSubmissionId""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The loan page should emit an antiforgery token.");
        Assert.True(submissionKey.Success, "The loan form should emit a submission key.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["BorrowerName"] = borrowerName,
            ["Quantity"] = quantity,
            ["Reference"] = reference,
            ["LoanSubmissionId"] = submissionId ?? submissionKey.Groups[1].Value,
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        return await _client.PostAsync("/Emprestimos?handler=Loan", form);
    }

    private async Task<HttpResponseMessage> SubmitReturnAsync(int loanId, string quantity, string? submissionId = null)
    {
        var page = await _client.GetAsync("/Emprestimos");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submissionKey = Regex.Match(html, @"name=""ReturnSubmissionId""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The loan page should emit an antiforgery token.");
        Assert.True(submissionId is not null || submissionKey.Success, "The return form should emit a submission key.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["LoanId"] = loanId.ToString(),
            ["ReturnQuantity"] = quantity,
            ["ReturnSubmissionId"] = submissionId ?? submissionKey.Groups[1].Value,
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        return await _client.PostAsync("/Emprestimos?handler=Return", form);
    }

    private async Task<HttpResponseMessage> SubmitReceiptAsync(int materialId, string quantity, string reference)
    {
        var page = await _client.GetAsync("/Movimentacoes/Entrada");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var submissionKey = Regex.Match(html, @"name=""SubmissionId""[^>]*value=""([^""]+)""");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Quantity"] = quantity,
            ["Reference"] = reference,
            ["SubmissionId"] = submissionKey.Success ? submissionKey.Groups[1].Value : Guid.NewGuid().ToString(),
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        return await _client.PostAsync("/Movimentacoes/Entrada", form);
    }

    private async Task<int> SeedPendingAdjustmentAsync(int materialId, int sectorId, string requestedByUserId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = new InventoryAdjustmentRequest
        {
            MaterialId = materialId,
            SectorId = sectorId,
            DeltaQuantity = -1m,
            Reason = "Solicitação de outro setor",
            Status = InventoryAdjustmentStatus.Pending,
            RequestedByUserId = requestedByUserId,
            RequestedByName = "Solicitante externo",
            RequestedAtUtc = DateTimeOffset.UtcNow
        };
        db.InventoryAdjustmentRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private async Task<(int MaterialId, int SectorId)> SeedMaterialInAnotherSectorAsync(string code, decimal stock)
    {
        await _client.GetAsync("/Materiais");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sector = new Sector { Name = "Outro Setor" };
        db.Sectors.Add(sector);
        await db.SaveChangesAsync();
        var category = new MaterialCategory
        {
            Name = "Materiais Gerais",
            NormalizedName = "MATERIAIS GERAIS",
            SectorId = sector.Id
        };
        db.MaterialCategories.Add(category);
        await db.SaveChangesAsync();
        var material = new Material
        {
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = "Material de outro setor",
            Unit = "unidade",
            TrackingMode = MaterialTrackingMode.Quantity,
            MinimumStock = 0m,
            CategoryId = category.Id,
            SectorId = sector.Id
        };
        db.Materials.Add(material);
        await db.SaveChangesAsync();
        if (stock > 0)
        {
            db.InventoryMovements.Add(new InventoryMovement
            {
                MaterialId = material.Id,
                SectorId = sector.Id,
                Type = InventoryMovementType.Entry,
                Quantity = stock,
                ActorUserId = "foreign-user",
                ActorName = "Foreign User",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        return (material.Id, sector.Id);
    }

    private async Task<int> SeedCategoryAsync()
    {
        await _client.GetAsync("/Categorias");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var category = new MaterialCategory
        {
            Name = "Periféricos",
            NormalizedName = "PERIFÉRICOS",
            SectorId = 1
        };
        db.MaterialCategories.Add(category);
        await db.SaveChangesAsync();
        return category.Id;
    }

    private async Task<(HttpResponseMessage Response, string Html)> SubmitMaterialAsync(
        int categoryId,
        string code,
        string name,
        string trackingMode = "Quantity")
    {
        var page = await _client.GetAsync("/Materiais");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The materials page should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = code,
            ["Name"] = name,
            ["Unit"] = "unidade",
            ["CategoryId"] = categoryId.ToString(),
            ["TrackingMode"] = trackingMode,
            ["MinimumStock"] = "5",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Materiais?handler=Create", form);
        var responseHtml = await response.Content.ReadAsStringAsync();
        return (response, responseHtml);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, Context.Request.Headers["X-Test-User-Id"].FirstOrDefault() ?? "pilot-user"),
                new Claim(ClaimTypes.Name, Context.Request.Headers["X-Test-User-Id"].FirstOrDefault() ?? "pilot-user"),
                new Claim(ClaimTypes.Role, Context.Request.Headers["X-Test-Role"].FirstOrDefault() ?? "Administrador")
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
