using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SistemaMateriais.Web.Data;
using Xunit;

namespace SistemaMateriais.Web.Tests;

[Collection("PostgreSQL integration")]
public sealed class PostgreSqlInventoryMovementIdempotencyTests
{
    [PostgreSqlFact]
    public async Task ConcurrentReceiptReplayWithSameKeyCreatesOneMovementAndBothRequestsSucceed()
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
                    options.DefaultAuthenticateScheme = "PostgreSqlIdempotencyTest";
                    options.DefaultChallengeScheme = "PostgreSqlIdempotencyTest";
                }).AddScheme<AuthenticationSchemeOptions, PostgreSqlTestAuthenticationHandler>(
                    "PostgreSqlIdempotencyTest", _ => { });
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
                    Name = $"Idempotency-{unique}",
                    NormalizedName = $"IDEMPOTENCY-{unique}".ToUpperInvariant(),
                    SectorId = 1
                };
                db.MaterialCategories.Add(category);
                await db.SaveChangesAsync();
                categoryId = category.Id;
                var material = new Material
                {
                    Code = $"IDEMP-{unique}",
                    NormalizedCode = $"IDEMP-{unique}".ToUpperInvariant(),
                    Name = "Material para replay concorrente",
                    Unit = "unidade",
                    TrackingMode = MaterialTrackingMode.Quantity,
                    MinimumStock = 0m,
                    CategoryId = category.Id,
                    SectorId = 1
                };
                db.Materials.Add(material);
                await db.SaveChangesAsync();
                materialId = material.Id;
            }

            async Task<(string Token, Guid SubmissionId)> GetReceiptFormIdentityAsync(HttpClient client)
            {
                using var page = await client.GetAsync("/Movimentacoes/Entrada");
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                var html = await page.Content.ReadAsStringAsync();
                var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
                var submission = Regex.Match(html, @"name=""SubmissionId""[^>]*value=""([^""]+)""");
                Assert.True(token.Success, "The receipt page should emit an antiforgery token.");
                Assert.True(submission.Success, "The receipt page should emit an idempotency key.");
                return (token.Groups[1].Value, Guid.Parse(submission.Groups[1].Value));
            }

            var identities = await Task.WhenAll(
                GetReceiptFormIdentityAsync(firstClient),
                GetReceiptFormIdentityAsync(secondClient));
            var sharedSubmissionId = identities[0].SubmissionId;
            using var firstForm = CreateReceiptForm(materialId, sharedSubmissionId, identities[0].Token);
            using var secondForm = CreateReceiptForm(materialId, sharedSubmissionId, identities[1].Token);
            var responses = await Task.WhenAll(
                firstClient.PostAsync("/Movimentacoes/Entrada", firstForm),
                secondClient.PostAsync("/Movimentacoes/Entrada", secondForm));
            try
            {
                Assert.All(responses, response => Assert.Equal(HttpStatusCode.Redirect, response.StatusCode));
                Assert.True(lookupBarrier.ObservedSubmissionLookups >= 2,
                    "Both requests should pass the initial idempotency lookup before either writes.");
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
            var movement = await verifyDb.InventoryMovements.SingleAsync(item => item.SubmissionId == sharedSubmissionId);
            Assert.Equal(materialId, movement.MaterialId);
            Assert.Equal(2m, movement.Quantity);
            Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync(item => item.MaterialId == materialId));
            Assert.Matches("^[0-9A-F]{64}$", movement.SubmissionFingerprint);
        }
        finally
        {
            await PostgreSqlIntegrationSupport.DeleteMaterialScenarioAsync(factory.Services, materialId, categoryId);
        }
    }

    [PostgreSqlFact]
    public async Task ConcurrentReplayForConsumptionLoanReturnAndAdjustmentPersistsOneEffectPerOperation()
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
                    options.DefaultAuthenticateScheme = "PostgreSqlIdempotencyTest";
                    options.DefaultChallengeScheme = "PostgreSqlIdempotencyTest";
                }).AddScheme<AuthenticationSchemeOptions, PostgreSqlTestAuthenticationHandler>(
                    "PostgreSqlIdempotencyTest", _ => { });
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
                var unique = Guid.NewGuid().ToString("N");
                var category = new MaterialCategory
                {
                    Name = $"Replay-{unique}",
                    NormalizedName = $"REPLAY-{unique}".ToUpperInvariant(),
                    SectorId = 1
                };
                db.MaterialCategories.Add(category);
                await db.SaveChangesAsync();
                categoryId = category.Id;
                var material = new Material
                {
                    Code = $"REPLAY-{unique}",
                    NormalizedCode = $"REPLAY-{unique}".ToUpperInvariant(),
                    Name = "Material para replay concorrente",
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
                await db.SaveChangesAsync();
            }

            async Task<(string Token, Guid SubmissionId)> GetFormIdentityAsync(
                HttpClient client,
                string pagePath,
                string submissionField)
            {
                using var page = await client.GetAsync(pagePath);
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                var html = await page.Content.ReadAsStringAsync();
                var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
                var key = Regex.Match(html,
                    $@"name=""{Regex.Escape(submissionField)}""[^>]*value=""([^""]+)""");
                Assert.True(token.Success, $"{pagePath} should emit an antiforgery token.");
                Assert.True(key.Success, $"{submissionField} should be rendered.");
                return (token.Groups[1].Value, Guid.Parse(key.Groups[1].Value));
            }

            async Task AssertConcurrentReplayAsync(
                string route,
                Dictionary<string, string> firstFields,
                Dictionary<string, string> secondFields)
            {
                using var firstForm = new FormUrlEncodedContent(firstFields);
                using var secondForm = new FormUrlEncodedContent(secondFields);
                lookupBarrier.Reset();
                var responses = await Task.WhenAll(
                    firstClient.PostAsync(route, firstForm),
                    secondClient.PostAsync(route, secondForm))
                    .WaitAsync(TimeSpan.FromSeconds(45));
                try
                {
                    Assert.All(responses, response => Assert.Equal(HttpStatusCode.Redirect, response.StatusCode));
                    Assert.True(lookupBarrier.ObservedSubmissionLookups >= 2,
                        "Both requests must finish the initial idempotency lookup before either writes.");
                }
                finally
                {
                    foreach (var response in responses)
                    {
                        response.Dispose();
                    }
                }
            }

            var consumptionForms = await Task.WhenAll(
                GetFormIdentityAsync(firstClient, "/Movimentacoes/Saida", "SubmissionId"),
                GetFormIdentityAsync(secondClient, "/Movimentacoes/Saida", "SubmissionId"));
            var consumptionKey = consumptionForms[0].SubmissionId;
            await AssertConcurrentReplayAsync("/Movimentacoes/Saida",
                new Dictionary<string, string>
                {
                    ["MaterialId"] = materialId.ToString(),
                    ["Quantity"] = "2",
                    ["Reference"] = "CONSUMPTION-REPLAY",
                    ["SubmissionId"] = consumptionKey.ToString(),
                    ["__RequestVerificationToken"] = consumptionForms[0].Token
                },
                new Dictionary<string, string>
                {
                    ["MaterialId"] = materialId.ToString(),
                    ["Quantity"] = "2",
                    ["Reference"] = "CONSUMPTION-REPLAY",
                    ["SubmissionId"] = consumptionKey.ToString(),
                    ["__RequestVerificationToken"] = consumptionForms[1].Token
                });

            var loanForms = await Task.WhenAll(
                GetFormIdentityAsync(firstClient, "/Emprestimos", "LoanSubmissionId"),
                GetFormIdentityAsync(secondClient, "/Emprestimos", "LoanSubmissionId"));
            var loanKey = loanForms[0].SubmissionId;
            await AssertConcurrentReplayAsync("/Emprestimos?handler=Loan",
                new Dictionary<string, string>
                {
                    ["MaterialId"] = materialId.ToString(),
                    ["BorrowerName"] = "Replay Test",
                    ["Quantity"] = "2",
                    ["Reference"] = "LOAN-REPLAY",
                    ["LoanSubmissionId"] = loanKey.ToString(),
                    ["__RequestVerificationToken"] = loanForms[0].Token
                },
                new Dictionary<string, string>
                {
                    ["MaterialId"] = materialId.ToString(),
                    ["BorrowerName"] = "Replay Test",
                    ["Quantity"] = "2",
                    ["Reference"] = "LOAN-REPLAY",
                    ["LoanSubmissionId"] = loanKey.ToString(),
                    ["__RequestVerificationToken"] = loanForms[1].Token
                });

            int loanId;
            await using (var loanScope = factory.Services.CreateAsyncScope())
            {
                var db = loanScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                loanId = await db.InventoryLoans.Where(loan => loan.MaterialId == materialId)
                    .Select(loan => loan.Id).SingleAsync();
            }

            var returnForms = await Task.WhenAll(
                GetFormIdentityAsync(firstClient, "/Emprestimos", "ReturnSubmissionId"),
                GetFormIdentityAsync(secondClient, "/Emprestimos", "ReturnSubmissionId"));
            var returnKey = returnForms[0].SubmissionId;
            await AssertConcurrentReplayAsync("/Emprestimos?handler=Return",
                new Dictionary<string, string>
                {
                    ["LoanId"] = loanId.ToString(),
                    ["ReturnQuantity"] = "1",
                    ["ReturnSubmissionId"] = returnKey.ToString(),
                    ["__RequestVerificationToken"] = returnForms[0].Token
                },
                new Dictionary<string, string>
                {
                    ["LoanId"] = loanId.ToString(),
                    ["ReturnQuantity"] = "1",
                    ["ReturnSubmissionId"] = returnKey.ToString(),
                    ["__RequestVerificationToken"] = returnForms[1].Token
                });

            var adjustmentForms = await Task.WhenAll(
                GetFormIdentityAsync(firstClient, "/Ajustes", "SubmissionId"),
                GetFormIdentityAsync(secondClient, "/Ajustes", "SubmissionId"));
            var adjustmentKey = adjustmentForms[0].SubmissionId;
            await AssertConcurrentReplayAsync("/Ajustes?handler=Submit",
                new Dictionary<string, string>
                {
                    ["MaterialId"] = materialId.ToString(),
                    ["DeltaQuantity"] = "1",
                    ["Reason"] = "ADJUSTMENT-REPLAY",
                    ["SubmissionId"] = adjustmentKey.ToString(),
                    ["__RequestVerificationToken"] = adjustmentForms[0].Token
                },
                new Dictionary<string, string>
                {
                    ["MaterialId"] = materialId.ToString(),
                    ["DeltaQuantity"] = "1",
                    ["Reason"] = "ADJUSTMENT-REPLAY",
                    ["SubmissionId"] = adjustmentKey.ToString(),
                    ["__RequestVerificationToken"] = adjustmentForms[1].Token
                });

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Single(await verifyDb.InventoryMovements.Where(item => item.SubmissionId == consumptionKey).ToListAsync());
            Assert.Single(await verifyDb.InventoryMovements.Where(item => item.SubmissionId == loanKey).ToListAsync());
            Assert.Single(await verifyDb.InventoryMovements.Where(item => item.SubmissionId == returnKey).ToListAsync());
            Assert.Single(await verifyDb.InventoryMovements.Where(item => item.SubmissionId == adjustmentKey).ToListAsync());
            Assert.Single(await verifyDb.InventoryLoans.Where(loan => loan.MaterialId == materialId).ToListAsync());
            Assert.Single(await verifyDb.InventoryAdjustmentRequests
                .Where(request => request.SubmissionId == adjustmentKey).ToListAsync());
            Assert.Equal(8m, await verifyDb.InventoryMovements
                .Where(movement => movement.MaterialId == materialId)
                .Select(movement => movement.Type == InventoryMovementType.Entry || movement.Type == InventoryMovementType.Return
                    ? movement.Quantity
                    : movement.Type == InventoryMovementType.Consumption || movement.Type == InventoryMovementType.Loan
                        ? -movement.Quantity
                        : movement.Quantity)
                .SumAsync());
        }
        finally
        {
            await PostgreSqlIntegrationSupport.DeleteMaterialScenarioAsync(factory.Services, materialId, categoryId);
        }
    }

    private static FormUrlEncodedContent CreateReceiptForm(int materialId, Guid submissionId, string antiforgeryToken) =>
        new(new Dictionary<string, string>
        {
            ["MaterialId"] = materialId.ToString(),
            ["Quantity"] = "2",
            ["Reference"] = "NF-CONCURRENT-REPLAY",
            ["SubmissionId"] = submissionId.ToString(),
            ["__RequestVerificationToken"] = antiforgeryToken
        });
}

internal sealed class SubmissionLookupBarrierInterceptor : DbCommandInterceptor
{
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string, bool> _matchesSubmissionLookup;
    private int _observedSubmissionLookups;

    public SubmissionLookupBarrierInterceptor(Func<string, bool>? matchesSubmissionLookup = null)
    {
        _matchesSubmissionLookup = matchesSubmissionLookup
            ?? (commandText => commandText.Contains("\"SubmissionId\"", StringComparison.Ordinal));
    }

    public int ObservedSubmissionLookups => Volatile.Read(ref _observedSubmissionLookups);

    public void Reset()
    {
        Interlocked.Exchange(ref _observedSubmissionLookups, 0);
        Interlocked.Exchange(ref _release, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        var release = Volatile.Read(ref _release);
        if (_matchesSubmissionLookup(command.CommandText)
            && Interlocked.Increment(ref _observedSubmissionLookups) <= 2)
        {
            if (ObservedSubmissionLookups == 2)
            {
                release.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
        }

        return result;
    }
}
