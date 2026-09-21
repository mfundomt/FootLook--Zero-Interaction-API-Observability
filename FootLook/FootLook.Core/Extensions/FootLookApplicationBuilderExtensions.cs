using Microsoft.AspNetCore.Builder;
using System;
using FootLook.Core.Middleware;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FootLook.Core.Extensions
{
    public static class FootLookApplicationBuilderExtensions
    {
        public static IApplicationBuilder UseFootLook(this IApplicationBuilder app)
        {
            // Wires up FootLook's own bearer-token scheme so the host doesn't have to call
            // UseAuthentication/UseAuthorization itself just to get FootLook's endpoints
            // protected - calling this twice (e.g. the host already has its own auth) is
            // harmless, ASP.NET Core just re-runs the auth middleware.
            // First, so that in central mode the local account routes are gone before anything else
            // (routing, body binding, authentication) sees them. A no-op without a Central ProjectId.
            app.UseMiddleware<CentralModeGuardMiddleware>();

            app.UseAuthentication();
            app.UseAuthorization();

            // Add the FootLookMiddleware to the application's request processing pipeline.
            return app.UseMiddleware<ShadowMiddleware>();
        }
    }
}
