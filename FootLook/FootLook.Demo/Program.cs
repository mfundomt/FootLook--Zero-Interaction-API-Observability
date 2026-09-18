using FootLook.Core.Services;
using FootLook.Core.Extensions;
using FootLook.Core.Options;
using FootLook.Core.Hubs;
using FootLook.Data.Repositories;
using FootLook.Data.Extensions;
using FootLook.Core.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
;
builder.Services.AddHttpContextAccessor();
//Register SignalR for real-time updates
builder.Services.AddSignalR();
builder.Services.AddFootLookMongoRepository();


builder.Services.AddFootLook(options =>
{
    builder.Configuration.GetSection("FootLook").Bind(options);

    options.CaptureRequestBody = true;
    options.CaptureResponseBody = true;
    options.MaxBodyLength = 1024 * 1024;
    options.EndpointBasePath = "/footlook";
    options.QueCapacity = 10_000;
    options.Enabled = true;
    options.SamplingRate = 1.0;

    options.ServiceName = "FootLook.Demo";
    options.EnvironmentName = builder.Environment.EnvironmentName;

    // Ignore certain paths from being captured
    options.IgnoredPaths.Add("/footlook");
    options.IgnoredPaths.Add("/footlook.html");
    options.IgnoredPaths.Add("/");
    options.IgnoredPaths.Add("/robots");
    options.IgnoredPaths.Add("/footlook/pause");
    options.IgnoredPaths.Add("/footlook/resume");
    options.IgnoredPaths.Add("/swagger");
    options.IgnoredPaths.Add("/favicon.ico");
    options.IgnoredPaths.Add("/.well-known");

    // Allow CORS for the FootLook endpoints
    options.AllowedMethods.Add("GET");
    options.AllowedMethods.Add("POST");
    options.AllowedMethods.Add("PATCH");
    options.AllowedMethods.Add("PUT");
    // Noise/scanner filtering from config (applied in all environments).
    var ignoredPrefixes =
        builder.Configuration
            .GetSection("FootLook:IgnoredPathPrefixes")
            .Get<string[]>() ?? Array.Empty<string>();

    foreach (var prefix in ignoredPrefixes)
    {
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            options.IgnoredPaths.Add(prefix.Trim());
        }
    }
//  options.AllowedMethods.Add("DELETE");

    // Dev-only default keys so the bundled demo/dashboard works out of the box.
    // A real deployment should set FootLook:ApiKeys via configuration/user-secrets/
    // environment variables instead of hardcoding keys in source.
    var configuredKeys = builder.Configuration.GetSection("FootLook:ApiKeys").Get<List<FootLookApiKey>>();
    options.ApiKeys = configuredKeys is { Count: > 0 }
        ? configuredKeys
        : new List<FootLookApiKey>
        {
            new("footlook-dev-key", IsAdmin: false, Label: "demo-default"),
            new("footlook-dev-admin-key", IsAdmin: true, Label: "demo-default-admin"),
        };
});

// TODO: Add MongoDB repository registration here if needed
//builder.Services.AddFootLookMongoRepository();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

#region FootLook Middleware Flow (Manual for testing)
//var sink = new InMemorySink();

//await sink.WriteAsync(new CapturedRequest
//{
//    //Id = Guid.NewGuid(),
//    Method = "GET",
//    Path = "/api/values",
//    StatusCode = 200,
//    //Headers = new Dictionary<string, string>()
//});

//var captures = sink.GetAll();

//Console.WriteLine($"Captured {captures.Count} requests.");
#endregion

//app.UseMiddleware<ShadowMiddleware>();

app.UseFootLook();


var footlookOptions = app.Services.GetRequiredService<FootLookOptions>();
app.MapFootLookEndpoints(footlookOptions);

// Pause/resume live here: only the authenticated /footlook/captures/pause and
// /footlook/captures/resume routes (mapped by MapFootLookEndpoints, admin key
// required). This demo used to also expose unauthenticated top-level /pause and
// /resume routes wired to CaptureEvents.Pause/Resume - a second, weaker "pause"
// that only silenced the console log subscriber below without actually stopping
// capture, sinks, or the live feed. That duplicate control surface is removed:
// it bypassed the auth just added and did not do what its name implied.

app.MapGet("/", () =>
{
    return "FootLook Running";
});

app.MapPost("/test2", (TestRequest request) =>
{
    return Results.Ok(new
    {
        Message = "Controller received body",
        Body = request
    });
});

app.MapGet("/slow", async () =>
{
   await Task.Delay(2000);
    return "This is a slow endpoint";
});

app.MapGet("/error", () =>
{
   throw new Exception("simulated failure");
});

app.MapHub<CaptureHub>("/footlook/live");

//app.MapGet("/mongo-test",
//    async (ICaptureRepository repository) =>
//    {
//        var captures =
//            await repository.GetRecentAsync(10);

//        return Results.Ok(captures);
//    });


var events = app.Services.GetRequiredService<CaptureEvents>();

events.OnRequestCaptured += capture =>
{
    Console.WriteLine(
        $"[LIVE] {capture.Method} {capture.Path} " +
        $"{capture.StatusCode} " +
        $"{capture.DurationMs}ms");
};

app.Run();


public class TestRequest
{
    public string Message { get; set; } = string.Empty;
}

