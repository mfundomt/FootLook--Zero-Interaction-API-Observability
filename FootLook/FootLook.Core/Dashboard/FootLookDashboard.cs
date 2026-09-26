using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FootLook.Core.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace FootLook.Core.Dashboard
{
    /// <summary>
    /// The live dashboard (footlook.html), its sign-in page and the "Connect to site" script, shipped
    /// inside the FootLook.Core assembly and served by MapFootLookEndpoints. A host gets the dashboard
    /// by adding FootLook - no files to copy into wwwroot and no UseStaticFiles.
    /// </summary>
    internal static class FootLookDashboard
    {
        private sealed record DashboardFile(string Path, string ResourceName, string ContentType);

        private static readonly DashboardFile[] Files =
        {
            new("/footlook.html", "FootLook.Dashboard.footlook.html", "text/html; charset=utf-8"),
            new("/footlook-login.html", "FootLook.Dashboard.footlook-login.html", "text/html; charset=utf-8"),
            new("/footlook-connect.js", "FootLook.Dashboard.footlook-connect.js", "text/javascript; charset=utf-8"),
        };

        /// <summary>
        /// The pages call the API at the default "/footlook" base path. Only JS string literals start
        /// with a quote followed by /footlook, so rewriting exactly these two forms retargets every API
        /// call without touching "/footlook.html" and the other page URLs.
        /// </summary>
        private const string DefaultBasePath = "/footlook";

        /// <summary>Whether a request path is one of the dashboard's own files (never captured).</summary>
        public static bool IsDashboardPath(string path) =>
            Files.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));

        public static void Map(IEndpointRouteBuilder endpoints, FootLookOptions options)
        {
            var basePath = options.EndpointBasePath.TrimEnd('/');

            foreach (var file in Files)
            {
                var content = Load(file.ResourceName, basePath);
                var etag = new EntityTagHeaderValue($"\"{Convert.ToHexString(SHA256.HashData(content))[..16]}\"");

                endpoints.MapGet(file.Path, (HttpContext context) =>
                {
                    var headers = context.Response.GetTypedHeaders();
                    // Revalidated on every load so a FootLook upgrade reaches the browser at once;
                    // an unchanged file costs only a 304.
                    headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                    headers.ETag = etag;

                    var ifNoneMatch = context.Request.GetTypedHeaders().IfNoneMatch;
                    if (ifNoneMatch.Any(tag => tag.Compare(etag, useStrongComparison: false)))
                    {
                        return Results.StatusCode(StatusCodes.Status304NotModified);
                    }

                    return Results.Bytes(content, file.ContentType);
                }).ExcludeFromDescription();
            }
        }

        private static byte[] Load(string resourceName, string basePath)
        {
            using var stream = typeof(FootLookDashboard).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"FootLook dashboard resource '{resourceName}' is missing from {typeof(FootLookDashboard).Assembly.GetName().Name}.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();

            if (!string.Equals(basePath, DefaultBasePath, StringComparison.Ordinal))
            {
                foreach (var quote in new[] { '\'', '"', '`' })
                {
                    text = text
                        .Replace($"{quote}{DefaultBasePath}/", $"{quote}{basePath}/", StringComparison.Ordinal)
                        .Replace($"{quote}{DefaultBasePath}{quote}", $"{quote}{basePath}{quote}", StringComparison.Ordinal);
                }
            }

            return Encoding.UTF8.GetBytes(text);
        }
    }
}
