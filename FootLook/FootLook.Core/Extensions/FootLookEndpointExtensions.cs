using FootLook.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using FootLook.Core.Options;
using FootLook.Core.Sinks;
using FootLook.Core.Services;
using FootLook.Core.Interfaces;
using FootLook.Data.Repositories;

namespace FootLook.Core.Extensions
{
    public static class FootLookEndpointExtensions
    {
        public static IEndpointRouteBuilder MapFootLookEndpoints(this IEndpointRouteBuilder endpoints, FootLookOptions options)
        {
            var prefix = options.EndpointBasePath.TrimEnd('/');

            endpoints.MapGet($"{prefix}/health", (
                FootLookOptions options,
                IShadowSink sink) =>
            {
                return Results.Ok(new
                {
                    Status = "Healthy",
                    Sink = sink.GetType().Name,
                    options.CaptureRequestBody,
                    options.CaptureResponseBody,
                    options.MaxBodyLength,
                    options.EndpointBasePath
                });
            });

            endpoints.MapGet($"{prefix}/captures", (IShadowCaptureStore store, int page = 1, int pageSize = 50, int? minStatusCode = null, long? minDuration = null,
              string? correlationId = null, string sortBy = "timestamp", string sortDirection = "desc", bool failedOnly = false, string? pathContains = null) =>
            {
                var captures = store.GetAll().AsEnumerable();

                if (failedOnly)
                {
                    captures = captures.Where(c =>
                        c.StatusCode >= 400 ||
                        !string.IsNullOrWhiteSpace(c.Exception));
                }

                if (!string.IsNullOrWhiteSpace(pathContains))
                {
                    captures = captures.Where(c =>
                        !string.IsNullOrWhiteSpace(c.Path) &&
                        c.Path.Contains(pathContains, StringComparison.OrdinalIgnoreCase));
                }

                if (minStatusCode.HasValue)
                {
                    captures = captures.Where(c => c.StatusCode >= minStatusCode.Value);
                }

                if (minDuration.HasValue)
                {
                    captures = captures.Where(c => c.DurationMs >= minDuration.Value);
                }

                if (!string.IsNullOrWhiteSpace(correlationId))
                {
                    captures = captures.Where(c => c.CorrelationId == correlationId);
                }

                var debugPaths = store.GetAll().Select(c => c.Path).ToList();

                page = Math.Max(page, 1);
                pageSize = Math.Clamp(pageSize, 1, 100);

                var total = captures.Count();

                var descending = sortDirection.Equals("desc", StringComparison.OrdinalIgnoreCase);

                captures = sortBy.ToLowerInvariant() switch
                {
                    "duration" => descending
                        ? captures.OrderByDescending(c => c.DurationMs)
                        : captures.OrderBy(c => c.DurationMs),

                    "status" => descending
                        ? captures.OrderByDescending(c => c.StatusCode)
                        : captures.OrderBy(c => c.StatusCode),

                    _ => descending
                        ? captures.OrderByDescending(c => c.TimestampUtc)
                        : captures.OrderBy(c => c.TimestampUtc)
                };

                var results = captures
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();

                //return Results.Ok(new
                //{
                //    Total = total,
                //    Page = page,
                //    PageSize = pageSize,
                //    Results = results
                //});

                return Results.Ok(new
                {
                    DebugPaths = store.GetAll().Select(c => c.Path).ToList(),
                    Total = total,
                    Page = page,
                    PageSize = pageSize,
                    Results = results
                });
            });

            endpoints.MapGet($"{prefix}/captures/stats",
            async (ICaptureRepository repository) =>
            {
                var stats = await repository.GetStatsAsync();

                return Results.Ok(stats);
            });


            endpoints.MapGet($"{prefix}/captures/recent",
            async (ICaptureRepository repository,
                   int count = 10) =>
            {
                var captures =
                    await repository.GetRecentAsync(count);

                return Results.Ok(captures);
            });

            endpoints.MapGet(
                $"{prefix}/captures/{{id:guid}}",
                (InMemorySink memorySink, Guid id) =>
                {
                    var capture = memorySink
            .GetAll()
            .FirstOrDefault(c => c.Id == id);

                    return capture is not null
            ? Results.Ok(capture)
            : Results.NotFound();
                });

            endpoints.MapDelete($"{prefix}/captures", (IShadowCaptureStore store) =>
            {
                store.Clear();
                return Results.Ok(new
                {
                    MessageProcessingHandler = "FootLook captures cleared."
                });
            });

            endpoints.MapGet($"{prefix}/captures/history",
            async (ICaptureRepository repository,
                   int count = 50) =>
            {
                var captures =
                    await repository.GetRecentAsync(count);

                return Results.Ok(captures);
            });

            return endpoints;
        }
    }
}
