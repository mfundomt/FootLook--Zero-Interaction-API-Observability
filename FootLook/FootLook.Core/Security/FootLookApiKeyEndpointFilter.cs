using FootLook.Core.Options;
using Microsoft.AspNetCore.Http;

namespace FootLook.Core.Security
{
    /// <summary>
    /// Enforces FootLookOptions.ApiKeys on a route/group. Attach with requireAdmin=true to
    /// gate host-wide actions (pause/resume, privacy-audit clear, self-heal/setup) behind an
    /// admin-flagged key, separately from the baseline key required to read/clear a caller's
    /// own capture scope.
    /// </summary>
    public class FootLookApiKeyEndpointFilter : IEndpointFilter
    {
        private readonly FootLookOptions _options;
        private readonly bool _requireAdmin;

        public FootLookApiKeyEndpointFilter(FootLookOptions options, bool requireAdmin)
        {
            _options = options;
            _requireAdmin = requireAdmin;
        }

        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            if (!_options.RequireApiKey)
            {
                return await next(context);
            }

            var httpContext = context.HttpContext;
            var providedKey = httpContext.Request.Headers[_options.ApiKeyHeaderName].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(providedKey))
            {
                providedKey = httpContext.Request.Query[_options.ApiKeyQueryParameterName].FirstOrDefault();
            }

            var matchedKey = FootLookApiKeyMatcher.Match(_options, providedKey);

            if (matchedKey is null)
            {
                return Results.Json(
                    new { message = "A valid FootLook API key is required." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (_requireAdmin && !matchedKey.IsAdmin)
            {
                return Results.Json(
                    new { message = "This action requires an admin FootLook API key." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            httpContext.Items[FootLookApiKeyContext.HttpContextItemKey] = matchedKey;

            return await next(context);
        }
    }

    public static class FootLookApiKeyContext
    {
        public const string HttpContextItemKey = "FootLook.ApiKey";
    }
}
