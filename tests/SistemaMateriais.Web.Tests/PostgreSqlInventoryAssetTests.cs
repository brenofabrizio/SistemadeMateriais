using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
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
public sealed class PostgreSqlInventoryAssetTests
{
    [PostgreSqlFact]
    public async Task IndividualAssetLoanAndReturnPersistThroughPostgreSqlHttpFlow()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSql");
        Assert.False(string.IsNullOrWhiteSpace(connectionString),
            "Set ConnectionStrings__PostgreSql to the disposable integration-test database.");
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
                    options.DefaultAuthenticateScheme = "PostgreSqlTest";
                    options.DefaultChallengeScheme = "PostgreSqlTest";
                }).AddScheme<AuthenticationSchemeOptions, PostgreSqlTestAuthenticationHandler>("PostgreSqlTest", _ => { });
            });
        });
        var materialId = 0;
        var categoryId = 0;
        try
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var competingClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
                Assert.Equal(connectionString, db.Database.GetConnectionString());
                Assert.True(await db.Sectors.AnyAsync(sector => sector.Id == 1 && sector.Name == "TI"));
                var unique = Guid.NewGuid().ToString("N");
                var category = new MaterialCategory
                {
                    Name = $"Integration-{unique}",
                    NormalizedName = $"INTEGRATION-{unique}".ToUpperInvariant(),
                    SectorId = 1
                };
                db.MaterialCategories.Add(category);
                await db.SaveChangesAsync();
                categoryId = category.Id;
                var material = new Material
                {
                    Code = $"PGA-{unique}",
                    NormalizedCode = $"PGA-{unique}".ToUpperInvariant(),
                    Name = "Projetor de integração",
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

            async Task<string> GetAntiforgeryTokenAsync(HttpClient targetClient)
            {
                var response = await targetClient.GetAsync("/Ativos");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var html = await response.Content.ReadAsStringAsync();
                var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
                Assert.True(token.Success, "The assets page should emit an antiforgery token.");
                return token.Groups[1].Value;
            }

            using (var registerForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["MaterialId"] = materialId.ToString(),
                ["Identifier"] = $"PG-PAT-{Guid.NewGuid():N}",
                ["Reference"] = "PG-RECEIPT-001",
                ["__RequestVerificationToken"] = await GetAntiforgeryTokenAsync(client)
            }))
            {
                Assert.Equal(HttpStatusCode.Redirect,
                    (await client.PostAsync("/Ativos?handler=Register", registerForm)).StatusCode);
            }

            int assetId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                assetId = await db.InventoryAssets.Where(asset => asset.MaterialId == materialId)
                    .Select(asset => asset.Id).SingleAsync();
            }
            string registeredIdentifier;
            Guid registrationSubmissionId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var asset = await db.InventoryAssets.SingleAsync(item => item.Id == assetId);
                registeredIdentifier = asset.Identifier;
                var entry = await db.InventoryMovements.SingleAsync(movement =>
                    movement.InventoryAssetId == assetId && movement.Type == InventoryMovementType.Entry);
                Assert.NotNull(entry.SubmissionId);
                Assert.False(string.IsNullOrWhiteSpace(entry.SubmissionFingerprint));
                registrationSubmissionId = entry.SubmissionId!.Value;
            }
            using (var replayRegistrationForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["MaterialId"] = materialId.ToString(),
                ["Identifier"] = registeredIdentifier,
                ["Reference"] = "PG-RECEIPT-001",
                ["AssetRegistrationSubmissionId"] = registrationSubmissionId.ToString(),
                ["__RequestVerificationToken"] = await GetAntiforgeryTokenAsync(client)
            }))
            {
                Assert.Equal(HttpStatusCode.Redirect,
                    (await client.PostAsync("/Ativos?handler=Register", replayRegistrationForm)).StatusCode);
            }
            var loanTokenTask = GetAntiforgeryTokenAsync(client);
            var competingLoanTokenTask = GetAntiforgeryTokenAsync(competingClient);
            await Task.WhenAll(loanTokenTask, competingLoanTokenTask);
            using (var loanForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["AssetId"] = assetId.ToString(),
                ["BorrowerName"] = "Teste PostgreSQL",
                ["LoanReference"] = "PG-LOAN-001",
                ["__RequestVerificationToken"] = loanTokenTask.Result
            }))
            using (var competingLoanForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["AssetId"] = assetId.ToString(),
                ["BorrowerName"] = "Teste concorrente PostgreSQL",
                ["LoanReference"] = "PG-LOAN-002",
                ["__RequestVerificationToken"] = competingLoanTokenTask.Result
            }))
            {
                var responses = await Task.WhenAll(
                    client.PostAsync("/Ativos?handler=LoanAsset", loanForm),
                    competingClient.PostAsync("/Ativos?handler=LoanAsset", competingLoanForm));
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Redirect);
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
                var conflict = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
                Assert.Contains("O ativo foi alterado por outra operação.",
                    System.Net.WebUtility.HtmlDecode(await conflict.Content.ReadAsStringAsync()));
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            int loanId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                loanId = await db.InventoryLoans.Where(loan => loan.InventoryAssetId == assetId)
                    .Select(loan => loan.Id).SingleAsync();
                Assert.Equal(InventoryAssetStatus.Loaned,
                    (await db.InventoryAssets.SingleAsync(asset => asset.Id == assetId)).Status);
            }
            Guid loanSubmissionId;
            string borrowerName;
            string? loanReference;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var loan = await db.InventoryLoans.SingleAsync(item => item.Id == loanId);
                borrowerName = loan.BorrowerName;
                loanReference = loan.Reference;
                var movement = await db.InventoryMovements.SingleAsync(item =>
                    item.InventoryLoanId == loanId && item.Type == InventoryMovementType.Loan);
                Assert.NotNull(movement.SubmissionId);
                Assert.False(string.IsNullOrWhiteSpace(movement.SubmissionFingerprint));
                loanSubmissionId = movement.SubmissionId!.Value;
            }
            using (var replayLoanForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["AssetId"] = assetId.ToString(),
                ["BorrowerName"] = borrowerName,
                ["LoanReference"] = loanReference ?? string.Empty,
                ["AssetLoanSubmissionId"] = loanSubmissionId.ToString(),
                ["__RequestVerificationToken"] = await GetAntiforgeryTokenAsync(client)
            }))
            {
                Assert.Equal(HttpStatusCode.Redirect,
                    (await client.PostAsync("/Ativos?handler=LoanAsset", replayLoanForm)).StatusCode);
            }
            var returnTokenTask = GetAntiforgeryTokenAsync(client);
            var competingReturnTokenTask = GetAntiforgeryTokenAsync(competingClient);
            await Task.WhenAll(returnTokenTask, competingReturnTokenTask);
            using (var returnForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["LoanId"] = loanId.ToString(),
                ["__RequestVerificationToken"] = returnTokenTask.Result
            }))
            using (var competingReturnForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["LoanId"] = loanId.ToString(),
                ["__RequestVerificationToken"] = competingReturnTokenTask.Result
            }))
            {
                var responses = await Task.WhenAll(
                    client.PostAsync("/Ativos?handler=ReturnAsset", returnForm),
                    competingClient.PostAsync("/Ativos?handler=ReturnAsset", competingReturnForm));
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Redirect);
                var duplicateReturn = Assert.Single(responses, response => response.StatusCode != HttpStatusCode.Redirect);
                Assert.True(duplicateReturn.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.NotFound,
                    $"Expected a validation, conflict, or not-found response, received {(int)duplicateReturn.StatusCode}.");
                if (duplicateReturn.StatusCode == HttpStatusCode.Conflict)
                {
                    Assert.Contains("O empréstimo foi alterado por outra operação.",
                        System.Net.WebUtility.HtmlDecode(await duplicateReturn.Content.ReadAsStringAsync()));
                }
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal(InventoryAssetStatus.Available,
                    (await db.InventoryAssets.SingleAsync(asset => asset.Id == assetId)).Status);
                Assert.Equal(1, await db.InventoryMovements.CountAsync(movement =>
                    movement.InventoryLoanId == loanId && movement.Type == InventoryMovementType.Return));
            }
            Guid returnSubmissionId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var returned = await db.InventoryMovements.SingleAsync(movement =>
                    movement.InventoryLoanId == loanId && movement.Type == InventoryMovementType.Return);
                Assert.NotNull(returned.SubmissionId);
                Assert.False(string.IsNullOrWhiteSpace(returned.SubmissionFingerprint));
                returnSubmissionId = returned.SubmissionId!.Value;
            }
            using (var replayReturnForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["LoanId"] = loanId.ToString(),
                ["AssetReturnSubmissionId"] = returnSubmissionId.ToString(),
                ["__RequestVerificationToken"] = await GetAntiforgeryTokenAsync(client)
            }))
            {
                Assert.Equal(HttpStatusCode.Redirect,
                    (await client.PostAsync("/Ativos?handler=ReturnAsset", replayReturnForm)).StatusCode);
            }

            var consumeTokenTask = GetAntiforgeryTokenAsync(client);
            var competingConsumeTokenTask = GetAntiforgeryTokenAsync(competingClient);
            await Task.WhenAll(consumeTokenTask, competingConsumeTokenTask);
            using (var consumeForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["AssetId"] = assetId.ToString(),
                ["Reference"] = "PG-CONSUME-001",
                ["__RequestVerificationToken"] = consumeTokenTask.Result
            }))
            using (var competingConsumeForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["AssetId"] = assetId.ToString(),
                ["Reference"] = "PG-CONSUME-002",
                ["__RequestVerificationToken"] = competingConsumeTokenTask.Result
            }))
            {
                var responses = await Task.WhenAll(
                    client.PostAsync("/Ativos?handler=ConsumeAsset", consumeForm),
                    competingClient.PostAsync("/Ativos?handler=ConsumeAsset", competingConsumeForm));
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Redirect);
                var duplicateConsume = Assert.Single(responses, response => response.StatusCode != HttpStatusCode.Redirect);
                Assert.True(duplicateConsume.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                    $"Expected a validation or conflict response, received {(int)duplicateConsume.StatusCode}.");
                if (duplicateConsume.StatusCode == HttpStatusCode.Conflict)
                {
                    Assert.Contains("O ativo foi alterado por outra operação.",
                        System.Net.WebUtility.HtmlDecode(await duplicateConsume.Content.ReadAsStringAsync()));
                }
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(InventoryAssetStatus.Consumed,
                (await verifyDb.InventoryAssets.SingleAsync(asset => asset.Id == assetId)).Status);
            var movements = await verifyDb.InventoryMovements
                .Where(movement => movement.MaterialId == materialId)
                .OrderBy(movement => movement.Id)
                .ToListAsync();
            Assert.Collection(movements,
                entry => Assert.Equal((InventoryMovementType.Entry, 1m, assetId),
                    (entry.Type, entry.Quantity, entry.InventoryAssetId)),
                loan => Assert.Equal((InventoryMovementType.Loan, 1m, assetId),
                    (loan.Type, loan.Quantity, loan.InventoryAssetId)),
                returned => Assert.Equal((InventoryMovementType.Return, 1m, assetId),
                    (returned.Type, returned.Quantity, returned.InventoryAssetId)),
                consumed => Assert.Equal((InventoryMovementType.Consumption, 1m, assetId),
                    (consumed.Type, consumed.Quantity, consumed.InventoryAssetId)));
            Assert.Equal(loanId, movements[1].InventoryLoanId);
            Assert.Equal(loanId, movements[2].InventoryLoanId);
            Assert.Null(movements[3].InventoryLoanId);
            Assert.NotNull(movements[3].SubmissionId);
            Assert.False(string.IsNullOrWhiteSpace(movements[3].SubmissionFingerprint));
            using var replayConsumeForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["AssetId"] = assetId.ToString(),
                ["Reference"] = movements[3].Reference ?? string.Empty,
                ["AssetConsumptionSubmissionId"] = movements[3].SubmissionId!.Value.ToString(),
                ["__RequestVerificationToken"] = await GetAntiforgeryTokenAsync(client)
            });
            Assert.Equal(HttpStatusCode.Redirect,
                (await client.PostAsync("/Ativos?handler=ConsumeAsset", replayConsumeForm)).StatusCode);
        }
        finally
        {
            await PostgreSqlIntegrationSupport.DeleteMaterialScenarioAsync(factory.Services, materialId, categoryId);
        }
    }

    [PostgreSqlFact]
    public async Task ConcurrentQuantityLoanReturnsCannotReturnTheSameOutstandingQuantityTwice()
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
                    options.DefaultAuthenticateScheme = "PostgreSqlTest";
                    options.DefaultChallengeScheme = "PostgreSqlTest";
                }).AddScheme<AuthenticationSchemeOptions, PostgreSqlTestAuthenticationHandler>("PostgreSqlTest", _ => { });
            });
        });
        var materialId = 0;
        var categoryId = 0;
        try
        {
            using var firstClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var secondClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var loanId = 0;

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
                Assert.Equal(connectionString, db.Database.GetConnectionString());
                var unique = Guid.NewGuid().ToString("N");
                var category = new MaterialCategory
                {
                    Name = $"Return-{unique}",
                    NormalizedName = $"RETURN-{unique}".ToUpperInvariant(),
                    SectorId = 1
                };
                db.MaterialCategories.Add(category);
                await db.SaveChangesAsync();
                categoryId = category.Id;
                var material = new Material
                {
                    Code = $"PGR-{unique}",
                    NormalizedCode = $"PGR-{unique}".ToUpperInvariant(),
                    Name = "Material de devolução concorrente",
                    Unit = "unidade",
                    TrackingMode = MaterialTrackingMode.Quantity,
                    MinimumStock = 0m,
                    CategoryId = category.Id,
                    SectorId = 1
                };
                db.Materials.Add(material);
                await db.SaveChangesAsync();
                materialId = material.Id;
                var loan = new InventoryLoan
                {
                    MaterialId = material.Id,
                    SectorId = 1,
                    BorrowerName = "Usuário de teste",
                    Quantity = 2m,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    CreatedByUserId = "pilot-user",
                    CreatedByName = "pilot-user"
                };
                db.InventoryLoans.Add(loan);
                await db.SaveChangesAsync();
                loanId = loan.Id;
                db.InventoryMovements.AddRange(
                    new InventoryMovement
                    {
                        MaterialId = material.Id,
                        SectorId = 1,
                        Type = InventoryMovementType.Entry,
                        Quantity = 5m,
                        ActorUserId = "pilot-user",
                        ActorName = "pilot-user",
                        CreatedAtUtc = DateTimeOffset.UtcNow
                    },
                    new InventoryMovement
                    {
                        MaterialId = material.Id,
                        SectorId = 1,
                        Type = InventoryMovementType.Loan,
                        Quantity = 2m,
                        InventoryLoanId = loan.Id,
                        ActorUserId = "pilot-user",
                        ActorName = "pilot-user",
                        CreatedAtUtc = loan.CreatedAtUtc
                    });
                await db.SaveChangesAsync();
            }

            async Task<(string Token, string SubmissionId)> GetTokenAsync(HttpClient client)
            {
                var page = await client.GetAsync("/Emprestimos");
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                var html = await page.Content.ReadAsStringAsync();
                var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
                var submissionKey = Regex.Match(html, @"name=""ReturnSubmissionId""[^>]*value=""([^""]+)""");
                Assert.True(token.Success, "The loan page should emit an antiforgery token.");
                Assert.True(submissionKey.Success, "The return form should emit a submission key.");
                return (token.Groups[1].Value, submissionKey.Groups[1].Value);
            }

            var firstToken = GetTokenAsync(firstClient);
            var secondToken = GetTokenAsync(secondClient);
            await Task.WhenAll(firstToken, secondToken);
            using var firstForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["LoanId"] = loanId.ToString(),
                ["ReturnQuantity"] = "2",
                ["ReturnSubmissionId"] = firstToken.Result.SubmissionId,
                ["__RequestVerificationToken"] = firstToken.Result.Token
            });
            using var secondForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["LoanId"] = loanId.ToString(),
                ["ReturnQuantity"] = "2",
                ["ReturnSubmissionId"] = secondToken.Result.SubmissionId,
                ["__RequestVerificationToken"] = secondToken.Result.Token
            });
            var responses = await Task.WhenAll(
                firstClient.PostAsync("/Emprestimos?handler=Return", firstForm),
                secondClient.PostAsync("/Emprestimos?handler=Return", secondForm));

            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Redirect);
            Assert.Equal(HttpStatusCode.Conflict, Assert.Single(responses, response => response.StatusCode != HttpStatusCode.Redirect).StatusCode);
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(1, await verifyDb.InventoryMovements.CountAsync(movement =>
                movement.InventoryLoanId == loanId && movement.Type == InventoryMovementType.Return));
            Assert.Equal(5m, await verifyDb.InventoryMovements
                .Where(movement => movement.MaterialId == materialId)
                .SumAsync(movement => movement.Type == InventoryMovementType.Entry || movement.Type == InventoryMovementType.Return
                    ? movement.Quantity
                    : movement.Type == InventoryMovementType.Consumption || movement.Type == InventoryMovementType.Loan
                        ? -movement.Quantity
                        : movement.Quantity));
        }
        finally
        {
            await PostgreSqlIntegrationSupport.DeleteMaterialScenarioAsync(factory.Services, materialId, categoryId);
        }
    }

    [PostgreSqlFact]
    public async Task ConcurrentPostgreSqlLoansDoNotOverspendOneUnit()
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
                    options.DefaultAuthenticateScheme = "PostgreSqlTest";
                    options.DefaultChallengeScheme = "PostgreSqlTest";
                }).AddScheme<AuthenticationSchemeOptions, PostgreSqlTestAuthenticationHandler>("PostgreSqlTest", _ => { });
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
                    Name = $"Concurrency-{unique}",
                    NormalizedName = $"CONCURRENCY-{unique}".ToUpperInvariant(),
                    SectorId = 1
                };
                db.MaterialCategories.Add(category);
                await db.SaveChangesAsync();
                categoryId = category.Id;
                var material = new Material
                {
                    Code = $"PQC-{unique}",
                    NormalizedCode = $"PQC-{unique}".ToUpperInvariant(),
                    Name = "Material de concorrência",
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
                    Quantity = 1m,
                    Reference = $"SEED-{unique}",
                    ActorUserId = "postgres-test-user",
                    ActorName = "postgres-test-user",
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();
                materialId = material.Id;
            }

            async Task<(string Token, string SubmissionId)> GetAntiforgeryTokenAsync(HttpClient client)
            {
                var page = await client.GetAsync("/Emprestimos");
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                var html = await page.Content.ReadAsStringAsync();
                var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
                var submissionKey = Regex.Match(html, @"name=""LoanSubmissionId""[^>]*value=""([^""]+)""");
                Assert.True(token.Success, "The loans page should emit an antiforgery token.");
                Assert.True(submissionKey.Success, "The loan form should emit a submission key.");
                return (token.Groups[1].Value, submissionKey.Groups[1].Value);
            }

            var firstTokenTask = GetAntiforgeryTokenAsync(firstClient);
            var secondTokenTask = GetAntiforgeryTokenAsync(secondClient);
            await Task.WhenAll(firstTokenTask, secondTokenTask);
            var firstForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["MaterialId"] = materialId.ToString(),
                ["BorrowerName"] = "Concorrente A",
                ["Quantity"] = "1",
                ["Reference"] = "PG-RACE-A",
                ["LoanSubmissionId"] = firstTokenTask.Result.SubmissionId,
                ["__RequestVerificationToken"] = firstTokenTask.Result.Token
            });
            var secondForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["MaterialId"] = materialId.ToString(),
                ["BorrowerName"] = "Concorrente B",
                ["Quantity"] = "1",
                ["Reference"] = "PG-RACE-B",
                ["LoanSubmissionId"] = secondTokenTask.Result.SubmissionId,
                ["__RequestVerificationToken"] = secondTokenTask.Result.Token
            });
            using (firstForm)
            using (secondForm)
            {
                var responses = await Task.WhenAll(
                    firstClient.PostAsync("/Emprestimos?handler=Loan", firstForm),
                    secondClient.PostAsync("/Emprestimos?handler=Loan", secondForm));
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Redirect);
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
                var conflict = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
                Assert.Contains("O estoque foi alterado por outra operação.",
                    System.Net.WebUtility.HtmlDecode(await conflict.Content.ReadAsStringAsync()));
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var loans = await verifyDb.InventoryLoans.Where(loan => loan.MaterialId == materialId).ToListAsync();
            Assert.Single(loans);
            var movements = await verifyDb.InventoryMovements.Where(movement => movement.MaterialId == materialId).ToListAsync();
            Assert.Equal(2, movements.Count);
            Assert.Equal(0m, movements.Sum(movement => movement.Type switch
            {
                InventoryMovementType.Entry or InventoryMovementType.Return => movement.Quantity,
                InventoryMovementType.Loan or InventoryMovementType.Consumption => -movement.Quantity,
                InventoryMovementType.Adjustment => movement.Quantity,
                _ => 0m
            }));
        }
        finally
        {
            await PostgreSqlIntegrationSupport.DeleteMaterialScenarioAsync(factory.Services, materialId, categoryId);
        }
    }
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SISTEMAMATERIAIS_POSTGRES_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Opt-in PostgreSQL integration test; requires an isolated database and applied migrations.";
        }
    }
}

internal sealed class PostgreSqlTestAuthenticationHandler(
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
