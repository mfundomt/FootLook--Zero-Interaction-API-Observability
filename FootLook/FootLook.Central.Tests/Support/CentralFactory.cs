using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FootLook.Central.Data;
using FootLook.Central.Security;
using FootLook.Core.Models;
using FootLook.Core.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FootLook.Central.Tests;

/// <summary>Collects every log line the app writes, so tests can prove no secret reaches a log.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _lines = new();

    public IReadOnlyList<string> Lines
    {
        get { lock (_lines) { return _lines.ToList(); } }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly CapturingLoggerProvider _owner;
        private readonly string _category;

        public CapturingLogger(CapturingLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = $"{logLevel} {_category}: {formatter(state, exception)}{(exception is null ? string.Empty : " " + exception)}";
            lock (_owner._lines)
            {
                _owner._lines.Add(line);
            }
        }
    }
}

/// <summary>
/// The whole central app in memory (TestServer), with a test signing key, fake stores, a test Microsoft
/// validator and a movable clock. Settings and the environment are chosen before the first request.
/// </summary>
public sealed class CentralFactory : WebApplicationFactory<Program>
{
    public RSA Rsa { get; } = RSA.Create(2048);
    public LocalRsaSigningKeyProvider Keys { get; }

    /// <summary>What the app signs with; the test key unless a test swaps it (rotation tests).</summary>
    public ISigningKeyProvider SigningProvider { get; set; }
    public FakeAccountStore Accounts { get; } = new();
    public InMemoryCentralStore Store { get; }
    public ManualTimeProvider Time { get; } = new();
    public MicrosoftTestTokens MsTokens { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();

    public const string Issuer = "https://central.test.example";

    public string EnvironmentName { get; set; } = "Development";

    /// <summary>False lets the app choose its own signing key (from configuration), as it does when deployed.</summary>
    public bool UseTestSigningKey { get; set; } = true;

    /// <summary>False leaves the app's own store wiring (no connection string means no stores).</summary>
    public bool UseFakeStores { get; set; } = true;

    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Central:Issuer"] = Issuer,
        // Far above anything a test does, so only the rate-limit tests ever see a 429.
        ["Central:RateLimits:SignInPerMinute"] = "1000",
        ["Central:RateLimits:RedeemPerMinute"] = "1000",
        ["Central:RateLimits:ConnectPerMinute"] = "1000",
    };

    public CentralFactory(Action<CentralFactory>? configure = null)
    {
        Keys = new LocalRsaSigningKeyProvider(Rsa);
        SigningProvider = Keys;
        Store = new InMemoryCentralStore(Accounts);
        configure?.Invoke(this);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(Logs);
        });

        builder.ConfigureTestServices(services =>
        {
            if (UseTestSigningKey)
            {
                services.AddSingleton(SigningProvider);
            }

            if (UseFakeStores)
            {
                services.AddSingleton<ICentralStore>(Store);
                services.AddSingleton<IFootLookMicrosoftAccountStore>(Accounts);
            }

            services.AddSingleton(MsTokens.CreateValidator());
            services.AddSingleton<TimeProvider>(Time);
        });
    }

    public CentralTokenService Tokens => Services.GetRequiredService<CentralTokenService>();

    /// <summary>A central session token for the user, minted with the test key (no Microsoft sign-in needed).</summary>
    public async Task<string> SessionAsync(FootLookUser user) => (await Tokens.IssueSessionTokenAsync(user)).Token;

    public FootLookUser NewUser(string? name = null, bool isAdmin = false)
    {
        var label = name ?? "user" + Guid.NewGuid().ToString("N")[..8];
        return Accounts.AddUser($"{label}@example.invalid".ToLowerInvariant(), label, isAdmin);
    }

    public static Guid IdOf(FootLookUser user) => Guid.Parse(user.Id);

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? token = null, object? body = null, string? origin = null, string? rawBody = null)
    {
        var client = CreateClient();
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (rawBody is not null)
        {
            request.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");
        }
        else if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request);

        // No cookies anywhere, ever.
        Assert.False(response.Headers.Contains("Set-Cookie"), $"{method} {path} set a cookie.");
        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? token = null) => SendAsync(HttpMethod.Get, path, token);

    public Task<HttpResponseMessage> PostAsync(string path, string? token = null, object? body = null) => SendAsync(HttpMethod.Post, path, token, body);

    public Task<HttpResponseMessage> DeleteAsync(string path, string? token = null) => SendAsync(HttpMethod.Delete, path, token);

    public Task<HttpResponseMessage> PatchAsync(string path, string? token, object body) => SendAsync(HttpMethod.Patch, path, token, body);

    public static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>Asserts the response is exactly <c>{ "error": "code" }</c> with this status.</summary>
    public static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {(int)status} {error} but got {(int)response.StatusCode}: {raw}");
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(new[] { "error" }, doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(error, doc.RootElement.GetProperty("error").GetString());
    }

    /// <summary>Creates a project as this user and returns its id.</summary>
    public async Task<string> CreateProjectAsync(FootLookUser owner, string name = "My API", params string[] returnUrls)
    {
        var response = await PostAsync("/projects", await SessionAsync(owner), new { name, allowedReturnUrls = returnUrls });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await JsonAsync(response);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Creates an invite as this owner and returns its raw code.</summary>
    public async Task<(long Id, string Code)> CreateInviteAsync(FootLookUser owner, string projectId, int maxUses = 1, int expiresInHours = 168)
    {
        var response = await PostAsync($"/projects/{projectId}/invites", await SessionAsync(owner), new { maxUses, expiresInHours });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await JsonAsync(response);
        return (doc.RootElement.GetProperty("id").GetInt64(), doc.RootElement.GetProperty("code").GetString()!);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            MsTokens.Dispose();
            Rsa.Dispose();
        }
    }
}
