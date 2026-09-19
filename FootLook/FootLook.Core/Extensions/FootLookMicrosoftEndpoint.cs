using System.Text.Json;
using FootLook.Core.Models;
using FootLook.Core.Options;
using FootLook.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FootLook.Core.Extensions
{
    /// <summary>
    /// POST {EndpointBasePath}/auth/microsoft - the endpoint the marketing site's Microsoft
    /// sign-in posts to. Unauthenticated (it is how a token is obtained), like /auth/login.
    /// <code>
    /// request:  { "idToken": "...", "mode": "login" | "register", "acceptedTerms": true }
    /// 200       { "token", "expiresAtUtc", "isAdmin", "user": { "id", "email", "displayName" } }
    /// 400       { "error": "invalid_request" | "terms_not_accepted" }
    /// 401       { "error": "invalid_token" }
    /// 403       { "error": "registration_closed" }
    /// 404       { "error": "account_not_found" }
    /// 503       { "error": "accounts_unavailable" | "token_validation_unavailable" }
    /// </code>
    /// The 503s are infrastructure failures (no account store configured or the database is
    /// unreachable; Microsoft's signing keys could not be fetched) and are not part of the
    /// SPA's contract for user-facing errors.
    /// </summary>
    internal static class FootLookMicrosoftEndpoint
    {
        // An ID token is a few KB; anything much larger is not a legitimate request.
        private const int MaxBodyBytes = 32 * 1024;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        internal static IEndpointRouteBuilder MapFootLookMicrosoftSignIn(this IEndpointRouteBuilder endpoints, string prefix)
        {
            endpoints.MapPost($"{prefix}/auth/microsoft", async (
                HttpContext http,
                FootLookOptions options,
                MicrosoftIdTokenValidator validator,
                FootLookTokenService tokenService) =>
            {
                var cancellationToken = http.RequestAborted;

                var request = await ReadRequestAsync(http.Request, cancellationToken);
                var register = string.Equals(request?.Mode, "register", StringComparison.OrdinalIgnoreCase);
                var login = string.Equals(request?.Mode, "login", StringComparison.OrdinalIgnoreCase);
                if (request is null || string.IsNullOrWhiteSpace(request.IdToken) || !(register || login))
                {
                    return Error(StatusCodes.Status400BadRequest, "invalid_request");
                }

                if (register && request.AcceptedTerms != true)
                {
                    return Error(StatusCodes.Status400BadRequest, "terms_not_accepted");
                }

                var validation = await validator.ValidateAsync(request.IdToken, cancellationToken);
                if (validation.Unavailable)
                {
                    return Error(StatusCodes.Status503ServiceUnavailable, "token_validation_unavailable");
                }

                if (validation.Identity is null)
                {
                    // Deliberately no detail: the reason is logged, not returned.
                    return Error(StatusCodes.Status401Unauthorized, "invalid_token");
                }

                var accounts = http.RequestServices.GetService<IFootLookMicrosoftAccountStore>();
                if (accounts is null)
                {
                    return Error(StatusCodes.Status503ServiceUnavailable, "accounts_unavailable");
                }

                MicrosoftSignInResult result;
                try
                {
                    result = await accounts.SignInAsync(
                        validation.Identity,
                        createIfMissing: register && options.AllowRegistration,
                        acceptedTerms: register,
                        ipAddress: http.Connection.RemoteIpAddress?.ToString(),
                        userAgent: http.Request.Headers.UserAgent.ToString(),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    http.RequestServices.GetService<ILoggerFactory>()?
                        .CreateLogger("FootLook.MicrosoftSignIn")
                        .LogError(ex, "Microsoft sign-in failed: the account store threw.");
                    return Error(StatusCodes.Status503ServiceUnavailable, "accounts_unavailable");
                }

                if (result.User is null)
                {
                    // Login for an identity nobody registered, or a brand-new registration
                    // while sign-up is closed. (An existing identity always signs in, in
                    // either mode.)
                    return login
                        ? Error(StatusCodes.Status404NotFound, "account_not_found")
                        : Error(StatusCodes.Status403Forbidden, "registration_closed");
                }

                var user = result.User;
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

        private static IResult Error(int statusCode, string error) => Results.Json(new { error }, statusCode: statusCode);

        private static async Task<FootLookMicrosoftSignInRequest?> ReadRequestAsync(HttpRequest request, CancellationToken cancellationToken)
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
                    : JsonSerializer.Deserialize<FootLookMicrosoftSignInRequest>(buffer.ToArray(), JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
