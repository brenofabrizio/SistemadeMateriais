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
using SistemaMateriais.Web.Data;
using Xunit;

namespace SistemaMateriais.Web.Tests;

[Collection("PostgreSQL integration")]
public sealed class PostgreSqlAdjustmentConcurrencyTests
{
    [PostgreSqlFact]
    public async Task ConcurrentApprovalAppliesPendingAdjustmentOnlyOnce()
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
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "PostgreSqlAdjustmentTest";
                    options.DefaultChallengeScheme = "PostgreSqlAdjustmentTest";
                }).AddScheme<AuthenticationSchemeOptions, PostgreSqlTestAuthenticationHandler>(
                    "PostgreSqlAdjustmentTest", _ => { });
            });
        });

        var materialId = 0;
        var categoryId = 0;
        try
        {
            using var firstClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var secondClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            int requestId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
                Assert.Equal(connectionString, db.Database.GetConnectionString());
                var unique = Guid.NewGuid().ToString("N");
                var category = new MaterialCategory
                {
                    Name = $"Approval-{unique}",
                    NormalizedName = $"APPROVAL-{unique}".ToUpperInvariant(),
                    SectorId = 1
                };
                db.MaterialCategories.Add(category);
                await db.SaveChangesAsync();
                categoryId = category.Id;
                var material = new Material
                {
                    Code = $"APV-{unique}",
                    NormalizedCode = $"APV-{unique}".ToUpperInvariant(),
                    Name = "Material para aprovação concorrente",
                    Unit = "unidade",
                    TrackingMode = MaterialTrackingMode.Quantity,
                    MinimumStock = 0m,
                    CategoryId = category.Id,
                    SectorId = 1
                };
                db.Materials.Add(material);
                await db.SaveChangesAsync();
                materialId = material.Id;
                db.InventoryMovements.Add(new InventoryMovement
                {
                    MaterialId = material.Id,
                    SectorId = 1,
                    Type = InventoryMovementType.Entry,
                    Quantity = 10m,
                    Reference = $"SEED-{unique}",
                    ActorUserId = "postgres-test-user",
                    ActorName = "postgres-test-user",
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
                var request = new InventoryAdjustmentRequest
                {
                    MaterialId = material.Id,
                    SectorId = 1,
                    DeltaQuantity = -3m,
                    Reason = "Ajuste pendente do teste concorrente",
                    Status = InventoryAdjustmentStatus.Pending,
                    RequestedByUserId = "assistant-requester",
                    RequestedByName = "Assistente de teste",
                    RequestedAtUtc = DateTimeOffset.UtcNow
                };
                db.InventoryAdjustmentRequests.Add(request);
                await db.SaveChangesAsync();
                requestId = request.Id;
            }

            async Task<string> GetTokenAsync(HttpClient client)
            {
                var page = await client.GetAsync("/Ajustes");
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                var html = await page.Content.ReadAsStringAsync();
                var match = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
                Assert.True(match.Success, "The adjustment page should emit an antiforgery token.");
                return match.Groups[1].Value;
            }

            var tokens = await Task.WhenAll(GetTokenAsync(firstClient), GetTokenAsync(secondClient));
            using var firstForm = CreateApprovalForm(requestId, tokens[0]);
            using var secondForm = CreateApprovalForm(requestId, tokens[1]);
            var responses = await Task.WhenAll(
                firstClient.PostAsync("/Ajustes?handler=Approve", firstForm),
                secondClient.PostAsync("/Ajustes?handler=Approve", secondForm));
            try
            {
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Redirect);
                Assert.Single(responses, response => response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict);
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var persistedRequest = await verifyDb.InventoryAdjustmentRequests.SingleAsync(item => item.Id == requestId);
            Assert.Equal(InventoryAdjustmentStatus.Approved, persistedRequest.Status);
            Assert.Equal("postgres-test-user", persistedRequest.DecidedByUserId);
            Assert.Equal(7m, await verifyDb.InventoryMovements
                .Where(movement => movement.MaterialId == materialId)
                .SumAsync(movement => movement.Quantity));
            Assert.Single(await verifyDb.InventoryMovements
                .Where(movement => movement.InventoryAdjustmentRequestId == requestId).ToListAsync());
        }
        finally
        {
            await PostgreSqlIntegrationSupport.DeleteMaterialScenarioAsync(factory.Services, materialId, categoryId);
        }
    }

    [PostgreSqlFact]
    public async Task ConcurrentManagerAdjustmentsCannotOverspendStock()
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
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "PostgreSqlAdjustmentTest";
                    options.DefaultChallengeScheme = "PostgreSqlAdjustmentTest";
                }).AddScheme<AuthenticationSchemeOptions, PostgreSqlTestAuthenticationHandler>(
                    "PostgreSqlAdjustmentTest", _ => { });
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
                    Name = $"Adjustment-{unique}",
                    NormalizedName = $"ADJUSTMENT-{unique}".ToUpperInvariant(),
                    SectorId = 1
                };
                db.MaterialCategories.Add(category);
                await db.SaveChangesAsync();
                categoryId = category.Id;
                var material = new Material
                {
                    Code = $"ADJ-{unique}",
                    NormalizedCode = $"ADJ-{unique}".ToUpperInvariant(),
                    Name = "Material para teste concorrente",
                    Unit = "unidade",
                    TrackingMode = MaterialTrackingMode.Quantity,
                    MinimumStock = 0m,
                    CategoryId = category.Id,
                    SectorId = 1
                };
                db.Materials.Add(material);
                await db.SaveChangesAsync();
                db.InventoryMovements.Add(new InventoryMovement
                {
                    MaterialId = material.Id,
                    SectorId = 1,
                    Type = InventoryMovementType.Entry,
                    Quantity = 10m,
                    Reference = $"SEED-{unique}",
                    ActorUserId = "postgres-test-user",
                    ActorName = "postgres-test-user",
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();
                materialId = material.Id;
            }

            async Task<(string Token, string SubmissionId, HttpStatusCode Status)> GetTokenAsync(HttpClient client)
            {
                var page = await client.GetAsync("/Ajustes");
                var html = await page.Content.ReadAsStringAsync();
                var match = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
                var submissionKey = Regex.Match(html, @"name=""SubmissionId""[^>]*value=""([^""]+)""");
                Assert.True(match.Success, "The adjustment page should emit an antiforgery token.");
                Assert.True(submissionKey.Success, "The adjustment page should emit a submission key.");
                return (match.Groups[1].Value, submissionKey.Groups[1].Value, page.StatusCode);
            }

            var tokens = await Task.WhenAll(GetTokenAsync(firstClient), GetTokenAsync(secondClient));
            Assert.All(tokens, token => Assert.Equal(HttpStatusCode.OK, token.Status));
            using var firstForm = CreateForm(materialId, tokens[0].Token, "CONCURRENT-A", tokens[0].SubmissionId);
            using var secondForm = CreateForm(materialId, tokens[1].Token, "CONCURRENT-B", tokens[1].SubmissionId);
            var responses = await Task.WhenAll(
                firstClient.PostAsync("/Ajustes?handler=Submit", firstForm),
                secondClient.PostAsync("/Ajustes?handler=Submit", secondForm));
            try
            {
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Redirect);
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var acceptedRequest = await verifyDb.InventoryAdjustmentRequests.SingleAsync(request => request.MaterialId == materialId);
            Assert.NotNull(acceptedRequest.SubmissionId);
            var replayIdentity = await GetTokenAsync(firstClient);
            using var replayForm = CreateForm(materialId, replayIdentity.Token, acceptedRequest.Reason, acceptedRequest.SubmissionId!.Value.ToString());
            using var replayResponse = await firstClient.PostAsync("/Ajustes?handler=Submit", replayForm);
            Assert.Equal(HttpStatusCode.Redirect, replayResponse.StatusCode);
            Assert.Single(await verifyDb.InventoryAdjustmentRequests.Where(request => request.MaterialId == materialId).ToListAsync());
            Assert.Equal(3m, await verifyDb.InventoryMovements
                .Where(movement => movement.MaterialId == materialId)
                .SumAsync(movement => movement.Quantity));
            Assert.Single(await verifyDb.InventoryAdjustmentRequests
                .Where(request => request.MaterialId == materialId).ToListAsync());
            Assert.Equal(2, await verifyDb.InventoryMovements
                .CountAsync(movement => movement.MaterialId == materialId));
        }
        finally
        {
            await PostgreSqlIntegrationSupport.DeleteMaterialScenarioAsync(factory.Services, materialId, categoryId);
        }
    }

    [PostgreSqlFact]
    public async Task ConcurrentApprovalAndRejectionPersistOnlyOneDecision()
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
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "PostgreSqlAdjustmentTest";
                    options.DefaultChallengeScheme = "PostgreSqlAdjustmentTest";
                }).AddScheme<AuthenticationSchemeOptions, PostgreSqlTestAuthenticationHandler>(
                    "PostgreSqlAdjustmentTest", _ => { });
            });
        });

        var materialId = 0;
        var categoryId = 0;
        try
        {
            using var approveClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var rejectClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            int requestId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
                Assert.Equal(connectionString, db.Database.GetConnectionString());
                var unique = Guid.NewGuid().ToString("N");
                var category = new MaterialCategory
                {
                    Name = $"Decision-{unique}",
                    NormalizedName = $"DECISION-{unique}".ToUpperInvariant(),
                    SectorId = 1
                };
                db.MaterialCategories.Add(category);
                await db.SaveChangesAsync();
                categoryId = category.Id;
                var material = new Material
                {
                    Code = $"DEC-{unique}",
                    NormalizedCode = $"DEC-{unique}".ToUpperInvariant(),
                    Name = "Material para disputa de decisão",
                    Unit = "unidade",
                    TrackingMode = MaterialTrackingMode.Quantity,
                    MinimumStock = 0m,
                    CategoryId = category.Id,
                    SectorId = 1
                };
                db.Materials.Add(material);
                await db.SaveChangesAsync();
                materialId = material.Id;
                db.InventoryMovements.Add(new InventoryMovement
                {
                    MaterialId = material.Id,
                    SectorId = 1,
                    Type = InventoryMovementType.Entry,
                    Quantity = 10m,
                    Reference = $"SEED-{unique}",
                    ActorUserId = "postgres-test-user",
                    ActorName = "postgres-test-user",
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
                var request = new InventoryAdjustmentRequest
                {
                    MaterialId = material.Id,
                    SectorId = 1,
                    DeltaQuantity = -3m,
                    Reason = "Pedido pendente para corrida de decisão",
                    Status = InventoryAdjustmentStatus.Pending,
                    RequestedByUserId = "assistant-requester",
                    RequestedByName = "Assistente de teste",
                    RequestedAtUtc = DateTimeOffset.UtcNow
                };
                db.InventoryAdjustmentRequests.Add(request);
                await db.SaveChangesAsync();
                requestId = request.Id;
            }

            var tokens = await Task.WhenAll(GetAntiforgeryTokenAsync(approveClient), GetAntiforgeryTokenAsync(rejectClient));
            using var approveForm = CreateApprovalForm(requestId, tokens[0]);
            using var rejectForm = CreateApprovalForm(requestId, tokens[1]);
            var responses = await Task.WhenAll(
                approveClient.PostAsync("/Ajustes?handler=Approve", approveForm),
                rejectClient.PostAsync("/Ajustes?handler=Reject", rejectForm));
            try
            {
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Redirect);
                Assert.Single(responses, response => response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict);
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var persistedRequest = await verifyDb.InventoryAdjustmentRequests.SingleAsync(item => item.Id == requestId);
            Assert.Contains(persistedRequest.Status, new[] { InventoryAdjustmentStatus.Approved, InventoryAdjustmentStatus.Rejected });
            var decisionMovements = await verifyDb.InventoryMovements
                .Where(movement => movement.InventoryAdjustmentRequestId == requestId).ToListAsync();
            if (persistedRequest.Status == InventoryAdjustmentStatus.Approved)
            {
                Assert.Single(decisionMovements);
                Assert.Equal(7m, await verifyDb.InventoryMovements
                    .Where(movement => movement.MaterialId == materialId)
                    .SumAsync(movement => movement.Quantity));
            }
            else
            {
                Assert.Empty(decisionMovements);
                Assert.Equal(10m, await verifyDb.InventoryMovements
                    .Where(movement => movement.MaterialId == materialId)
                    .SumAsync(movement => movement.Quantity));
            }
        }
        finally
        {
            await PostgreSqlIntegrationSupport.DeleteMaterialScenarioAsync(factory.Services, materialId, categoryId);
        }
    }

    [PostgreSqlFact]
    public async Task ConcurrentRejectionsPersistOneDecisionOnly()
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
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "PostgreSqlAdjustmentTest";
                    options.DefaultChallengeScheme = "PostgreSqlAdjustmentTest";
                }).AddScheme<AuthenticationSchemeOptions, PostgreSqlTestAuthenticationHandler>(
                    "PostgreSqlAdjustmentTest", _ => { });
            });
        });

        var materialId = 0;
        var categoryId = 0;
        try
        {
            using var firstClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var secondClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            int requestId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
                Assert.Equal(connectionString, db.Database.GetConnectionString());
                var unique = Guid.NewGuid().ToString("N");
                var category = new MaterialCategory
                {
                    Name = $"Reject-{unique}",
                    NormalizedName = $"REJECT-{unique}".ToUpperInvariant(),
                    SectorId = 1
                };
                db.MaterialCategories.Add(category);
                await db.SaveChangesAsync();
                categoryId = category.Id;
                var material = new Material
                {
                    Code = $"REJ-{unique}",
                    NormalizedCode = $"REJ-{unique}".ToUpperInvariant(),
                    Name = "Material para rejeição concorrente",
                    Unit = "unidade",
                    TrackingMode = MaterialTrackingMode.Quantity,
                    MinimumStock = 0m,
                    CategoryId = category.Id,
                    SectorId = 1
                };
                db.Materials.Add(material);
                await db.SaveChangesAsync();
                materialId = material.Id;
                db.InventoryMovements.Add(new InventoryMovement
                {
                    MaterialId = material.Id,
                    SectorId = 1,
                    Type = InventoryMovementType.Entry,
                    Quantity = 10m,
                    Reference = $"SEED-{unique}",
                    ActorUserId = "postgres-test-user",
                    ActorName = "postgres-test-user",
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
                var request = new InventoryAdjustmentRequest
                {
                    MaterialId = material.Id,
                    SectorId = 1,
                    DeltaQuantity = -3m,
                    Reason = "Pedido pendente para rejeição concorrente",
                    Status = InventoryAdjustmentStatus.Pending,
                    RequestedByUserId = "assistant-requester",
                    RequestedByName = "Assistente de teste",
                    RequestedAtUtc = DateTimeOffset.UtcNow
                };
                db.InventoryAdjustmentRequests.Add(request);
                await db.SaveChangesAsync();
                requestId = request.Id;
            }

            var tokens = await Task.WhenAll(
                GetAntiforgeryTokenAsync(firstClient),
                GetAntiforgeryTokenAsync(secondClient));
            using var firstForm = CreateApprovalForm(requestId, tokens[0]);
            using var secondForm = CreateApprovalForm(requestId, tokens[1]);
            var responses = await Task.WhenAll(
                firstClient.PostAsync("/Ajustes?handler=Reject", firstForm),
                secondClient.PostAsync("/Ajustes?handler=Reject", secondForm));
            try
            {
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Redirect);
                Assert.Single(responses, response => response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict);
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var persistedRequest = await verifyDb.InventoryAdjustmentRequests.SingleAsync(item => item.Id == requestId);
            Assert.Equal(InventoryAdjustmentStatus.Rejected, persistedRequest.Status);
            Assert.False(string.IsNullOrWhiteSpace(persistedRequest.DecidedByUserId));
            Assert.Empty(await verifyDb.InventoryMovements
                .Where(movement => movement.InventoryAdjustmentRequestId == requestId)
                .ToListAsync());
            Assert.Equal(10m, await verifyDb.InventoryMovements
                .Where(movement => movement.MaterialId == materialId)
                .SumAsync(movement => movement.Quantity));
        }
        finally
        {
            await PostgreSqlIntegrationSupport.DeleteMaterialScenarioAsync(factory.Services, materialId, categoryId);
        }
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var page = await client.GetAsync("/Ajustes");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var match = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(match.Success, "The adjustment page should emit an antiforgery token.");
        return match.Groups[1].Value;
    }

    private static FormUrlEncodedContent CreateForm(int materialId, string token, string reference, string submissionId) =>
        new(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["DeltaQuantity"] = "-7",
            ["Reason"] = reference,
            ["SubmissionId"] = submissionId,
            ["__RequestVerificationToken"] = token
        });

    private static FormUrlEncodedContent CreateApprovalForm(int requestId, string token) =>
        new(new Dictionary<string, string>
        {
            ["RequestId"] = requestId.ToString(),
            ["__RequestVerificationToken"] = token
        });
}
