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
            // Add the FootLookMiddleware to the application's request processing pipeline.
            return app.UseMiddleware<ShadowMiddleware>();
        }
    }
}
