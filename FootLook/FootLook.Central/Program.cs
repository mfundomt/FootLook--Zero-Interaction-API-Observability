using FootLook.Central.Data;
using FootLook.Central.Endpoints;
using FootLook.Central.Options;
using FootLook.Central.Security;
using FootLook.Core.Options;
using FootLook.Core.Security;
using FootLook.Data.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// The framework's exception middleware logs the full exception; ours (below) logs only its type, since an
// exception's text can carry connection details.
builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);

// Options are read from configuration when first needed (not here), so everything below sees the final
// configuration, including settings a test host adds.
builder.Services.AddSingleton(provider =>
    provider.GetRequiredService<IConfiguration>().GetSection("Central").Get<CentralOptions>() ?? new CentralOptions());
builder.Services.AddSingleton(provider =>
    provider.GetRequiredService<IConfiguration>().GetSection("FootLook:Microsoft").Get<FootLookMicrosoftOptions>() ?? new FootLookMicrosoftOptions());
builder.Services.AddSingleton(TimeProvider.System);

// The signing key. Resolved once at startup (below) so a missing or unusable key stops the app instead of
// failing the first sign-in.
builder.Services.AddSingleton<ISigningKeyProvider>(provider => SigningKeyProviderFactory.Create(
    provider.GetRequiredService<CentralOptions>(),
    provider.GetRequiredService<IHostEnvironment>(),
    provider.GetRequiredService<ILoggerFactory>().CreateLogger("FootLook.Central.SigningKey")));
builder.Services.AddSingleton<CentralTokenService>();
builder.Services.AddSingleton<RedeemThrottle>();

// Microsoft (Entra ID) ID-token validation and the accounts table are FootLook.Core / FootLook.Data's.
builder.Services.AddSingleton(provider => new MicrosoftIdTokenValidator(
    provider.GetRequiredService<FootLookMicrosoftOptions>(),
    provider.GetService<ILogger<MicrosoftIdTokenValidator>>()));

var connectionString = builder.Configuration.GetConnectionString("FootLookAccounts");
builder.Services.AddFootLookSqlAccounts(connectionString);
if (string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton<ICentralStore, UnavailableCentralStore>();
}
else
{
    builder.Services.AddSingleton<ICentralStore>(provider =>
        new SqlCentralStore(connectionString, provider.GetService<ILogger<SqlCentralStore>>()));
}

// Central session tokens (aud = footlook-central) are the only thing the API accepts. A pass has another
// audience, so it is rejected here; the token comes from the Authorization header only.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<ISigningKeyProvider, CentralOptions>((jwt, keys, central) =>
    {
        var publicKeys = keys.PublicKeys.Select(k => k.ToSecurityKey()).ToList();
        jwt.MapInboundClaims = false;
        jwt.SaveToken = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            // A token names its key by kid; only a published key with that exact kid is tried.
            IssuerSigningKeyResolver = (_, _, kid, _) => publicKeys.Where(k => k.KeyId == kid),
            // Only RS256: never let the token pick another algorithm (or "none").
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
            ValidateIssuer = true,
            ValidIssuer = central.NormalizedIssuer,
            ValidateAudience = true,
            ValidAudience = CentralTokenService.SessionAudience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(60),
        };
        jwt.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            },
        };
    });
builder.Services.AddAuthorization();

// CORS: the marketing site only; no credentials and no cookies at all. The public key is open to anyone.
builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>().Configure<CentralOptions>((cors, central) =>
{
    cors.AddPolicy(AuthEndpoints.SiteCorsPolicy, policy => policy
        .WithOrigins(central.EffectiveAllowedOrigins.ToArray())
        .WithHeaders("content-type", "authorization")
        .WithMethods("GET", "POST", "PATCH", "DELETE", "OPTIONS")
        .SetPreflightMaxAge(TimeSpan.FromHours(1)));
    cors.AddPolicy(AuthEndpoints.OpenCorsPolicy, policy => policy
        .AllowAnyOrigin()
        .WithMethods("GET")
        .SetPreflightMaxAge(TimeSpan.FromHours(1)));
});

builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>().Configure<CentralOptions>((limiter, central) =>
    RateLimitPolicies.Configure(limiter, central.RateLimits));

builder.Services.AddOptions<ForwardedHeadersOptions>().Configure(forwarded =>
{
    // Behind the App Service front end: take the client address and scheme it reports. Only the last
    // hop in X-Forwarded-For is used (ForwardLimit = 1), so a client cannot pick its own address.
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    forwarded.ForwardLimit = 1;
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
});

var app = builder.Build();

try
{
    app.Services.GetRequiredService<ISigningKeyProvider>();
}
catch (SigningKeyConfigurationException ex)
{
    app.Logger.LogCritical("Startup failed: {Message}", ex.Message);
    throw;
}

if (string.IsNullOrWhiteSpace(connectionString))
{
    app.Logger.LogWarning("ConnectionStrings:FootLookAccounts is not set: sign-in answers 503 accounts_unavailable and the project routes 503 service_unavailable.");
}

if (app.Services.GetRequiredService<CentralOptions>().TrustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler(errors => errors.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("FootLook.Central");

    if (error is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
    {
        return;
    }

    var unavailable = error is CentralStoreUnavailableException or SqlException;
    logger.LogError(
        "Request failed with {ExceptionType}{SqlNumber}.",
        error?.GetType().Name,
        error is SqlException sql ? $" (SQL error {sql.Number})" : string.Empty);

    context.Response.StatusCode = unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new { error = unavailable ? "service_unavailable" : "server_error" });
}));

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        // Tokens, passes and invite codes are in these responses: nothing may cache them. (The JWKS sets its own.)
        if (!headers.ContainsKey("Cache-Control"))
        {
            headers["Cache-Control"] = "no-store";
        }

        return Task.CompletedTask;
    });
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapProjectEndpoints();

app.Run();

// Lets the tests start the whole app (WebApplicationFactory<Program>).
public partial class Program;
