using System.Net;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace SistemaMateriais.Web.Tests;

public sealed class LoginAccessibilityTests : IAsyncLifetime
{
    private const string TestAdminPassword = "Only-For-Local-Tests!234";
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _databasePath = null!;
    private readonly ConcurrentQueue<string> _capturedLogs = new();

    public Task InitializeAsync()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var scratch = Path.Combine(localData, "hermes", "cache", "scratch");
        Directory.CreateDirectory(scratch);
        _databasePath = Path.Combine(scratch, $"materials-login-a11y-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(_capturedLogs)));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["ConnectionStrings:DefaultConnection"] = $"Data Source={_databasePath}",
                    ["BootstrapAdmin:UserName"] = "login-a11y-admin",
                    ["BootstrapAdmin:Password"] = TestAdminPassword
                }));
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task LoginValidationErrorsAreProgrammaticallyAssociatedWithTheirInputs()
    {
        using var page = await _client.GetAsync("/Identity/Account/Login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var pageHtml = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(pageHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The login form should emit an antiforgery token.");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.UserName"] = "",
            ["Input.Password"] = "",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        using var response = await _client.PostAsync("/Identity/Account/Login", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains("aria-describedby=\"username-error\"", html);
        Assert.Contains("id=\"username-error\"", html);
        Assert.Contains("aria-describedby=\"password-error\"", html);
        Assert.Contains("id=\"password-error\"", html);
        Assert.Contains("Informe o usuário.", html);
        Assert.Contains("Informe a senha.", html);
        Assert.Matches(@"<input\b(?=[^>]*\bid=""Input_UserName"")(?=[^>]*\baria-invalid=""true"")[^>]*>", html);
        Assert.Matches(@"<input\b(?=[^>]*\bid=""Input_Password"")(?=[^>]*\baria-invalid=""true"")[^>]*>", html);
    }

    [Fact]
    public async Task LoginMutedTextUsesTheAccessibleThemeContrastToken()
    {
        using var response = await _client.GetAsync("/css/site.css");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var css = await response.Content.ReadAsStringAsync();

        Assert.Contains("--muted: hsl(215, 16%, 44%);", css);
    }

    [Fact]
    public async Task LoginTextFieldsHaveAVisibleKeyboardFocusIndicator()
    {
        using var response = await _client.GetAsync("/css/site.css");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var css = await response.Content.ReadAsStringAsync();

        Assert.Contains(".form-control:focus-visible { outline: 3px solid var(--brand); outline-offset: 2px;", css);
    }

    [Fact]
    public async Task ValidCredentialsCreateSecureSessionAndOpenTheProtectedDashboard()
    {
        using var page = await _client.GetAsync("/Identity/Account/Login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The login form should emit an antiforgery token.");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.UserName"] = "login-a11y-admin",
            ["Input.Password"] = TestAdminPassword,
            ["Input.RememberMe"] = "false",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        using var response = await _client.PostAsync("/Identity/Account/Login", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join("\n", values)
            : string.Empty;
        Assert.Contains(".AspNetCore.Identity.Application=", cookies);
        Assert.Contains("secure", cookies, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookies, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookies, StringComparison.OrdinalIgnoreCase);

        using var dashboard = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("Painel", await dashboard.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RepeatedInvalidPasswordsLockOutTheAccountAndBlockAValidSignIn()
    {
        using var page = await _client.GetAsync("/Identity/Account/Login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The login form should emit an antiforgery token.");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.UserName"] = "login-a11y-admin",
                ["Input.Password"] = "Wrong-Password!987",
                ["Input.RememberMe"] = "false",
                ["__RequestVerificationToken"] = token.Groups[1].Value
            });
            using var response = await _client.PostAsync("/Identity/Account/Login", form);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser>>();
        var user = await userManager.FindByNameAsync("login-a11y-admin");
        Assert.NotNull(user);
        Assert.True(user.LockoutEnabled);
        Assert.True(await userManager.IsLockedOutAsync(user));
        Assert.InRange(
            user.LockoutEnd!.Value - DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(4),
            TimeSpan.FromMinutes(5));

        using var validForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.UserName"] = "login-a11y-admin",
            ["Input.Password"] = TestAdminPassword,
            ["Input.RememberMe"] = "false",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        using var blockedSignIn = await _client.PostAsync("/Identity/Account/Login", validForm);

        Assert.Equal(HttpStatusCode.OK, blockedSignIn.StatusCode);
        Assert.Contains("Usuário ou senha inválidos.", WebUtility.HtmlDecode(await blockedSignIn.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task FailedLoginDoesNotWriteSubmittedPasswordToApplicationLogs()
    {
        const string passwordSentinel = "Never-Log-This-Password!492";
        using var page = await _client.GetAsync("/Identity/Account/Login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(token.Success, "The login form should emit an antiforgery token.");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.UserName"] = "nonexistent-log-test-user",
            ["Input.Password"] = passwordSentinel,
            ["Input.RememberMe"] = "false",
            ["__RequestVerificationToken"] = token.Groups[1].Value
        });
        using var response = await _client.PostAsync("/Identity/Account/Login", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(_capturedLogs, message => message.Contains(passwordSentinel, StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            void ILogger.Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
            }
        }
    }
}
