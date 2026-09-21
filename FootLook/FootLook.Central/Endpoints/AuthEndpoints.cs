using FootLook.Central.Options;
using FootLook.Central.Security;
using FootLook.Core.Models;
using FootLook.Core.Security;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Central.Endpoints;

/// <summary>Sign-in, the caller's own profile, the public key address and a health probe.</summary>
internal static class AuthEndpoints
{
    public const string SiteCorsPolicy = "site";
    public const string OpenCorsPolicy = "open";

    internal static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // POST /auth/microsoft: the same request and error contract the Angular site used against FootLook.Core
        // (FootLookMicrosoftEndpoint), except that the token is the CENTRAL session token.
        endpoints.MapPost("/auth/microsoft", SignInAsync)
            .RequireCors(SiteCorsPolicy)
            .RequireRateLimiting(RateLimitPolicies.SignIn);

        endpoints.MapGet("/me", (Delegate)MeAsync)
            .RequireCors(SiteCorsPolicy)
            .RequireAuthorization();

        endpoints.MapGet("/.well-known/jwks.json", (ISigningKeyProvider keys, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Json(new
            {
                keys = keys.PublicKeys.Select(k => new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = k.KeyId,
                    n = Base64UrlEncoder.Encode(k.Modulus),
                    e = Base64UrlEncoder.Encode(k.Exponent),
                }),
            });
        }).RequireCors(OpenCorsPolicy);

        endpoints.MapGet("/health", () => Results.Json(new { status = "ok" }));

        return endpoints;
    }

    private static async Task<IResult> SignInAsync(
        HttpContext http,
        CentralOptions options,
        MicrosoftIdTokenValidator validator,
        CentralTokenService tokens,
        ILoggerFactory loggerFactory)
    {
        var cancellationToken = http.RequestAborted;

        var body = await Api.ReadBodyAsync<FootLookMicrosoftSignInRequest>(http.Request, Api.MaxSignInBodyBytes, cancellationToken);
        var request = body.Value;
        var register = string.Equals(request?.Mode, "register", StringComparison.OrdinalIgnoreCase);
        var login = string.Equals(request?.Mode, "login", StringComparison.OrdinalIgnoreCase);
        if (request is null || string.IsNullOrWhiteSpace(request.IdToken) || !(register || login))
        {
            return Api.InvalidRequest();
        }

        if (register && request.AcceptedTerms != true)
        {
            return Api.Error(StatusCodes.Status400BadRequest, "terms_not_accepted");
        }

        var validation = await validator.ValidateAsync(request.IdToken, cancellationToken);
        if (validation.Unavailable)
        {
            return Api.Error(StatusCodes.Status503ServiceUnavailable, "token_validation_unavailable");
        }

        if (validation.Identity is null)
        {
            // Deliberately no detail: the reason is logged (by the validator), not returned.
            return Api.Error(StatusCodes.Status401Unauthorized, "invalid_token");
        }

        var accounts = http.RequestServices.GetService<IFootLookMicrosoftAccountStore>();
        if (accounts is null)
        {
            return Api.Error(StatusCodes.Status503ServiceUnavailable, "accounts_unavailable");
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
            // The type only: an exception's text can carry connection details.
            loggerFactory.CreateLogger("FootLook.Central.SignIn").LogError("Microsoft sign-in failed: the account store threw {ExceptionType}.", ex.GetType().Name);
            return Api.Error(StatusCodes.Status503ServiceUnavailable, "accounts_unavailable");
        }

        if (result.User is null)
        {
            // Login for an identity nobody registered, or a brand-new registration while sign-up is closed.
            // (An existing identity always signs in, in either mode.)
            return login
                ? Api.Error(StatusCodes.Status404NotFound, "account_not_found")
                : Api.Error(StatusCodes.Status403Forbidden, "registration_closed");
        }

        var user = result.User;
        var issued = await tokens.IssueSessionTokenAsync(user, cancellationToken);
        return Results.Ok(new
        {
            token = issued.Token,
            expiresAtUtc = issued.ExpiresAtUtc,
            isAdmin = user.IsAdmin,
            user = new { id = user.Id, email = user.Email, displayName = user.DisplayName },
        });
    }

    private static async Task<IResult> MeAsync(HttpContext http)
    {
        if (!Api.TryGetUserId(http.User, out var userId))
        {
            return Api.Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }

        var accounts = http.RequestServices.GetService<IFootLookMicrosoftAccountStore>();
        if (accounts is null)
        {
            return Api.Error(StatusCodes.Status503ServiceUnavailable, "accounts_unavailable");
        }

        // Fresh from the database, not from the token: a token can outlive a change of name or admin flag.
        var user = await accounts.FindByIdAsync(userId.ToString("N"), http.RequestAborted);
        if (user is null)
        {
            return Api.Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }

        return Results.Ok(new { id = user.Id, email = user.Email, displayName = user.DisplayName, isAdmin = user.IsAdmin });
    }
}
