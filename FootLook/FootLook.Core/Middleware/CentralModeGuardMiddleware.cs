using FootLook.Core.Options;
using Microsoft.AspNetCore.Http;

namespace FootLook.Core.Middleware
{
    /// <summary>
    /// In central mode (FootLook:Central:ProjectId set) the host's own account routes must not
    /// exist: otherwise anyone who can reach the host could create an account and watch its
    /// traffic. This answers 404 { "error": "disabled_in_central_mode" } to them.
    /// <para>
    /// It is middleware rather than an endpoint filter on purpose: it runs before routing and model
    /// binding, so even a malformed request body gets the same 404, and a route added later cannot
    /// forget it. Without a ProjectId it does nothing.
    /// </para>
    /// </summary>
    public sealed class CentralModeGuardMiddleware
    {
        private static readonly string[] LocalAccountRoutes = { "/auth/register", "/auth/login", "/auth/microsoft" };

        private readonly RequestDelegate _next;
        private readonly FootLookOptions _options;

        public CentralModeGuardMiddleware(RequestDelegate next, FootLookOptions options)
        {
            _next = next;
            _options = options;
        }

        public Task InvokeAsync(HttpContext context)
        {
            if (_options.Central.IsEnabled && IsLocalAccountRoute(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return context.Response.WriteAsJsonAsync(new { error = "disabled_in_central_mode" });
            }

            return _next(context);
        }

        private bool IsLocalAccountRoute(PathString path)
        {
            var prefix = _options.EndpointBasePath.TrimEnd('/');
            var value = (path.Value ?? string.Empty).TrimEnd('/');
            foreach (var route in LocalAccountRoutes)
            {
                if (string.Equals(value, prefix + route, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
