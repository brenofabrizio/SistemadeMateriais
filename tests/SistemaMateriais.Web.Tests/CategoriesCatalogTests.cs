using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Tests;

public sealed class CategoriesCatalogTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _databasePath = null!;

    public Task InitializeAsync()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var hermesScratch = Path.Combine(localData, "hermes", "cache", "scratch");
        var scratch = Directory.Exists(hermesScratch) ? hermesScratch : Path.GetTempPath();
        _databasePath = Path.Combine(scratch, $"materiais-category-test-{Guid.NewGuid():N}.db");
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
    public async Task CategoriesCatalogPageIsAvailableToAnAuthenticatedPilotUser()
    {
        var response = await _client.GetAsync("/Categorias");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Categorias de materiais", html);
    }

    [Fact]
    public async Task CreatingCategoryPersistsItInThePilotSector()
    {
        var page = await _client.GetAsync("/Categorias");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The category form should emit an antiforgery token.");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CategoryName"] = "Periféricos",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Categorias?handler=Create", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var category = await db.MaterialCategories.SingleAsync(item => item.Name == "Periféricos");
        Assert.Equal(1, category.SectorId);
        Assert.Equal("PERIFÉRICOS", category.NormalizedName);
    }

    [Fact]
    public async Task CreatingCategoryWithoutAntiforgeryTokenIsRejectedWithoutPersistence()
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CategoryName"] = "Sem token antiforgery"
        });

        var response = await _client.PostAsync("/Categorias?handler=Create", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.MaterialCategories.ToListAsync());
    }

    [Fact]
    public async Task CreatingCategoryWithAnEmptyNameReturnsValidationInsteadOfFailing()
    {
        var page = await _client.GetAsync("/Categorias");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The category form should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CategoryName"] = string.Empty,
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });

        var response = await _client.PostAsync("/Categorias?handler=Create", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Informe o nome da categoria", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.MaterialCategories.ToListAsync());
    }

    [Fact]
    public async Task EditingCategoryUpdatesItsPersistedName()
    {
        await SubmitCategoryAsync("Periféricos");
        int categoryId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            categoryId = await db.MaterialCategories.Select(item => item.Id).SingleAsync();
        }

        var page = await _client.GetAsync("/Categorias");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Editar Periféricos", WebUtility.HtmlDecode(html));
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The category form should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["EditId"] = categoryId.ToString(),
            ["EditName"] = "Acessórios",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Categorias?handler=Edit", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var category = await verifyDb.MaterialCategories.SingleAsync(item => item.Id == categoryId);
        Assert.Equal("Acessórios", category.Name);
        Assert.Equal("ACESSÓRIOS", category.NormalizedName);
    }

    [Fact]
    public async Task EditingCategoryWithAnEmptyNameReturnsValidationInsteadOfFailing()
    {
        await SubmitCategoryAsync("Periféricos");
        int categoryId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            categoryId = await db.MaterialCategories.Select(item => item.Id).SingleAsync();
        }

        var page = await _client.GetAsync("/Categorias");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The category form should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["EditId"] = categoryId.ToString(),
            ["EditName"] = string.Empty,
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });

        var response = await _client.PostAsync("/Categorias?handler=Edit", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Informe o nome da categoria", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("Periféricos", (await verifyDb.MaterialCategories.SingleAsync(item => item.Id == categoryId)).Name);
    }

    [Fact]
    public async Task DeactivatingCategoryRetainsTheRecordAndMarksItInactive()
    {
        await SubmitCategoryAsync("Periféricos");
        int categoryId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            categoryId = await db.MaterialCategories.Select(item => item.Id).SingleAsync();
        }

        var page = await _client.GetAsync("/Categorias");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The category form should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["DeactivateId"] = categoryId.ToString(),
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Categorias?handler=Deactivate", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var category = await verifyDb.MaterialCategories.SingleAsync(item => item.Id == categoryId);
        Assert.False(category.IsActive);
        Assert.Contains("Periféricos", await verifyDb.MaterialCategories.Select(item => item.Name).ToListAsync());
    }

    [Fact]
    public async Task ActivatingInactiveCategoryRestoresItToActiveState()
    {
        await SubmitCategoryAsync("Periféricos");
        int categoryId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var category = await db.MaterialCategories.SingleAsync();
            category.IsActive = false;
            await db.SaveChangesAsync();
            categoryId = category.Id;
        }

        var page = await _client.GetAsync("/Categorias");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The category form should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ActivateId"] = categoryId.ToString(),
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Categorias?handler=Activate", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True((await verifyDb.MaterialCategories.SingleAsync(item => item.Id == categoryId)).IsActive);
    }

    [Fact]
    public async Task DuplicateCategoryNameIgnoringCaseIsRejectedWithoutAddingARow()
    {
        await SubmitCategoryAsync("Periféricos");
        var (response, html) = await SubmitCategoryAsync("PERIFÉRICOS");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Já existe uma categoria com esse nome neste setor.", WebUtility.HtmlDecode(html));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.MaterialCategories.CountAsync());
    }

    [Fact]
    public async Task AssistantCannotCreateCategories()
    {
        _client.DefaultRequestHeaders.Add("X-Test-Role", "Assistente");
        var (response, _) = await SubmitCategoryAsync("Periféricos");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.MaterialCategories.ToListAsync());
    }

    private async Task<(HttpResponseMessage Response, string Html)> SubmitCategoryAsync(string name)
    {
        var page = await _client.GetAsync("/Categorias");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The category form should emit an antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CategoryName"] = name,
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        var response = await _client.PostAsync("/Categorias?handler=Create", form);
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
                new Claim(ClaimTypes.NameIdentifier, "pilot-user"),
                new Claim(ClaimTypes.Name, "pilot-user"),
                new Claim(ClaimTypes.Role, Context.Request.Headers["X-Test-Role"].FirstOrDefault() ?? "Administrador")
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
