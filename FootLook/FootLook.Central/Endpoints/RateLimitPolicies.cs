using System.Threading.RateLimiting;
using FootLook.Central.Options;
using Microsoft.AspNetCore.RateLimiting;

namespace FootLook.Central.Endpoints;

/// <summary>
/// The built-in ASP.NET rate limiter, one fixed one-minute window per caller: sign-in per client IP (nobody is
/// signed in yet), redeem and connect per user (falling back to the IP if somehow unauthenticated).
/// The limiter runs after authentication so the user is known.
/// </summary>
internal static class RateLimitPolicies
{
    public const string SignIn = "signin";
    public const string Redeem = "redeem";
    public const string Connect = "connect";

    public static void Configure(RateLimiterOptions limiter, CentralRateLimits limits)
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.OnRejected = async (context, cancellationToken) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.HttpContext.Response.WriteAsJsonAsync(new { error = "rate_limited" }, cancellationToken);
        };

        limiter.AddPolicy(SignIn, http => Window(ClientIp(http), limits.SignInPerMinute));
        limiter.AddPolicy(Redeem, http => Window(UserOrIp(http), limits.RedeemPerMinute));
        limiter.AddPolicy(Connect, http => Window(UserOrIp(http), limits.ConnectPerMinute));
    }

    private static RateLimitPartition<string> Window(string key, int permitsPerMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permitsPerMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    private static string ClientIp(HttpContext http) => "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    private static string UserOrIp(HttpContext http) =>
        Api.TryGetUserId(http.User, out var userId) ? "user:" + userId.ToString("N") : ClientIp(http);
}
