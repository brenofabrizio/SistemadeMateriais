using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SistemaMateriais.Web.Data;

namespace SistemaMateriais.Web.Tests;

[Collection("PostgreSQL integration")]
public sealed class PostgreSqlAssetRegistrationConcurrencyTests
{
    [PostgreSqlFact]
    public async Task ConcurrentRegistrationOfNormalizedIdentifierDoesNotCreateDuplicateRows()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSql");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        PostgreSqlIntegrationSupport.AssertDisposableLoopbackDatabase(connectionString!);

        var lookupBarrier = new SubmissionLookupBarrierInterceptor(commandText =>
            commandText.Contains("\"SubmissionId\" =", StringComparison.Ordinal));
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
                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseNpgsql(connectionString).AddInterceptors(lookupBarrier));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "PostgreSqlRegistrationTest";
                    options.DefaultChallengeScheme = "PostgreSqlRegistrationTest";
                }).AddScheme<AuthenticationSchemeOptions, PostgreSqlTestAuthenticationHandler>(
                    "PostgreSqlRegistrationTest", _ => { });
            });
        });
        var materialId = 0;
        var categoryId = 0;
        try
        {
            using var firstClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var secondClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
                Assert.Equal(connectionString, db.Database.GetConnectionString());
                var unique = Guid.NewGuid().ToString("N");
                var category = new MaterialCategory
                {
                    Name = $"RegistrationRace-{unique}",
                    NormalizedName = $"REGISTRATIONRACE-{unique}".ToUpperInvariant(),
                    SectorId = 1
                };
                db.MaterialCategories.Add(category);
                await db.SaveChangesAsync();
                categoryId = category.Id;
                var material = new Material
                {
                    Code = $"PGR-{unique}",
                    NormalizedCode = $"PGR-{unique}".ToUpperInvariant(),
                    Name = "Ativo para teste de cadastro concorrente",
                    Unit = "unidade",
                    TrackingMode = MaterialTrackingMode.IndividualAsset,
                    MinimumStock = 0m,
                    CategoryId = category.Id,
                    SectorId = 1
                };
                db.Materials.Add(material);
                await db.SaveChangesAsync();
                materialId = material.Id;
            }

            async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
            {
                using var page = await client.GetAsync("/Ativos");
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                var html = await page.Content.ReadAsStringAsync();
                var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
                Assert.True(token.Success, "The assets page should emit an antiforgery token.");
                return token.Groups[1].Value;
            }

            for (var attempt = 0; attempt < 5; attempt++)
            {
                lookupBarrier.Reset();
                var identifier = $"PG-DUP-{Guid.NewGuid():N}";
                var normalizedIdentifier = identifier.ToUpperInvariant();
                var firstTokenTask = GetAntiforgeryTokenAsync(firstClient);
                var secondTokenTask = GetAntiforgeryTokenAsync(secondClient);
                await Task.WhenAll(firstTokenTask, secondTokenTask);
                using var firstForm = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["MaterialId"] = materialId.ToString(),
                    ["Identifier"] = identifier,
                    ["Reference"] = $"RECEIPT-A-{attempt}",
                    ["__RequestVerificationToken"] = firstTokenTask.Result
                });
                using var secondForm = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["MaterialId"] = materialId.ToString(),
                    ["Identifier"] = identifier.ToLowerInvariant(),
                    ["Reference"] = $"RECEIPT-B-{attempt}",
                    ["__RequestVerificationToken"] = secondTokenTask.Result
                });
                var responses = await Task.WhenAll(
                    firstClient.PostAsync("/Ativos?handler=Register", firstForm),
                    secondClient.PostAsync("/Ativos?handler=Register", secondForm));
                Assert.True(lookupBarrier.ObservedSubmissionLookups >= 2,
                    "Both registration requests should complete the initial keyed lookup before writing.");

                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Redirect);
                var duplicateResponse = Assert.Single(responses, response => response.StatusCode != HttpStatusCode.Redirect);
                Assert.True(duplicateResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                    $"Expected a validation/conflict response, received {(int)duplicateResponse.StatusCode}.");
                var body = System.Net.WebUtility.HtmlDecode(await duplicateResponse.Content.ReadAsStringAsync());
                Assert.Contains("Já existe um ativo com esse número de série ou patrimônio neste setor.", body);
                foreach (var response in responses)
                {
                    response.Dispose();
                }

                await using var verifyScope = factory.Services.CreateAsyncScope();
                var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var asset = await verifyDb.InventoryAssets.SingleAsync(item =>
                    item.SectorId == 1 && item.NormalizedIdentifier == normalizedIdentifier);
                Assert.Equal(InventoryAssetStatus.Available, asset.Status);
                Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync(movement =>
                    movement.InventoryAssetId == asset.Id && movement.Type == InventoryMovementType.Entry));
            }

            lookupBarrier.Reset();
            var replayIdentifier = $"PG-REPLAY-{Guid.NewGuid():N}";
            var replaySubmissionId = Guid.NewGuid();
            var replayTokens = await Task.WhenAll(
                GetAntiforgeryTokenAsync(firstClient),
                GetAntiforgeryTokenAsync(secondClient));
            using var replayFirstForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["MaterialId"] = materialId.ToString(),
                ["Identifier"] = replayIdentifier,
                ["Reference"] = "RECEIPT-REPLAY",
                ["AssetRegistrationSubmissionId"] = replaySubmissionId.ToString(),
                ["__RequestVerificationToken"] = replayTokens[0]
            });
            using var replaySecondForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["MaterialId"] = materialId.ToString(),
                ["Identifier"] = replayIdentifier,
                ["Reference"] = "RECEIPT-REPLAY",
                ["AssetRegistrationSubmissionId"] = replaySubmissionId.ToString(),
                ["__RequestVerificationToken"] = replayTokens[1]
            });
            var replayResponses = await Task.WhenAll(
                firstClient.PostAsync("/Ativos?handler=Register", replayFirstForm),
                secondClient.PostAsync("/Ativos?handler=Register", replaySecondForm));
            try
            {
                Assert.All(replayResponses, response => Assert.Equal(HttpStatusCode.Redirect, response.StatusCode));
                Assert.True(lookupBarrier.ObservedSubmissionLookups >= 2,
                    "Both replay requests should pass the initial idempotency lookup before either writes.");
            }
            finally
            {
                foreach (var response in replayResponses)
                {
                    response.Dispose();
                }
            }

            await using var replayVerifyScope = factory.Services.CreateAsyncScope();
            var replayVerifyDb = replayVerifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var replayAsset = await replayVerifyDb.InventoryAssets.SingleAsync(item =>
                item.SectorId == 1 && item.NormalizedIdentifier == replayIdentifier.ToUpperInvariant());
            Assert.Equal(1, await replayVerifyDb.InventoryMovements.CountAsync(movement =>
                movement.InventoryAssetId == replayAsset.Id
                && movement.SubmissionId == replaySubmissionId
                && movement.SubmissionFingerprint != null));
        }
        finally
        {
            await PostgreSqlIntegrationSupport.DeleteMaterialScenarioAsync(factory.Services, materialId, categoryId);
        }
    }
}
