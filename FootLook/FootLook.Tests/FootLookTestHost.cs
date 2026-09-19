using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FootLook.Core.Extensions;
using FootLook.Core.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FootLook.Tests;

/// <summary>
/// A minimal in-memory host (TestServer, no ports) with FootLook wired up the way a real
/// app does it, plus a few trivial host endpoints to generate observable traffic.
/// </summary>
public sealed class FootLookTestHost : IAsyncDisposable
{
    public const string Password = "Passw0rd!123";

    private readonly WebApplication _app;
    private readonly string _userStorePath;

    public HttpClient Client { get; }
    public TestServer Server { get; }
    public FootLookOptions Options { get; }
    public IServiceProvider Services => _app.Services;

    private FootLookTestHost(WebApplication app, string userStorePath)
    {
        _app = app;
        _userStorePath = userStorePath;
        Server = app.GetTestServer();
        Client = app.GetTestClient();
        Options = app.Services.GetRequiredService<FootLookOptions>();
    }

    public static async Task<FootLookTestHost> StartAsync(Action<FootLookOptions>? configure = null, Action<IServiceCollection>? configureServices = null)
    {
        var userStorePath = Path.Combine(Path.GetTempPath(), $"footlook-tests-{Guid.NewGuid():N}.json");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSignalR();
        // Lets a test swap services (e.g. the Microsoft account store) before AddFootLook adds its TryAdd defaults.
        configureServices?.Invoke(builder.Services);
        builder.Services.AddFootLook(o =>
        {
            o.UserStorePath = userStorePath;
            o.EndpointBasePath = "/footlook";
            o.TokenSigningKey = "unit-test-signing-key-0123456789-abcdef";
            configure?.Invoke(o);
        });

        var app = builder.Build();
        app.UseFootLook();
        app.MapFootLookEndpoints(app.Services.GetRequiredService<FootLookOptions>());

        app.MapGet("/hello", () => Results.Ok(new { message = "hello" }));
        app.MapGet("/other", () => Results.Ok(new { message = "other" }));
        app.MapPost("/echo", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            return Results.Text(await reader.ReadToEndAsync(), "application/json");
        });

        await app.StartAsync();
        return new FootLookTestHost(app, userStorePath);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();

        foreach (var path in new[] { _userStorePath, _userStorePath + ".tmp" })
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? token = null) => SendAsync(HttpMethod.Get, path, token);

    public Task<HttpResponseMessage> PostAsync(string path, string? token = null, object? body = null) => SendAsync(HttpMethod.Post, path, token, body);

    public Task<HttpResponseMessage> RegisterAsync(string email, string password = Password, string? displayName = null) =>
        PostAsync("/footlook/auth/register", body: new { email, password, displayName });

    public Task<HttpResponseMessage> LoginAsync(string email, string password = Password) =>
        PostAsync("/footlook/auth/login", body: new { email, password });

    public async Task<string> RegisterAndLoginAsync(string email, string password = Password)
    {
        var register = await RegisterAsync(email, password);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        return await LoginTokenAsync(email, password);
    }

    public async Task<string> LoginTokenAsync(string email, string password = Password)
    {
        var login = await LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>Paths of every capture the token's account can see (newest first).</summary>
    public async Task<List<string>> GetCapturePathsAsync(string token)
    {
        var response = await GetAsync("/footlook/captures?pageSize=100", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("results").EnumerateArray()
            .Select(e => e.GetProperty("path").GetString()!)
            .ToList();
    }

    public async Task<Guid?> GetCaptureIdAsync(string token, string path)
    {
        var response = await GetAsync("/footlook/captures?pageSize=100", token);
        using var doc = await ReadJsonAsync(response);
        foreach (var e in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            if (e.GetProperty("path").GetString() == path)
            {
                return e.GetProperty("id").GetGuid();
            }
        }

        return null;
    }

    /// <summary>Polls until the account's capture list contains the path (captures are written asynchronously).</summary>
    public Task WaitForCaptureAsync(string token, string path) =>
        WaitUntilAsync(async () => (await GetCapturePathsAsync(token)).Contains(path), $"capture of {path}");

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string description, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out after {timeoutMs}ms waiting for: {description}");
    }
}
