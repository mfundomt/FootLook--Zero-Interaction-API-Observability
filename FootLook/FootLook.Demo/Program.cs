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
builder.Services.AddSwaggerGen(swagger =>
{
    // Lets Swagger UI's Authorize button drive FootLook's own bearer-token auth: paste
    // the token from POST /footlook/auth/login (or GET /footlook/auth/me) and every
    // "Try it out" call on a FootLook endpoint carries it automatically.
    swagger.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "FootLook bearer token, obtained from POST /footlook/auth/login. Logging in also starts observation of this API."
    });
    // Applied per-operation (not globally) so /footlook/health, /footlook/auth/register and
    // /footlook/auth/login - the routes that are genuinely unauthenticated - don't show a misleading padlock
    // in the UI for a requirement that was never actually enforced on them.
    swagger.OperationFilter<FootLookSwaggerSecurityFilter>();
});
builder.Services.AddHttpContextAccessor();
//Register SignalR for real-time updates
builder.Services.AddSignalR();

// Demo-only CORS so a separately-hosted frontend (different port/origin) can be
// driven through "Connect to site": it needs to send the X-Footlook-Session-Id /
// X-Footlook-Tab-Id headers FootLook.Connect attaches, which requires an explicit
// AllowedHeaders entry (not just AllowAnyOrigin) for the CORS preflight to pass.
// A real deployment should scope this to its actual frontend origin(s) - this is
// permissive only because the demo's whole point is to be poked at from anywhere.
builder.Services.AddCors(cors =>
{
    cors.AddPolicy("FootLookDemoFrontend", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});
builder.Services.AddFootLookMongoRepository();

// Microsoft (Entra ID) sign-in accounts live in Azure SQL. The connection string is a SECRET and is
// never committed: set ConnectionStrings:FootLookAccounts via user-secrets locally, or the App Service
// application setting ConnectionStrings__FootLookAccounts. Without it the host still starts and
// POST /footlook/auth/microsoft answers 503 accounts_unavailable.
builder.Services.AddFootLookSqlAccounts(builder.Configuration);


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

    // Ignore this app's own endpoints (dashboard/swagger/self) from being captured.
    // Bot/crawler/scanner noise (robots.txt, sitemap.xml, wp-*, etc.) lives in
    // appsettings.json's FootLook:IgnoredPathPrefixes instead, below - that's the
    // list meant to grow per-environment without a redeploy.
    options.IgnoredPaths.Add("/footlook");
    options.IgnoredPaths.Add("/footlook.html");
    options.IgnoredPaths.Add("/footlook-login.html");
    options.IgnoredPaths.Add("/footlook-connect.js");
    options.IgnoredPaths.Add("/");
    options.IgnoredPaths.Add("/footlook/pause");
    options.IgnoredPaths.Add("/footlook/resume");
    options.IgnoredPaths.Add("/swagger");
    options.IgnoredPaths.Add("/favicon.ico");
    options.IgnoredPaths.Add("/.well-known");

    // HTTP methods FootLook will capture on the host's OWN endpoints (this has nothing
    // to do with CORS - it's consumed by ShadowMiddleware.ShouldCaptureMethod to decide
    // which requests get observed at all). DELETE is deliberately excluded below.
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

    // No credentials are configured here: developers create an account with
    // POST /footlook/auth/register and log in with POST /footlook/auth/login, and that
    // login is what turns observation on. The first account registered is the admin.
    // Set FootLook:AllowRegistration=false once your accounts exist to close sign-up.
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
app.UseCors("FootLookDemoFrontend");

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

// Exercises FootLook's response-body truncation: returns well over MaxBodyLength
// (1MB configured above) so captures should show a truncated body with the
// "...(truncated)" marker instead of buffering the whole thing unbounded.
app.MapGet("/large", () =>
{
    var chunk = new string('x', 1024); // 1KB
    var body = string.Concat(Enumerable.Repeat(chunk, 2048)); // ~2MB
    return Results.Text(body, "text/plain");
});

// Exercises true response streaming: writes five chunks with a real delay and an
// explicit flush between each. Before the CappedTeeStream fix, FootLook buffered
// the entire response until the handler finished, so a client would see nothing
// until all ~2.5s had elapsed regardless of these flushes. After the fix, each
// chunk should reach the client as it's written - verify with:
//   curl -w "ttfb=%{time_starttransfer}s total=%{time_total}s\n" -o /dev/null -s http://localhost:5042/stream
// ttfb should be near-instant; total should be ~2.5s.
app.MapGet("/stream", async (HttpContext context) =>
{
    context.Response.ContentType = "text/plain";
    for (var i = 0; i < 5; i++)
    {
        await context.Response.WriteAsync($"chunk {i}\n");
        await context.Response.Body.FlushAsync();
        await Task.Delay(500);
    }
});

// The /footlook/live hub is mapped inside MapFootLookEndpoints (above) so its
// authentication requirement can never drift from the REST endpoints' own.

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

/// <summary>
/// Marks Swagger operations as requiring the Bearer scheme, but only the ones FootLook
/// itself actually protects (paths under /footlook, minus the genuinely
/// unauthenticated routes) - this demo's own business endpoints (/, /test2, /slow,
/// /error) were never behind FootLook's auth and must not show a padlock either, or the
/// Swagger doc would claim a requirement that isn't real in either direction.
/// </summary>
public class FootLookSwaggerSecurityFilter : Swashbuckle.AspNetCore.SwaggerGen.IOperationFilter
{
    private const string ProtectedPrefix = "footlook/";

    private static readonly string[] ExemptRelativePaths =
    {
        "footlook/health",
        "footlook/auth/register",
        "footlook/auth/login",
        "footlook/auth/microsoft",
        "footlook/auth/config",
        "footlook/auth/exchange"
    };

    public void Apply(Microsoft.OpenApi.Models.OpenApiOperation operation, Swashbuckle.AspNetCore.SwaggerGen.OperationFilterContext context)
    {
        var relativePath = context.ApiDescription.RelativePath?.TrimEnd('/') ?? string.Empty;

        if (!relativePath.StartsWith(ProtectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (ExemptRelativePaths.Any(exempt => relativePath.Equals(exempt, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        operation.Security = new List<Microsoft.OpenApi.Models.OpenApiSecurityRequirement>
        {
            new()
            {
                {
                    new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                    {
                        Reference = new Microsoft.OpenApi.Models.OpenApiReference
                        {
                            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    Array.Empty<string>()
                }
            }
        };
    }
}

