using System.Text.Json;
using FootLook.Core.Models;
using FootLook.Core.Options;
using FootLook.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FootLook.Core.Extensions
{
    /// <summary>
    /// The host side of FootLook's central sign-in. Both routes are unauthenticated (they are how a
    /// session is obtained), like /auth/login.
    /// <code>
    /// GET  {EndpointBasePath}/auth/config    200 { "mode": "central" | "local", "projectId", "loginUrl" }
    ///                                            (projectId and loginUrl only in central mode)
    /// POST {EndpointBasePath}/auth/exchange  request: { "pass": "..." }
    ///      200 { "token", "expiresAtUtc", "isAdmin", "user": { "id", "email", "displayName" } }
    ///      400 { "error": "invalid_request" }     401 { "error": "invalid_pass" }
    ///      503 { "error": "central_unavailable" } 404 { "error": "disabled" }  (not in central mode)
    /// </code>
    /// A valid pass becomes a local session: the same FootLookTokenService.IssueToken call the other
    /// sign-in routes use, so the observation session, its scoped captures and clearing on sign-out
    /// behave identically. The pass itself is never stored or logged.
    /// </summary>
    internal static class FootLookCentralEndpoint
    {
        // A pass is a few hundred bytes; anything much larger is not a legitimate request.
        private const int MaxBodyBytes = 16 * 1024;

        // Central accounts are namespaced so they can never collide with a local account id.
        internal const string UserIdPrefix = "central:";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        internal static IEndpointRouteBuilder MapFootLookCentralSignIn(this IEndpointRouteBuilder endpoints, string prefix)
        {
            endpoints.MapGet($"{prefix}/auth/config", (FootLookOptions options) =>
            {
                var central = options.Central;
                return central.IsEnabled
                    ? Results.Ok(new { mode = "central", projectId = central.ProjectId, loginUrl = central.LoginUrl })
                    : Results.Ok(new { mode = "local" });
            });

            endpoints.MapPost($"{prefix}/auth/exchange", async (
                HttpContext http,
                FootLookOptions options,
                IPassValidator validator,
                PassReplayCache replays,
                FootLookTokenService tokenService) =>
            {
                if (!options.Central.IsEnabled)
                {
                    return Error(StatusCodes.Status404NotFound, "disabled");
                }

                var cancellationToken = http.RequestAborted;

                var request = await ReadRequestAsync(http.Request, cancellationToken);
                if (request is null || string.IsNullOrWhiteSpace(request.Pass))
                {
                    return Error(StatusCodes.Status400BadRequest, "invalid_request");
                }

                var validation = await validator.ValidateAsync(request.Pass, cancellationToken);
                if (validation.Unavailable)
                {
                    return Error(StatusCodes.Status503ServiceUnavailable, "central_unavailable");
                }

                if (validation.Pass is not { } pass)
                {
                    // Deliberately no detail: the reason is logged by the validator, not returned.
                    return Error(StatusCodes.Status401Unauthorized, "invalid_pass");
                }

                // Single use. Kept until the pass could no longer pass validation (expiry + skew),
                // plus a second of margin.
                var skew = TimeSpan.FromSeconds(Math.Clamp(options.Central.ClockSkewSeconds, 0, 60));
                if (!replays.TryRegister(pass.JwtId, pass.ExpiresAtUtc + skew + TimeSpan.FromSeconds(1)))
                {
                    return Error(StatusCodes.Status401Unauthorized, "invalid_pass");
                }

                var user = new FootLookUser(
                    Id: UserIdPrefix + pass.Subject,
                    Email: pass.Email,
                    DisplayName: pass.DisplayName,
                    PasswordHash: string.Empty,
                    IsAdmin: pass.IsOwner,
                    CreatedAtUtc: DateTime.UtcNow);

                var (token, expiresAtUtc, _) = tokenService.IssueToken(user);
                return Results.Ok(new
                {
                    token,
                    expiresAtUtc,
                    isAdmin = user.IsAdmin,
                    user = new { id = user.Id, email = user.Email, displayName = user.DisplayName }
                });
            });

            return endpoints;
        }

        /// <summary>
        /// Second line of defence for the local account routes (the first is
        /// <see cref="Middleware.CentralModeGuardMiddleware"/>, which runs even earlier): in central
        /// mode the handler is never reached.
        /// </summary>
        internal static RouteHandlerBuilder DisabledInCentralMode(this RouteHandlerBuilder route) =>
            route.AddEndpointFilter(async (context, next) =>
                context.HttpContext.RequestServices.GetRequiredService<FootLookOptions>().Central.IsEnabled
                    ? Error(StatusCodes.Status404NotFound, "disabled_in_central_mode")
                    : await next(context));

        private static IResult Error(int statusCode, string error) => Results.Json(new { error }, statusCode: statusCode);

        private static async Task<FootLookCentralExchangeRequest?> ReadRequestAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            if (request.ContentLength is > MaxBodyBytes)
            {
                return null;
            }

            try
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
                {
                    if (buffer.Length + read > MaxBodyBytes)
                    {
                        return null;
                    }

                    buffer.Write(chunk, 0, read);
                }

                return buffer.Length == 0
                    ? null
                    : JsonSerializer.Deserialize<FootLookCentralExchangeRequest>(buffer.ToArray(), JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
