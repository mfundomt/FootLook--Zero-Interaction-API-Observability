using FootLook.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using FootLook.Core.Options;
using FootLook.Core.Models;
using FootLook.Core.Services;

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

            endpoints.MapGet($"{prefix}/dashboard/ops", (FootLookOptions options, CaptureReliabilityState reliabilityState, PrivacyAuditStore privacyAuditStore) =>
            {
                var health = reliabilityState.EvaluateOperationalHealth(options, 20);
                var recentPrivacyEvents = privacyAuditStore.GetRecent(20);

                return Results.Ok(new
                {
                    status = health.HealthStatus,
                    alerts = health.Alerts,
                    reliability = health.Metrics,
                    privacy = new
                    {
                        RecentAuditEvents = recentPrivacyEvents.Count,
                        LastEventUtc = recentPrivacyEvents.FirstOrDefault()?.TimestampUtc
                    }
                });
            });

            endpoints.MapGet($"{prefix}/dev/diagnostics", (FootLookOptions options, FootLookDeveloperExperienceService devx) =>
            {
                var diagnostics = devx.RunDiagnostics(options);
                return Results.Ok(diagnostics);
            });

            endpoints.MapPost($"{prefix}/dev/self-heal", (FootLookOptions options, FootLookDeveloperExperienceService devx) =>
            {
                var result = devx.ApplySelfHealing(options);
                return Results.Ok(new
                {
                    message = "Self-healing checks applied.",
                    result.AppliedFixes,
                    result.Diagnostics
                });
            });

            endpoints.MapPost($"{prefix}/dev/setup", (FootLookOptions options, FootLookDeveloperExperienceService devx, ProductOutcomeMetricsService outcomes, string? profile) =>
            {
                var selectedProfile = string.IsNullOrWhiteSpace(profile) ? "development" : profile;
                var result = devx.ApplySetupProfile(options, selectedProfile);
                outcomes.RecordSetupProfileApplied(selectedProfile);

                return Results.Ok(new
                {
                    message = $"Setup profile '{selectedProfile}' applied.",
                    result.AppliedFixes,
                    result.Diagnostics
                });
            });

            endpoints.MapGet($"{prefix}/outcomes/metrics", (ProductOutcomeMetricsService outcomes) =>
            {
                return Results.Ok(outcomes.Snapshot());
            });

            endpoints.MapPost($"{prefix}/outcomes/session/start", (ProductOutcomeMetricsService outcomes, string sessionId, string tabId, string? siteUrl) =>
            {
                outcomes.StartSession(sessionId, tabId, siteUrl);
                return Results.Ok(new { Message = "Outcome session started.", sessionId, tabId });
            });

            endpoints.MapPost($"{prefix}/outcomes/session/end", (ProductOutcomeMetricsService outcomes, string sessionId) =>
            {
                outcomes.EndSession(sessionId);
                return Results.Ok(new { Message = "Outcome session ended.", sessionId });
            });

            endpoints.MapPost($"{prefix}/outcomes/issue/start", (ProductOutcomeMetricsService outcomes, string key) =>
            {
                outcomes.StartIssueInvestigation(key);
                return Results.Ok(new { Message = "Issue investigation started.", key });
            });

            endpoints.MapPost($"{prefix}/outcomes/issue/complete", (ProductOutcomeMetricsService outcomes, string key) =>
            {
                var completed = outcomes.CompleteIssueInvestigation(key, out var durationSeconds);
                return completed
                    ? Results.Ok(new { Message = "Issue investigation completed.", key, durationSeconds })
                    : Results.NotFound(new { Message = "No investigation found for key.", key });
            });

            endpoints.MapGet($"{prefix}/captures", (HttpContext httpContext, IShadowCaptureStore store, int page = 1, int pageSize = 50, int? minStatusCode = null, long? minDuration = null,
              string? correlationId = null, string sortBy = "timestamp", string sortDirection = "desc", bool failedOnly = false, string? pathContains = null) =>
            {
                var captures = store.GetAll().AsEnumerable();
                var scopeId = httpContext.Request.Cookies["footlook_scope_id"];
                if (string.IsNullOrWhiteSpace(scopeId))
                {
                    scopeId = Guid.NewGuid().ToString("D");
                    httpContext.Response.Cookies.Append("footlook_scope_id", scopeId, new CookieOptions
                    {
                        HttpOnly = true,
                        IsEssential = true,
                        SameSite = SameSiteMode.Lax,
                        Secure = httpContext.Request.IsHttps,
                        Expires = DateTimeOffset.UtcNow.AddYears(1)
                    });
                }

                captures = captures.Where(c => c.CaptureScopeId == scopeId);

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

                return Results.Ok(new
                {
                    Total = total,
                    Page = page,
                    PageSize = pageSize,
                    Results = results
                });
            });

            endpoints.MapGet($"{prefix}/captures/stats",
            (HttpContext httpContext, IShadowCaptureStore store) =>
            {
                var scopeId = httpContext.Request.Cookies["footlook_scope_id"];
                if (string.IsNullOrWhiteSpace(scopeId))
                {
                    scopeId = Guid.NewGuid().ToString("D");
                    httpContext.Response.Cookies.Append("footlook_scope_id", scopeId, new CookieOptions
                    {
                        HttpOnly = true,
                        IsEssential = true,
                        SameSite = SameSiteMode.Lax,
                        Secure = httpContext.Request.IsHttps,
                        Expires = DateTimeOffset.UtcNow.AddYears(1)
                    });
                }
                var captures = store.GetAll()
                    .Where(c => c.CaptureScopeId == scopeId)
                    .ToList();

                var totalRequests = captures.Count;
                var failedRequests = captures.Count(c =>
                    c.StatusCode >= 400 ||
                    !string.IsNullOrWhiteSpace(c.Exception));

                var averageDuration = captures.Any()
                    ? captures.Average(c => c.DurationMs)
                    : 0;

                var slowRequests = captures.Count(c =>
                    c.DurationMs >= 1000);

                var topEndpoints = captures
                    .Where(c => !string.IsNullOrWhiteSpace(c.Path))
                    .GroupBy(c => c.Path)
                    .Select(g => new TopEndpointStats
                    {
                        Path = g.Key,
                        Count = g.Count(),
                        AverageDuration = g.Average(x => x.DurationMs),
                        Failures = g.Count(x =>
                            x.StatusCode >= 400 ||
                            !string.IsNullOrWhiteSpace(x.Exception))
                    })
                    .OrderByDescending(x => x.Count)
                    .Take(10)
                    .ToList();

                var topSlowEndpoints = captures
                    .Where(c => !string.IsNullOrWhiteSpace(c.Path))
                    .GroupBy(c => c.Path
                    )
                    .Select(g => new TopEndpointStats
                    {
                        Path = g.Key,
                        Count = g.Count(),
                        AverageDuration = g.Average(x => x.DurationMs),
                        Failures = g.Count(x =>
                            x.StatusCode >= 400 ||
                            !string.IsNullOrWhiteSpace(x.Exception))
                    })
                    .OrderByDescending(x => x.AverageDuration)
                    .Take(10)
                    .ToList();

                var stats = new CaptureStats
                {
                    TotalRequests = totalRequests,
                    FailedRequests = failedRequests,
                    AverageDurationMs = averageDuration,
                    SlowRequests = slowRequests,
                    TopEndpoints = topEndpoints,
                    TopSlowEndpoints = topSlowEndpoints
                };

                return Results.Ok(stats);
            });


            endpoints.MapGet($"{prefix}/captures/recent",
            (IShadowCaptureStore store,
                   ProductOutcomeMetricsService outcomes,
                   int? count = null,
                   int? page = null,
                   int? pageSize = null,
                   string? footlookSessionId = null,
                   string? footlookTabId = null,
                   double minConfidence = 0.65,
                   string? siteHost = null) =>
            {
                var orderedCaptures = store
                    .GetAll()
                    .OrderByDescending(c => c.TimestampUtc)
                    .ToList();
                var totalEvaluated = orderedCaptures.Count;

                var hasIdentityScope =
                    !string.IsNullOrWhiteSpace(footlookSessionId)
                    && !string.IsNullOrWhiteSpace(footlookTabId);

                CaptureIdentityResolver? resolver = null;
                var threshold = Math.Clamp(minConfidence, 0.0, 1.0);
                List<(CapturedRequest Capture, IdentityResolutionResult Resolution)> scoredCaptures = new();

                if (hasIdentityScope)
                {
                    resolver = new CaptureIdentityResolver();

                    scoredCaptures = orderedCaptures
                        .Select(c => (Capture: c, Resolution: resolver.Resolve(c, footlookSessionId!, footlookTabId!, siteHost)))
                        .Where(x => x.Resolution.Confidence >= threshold)
                        .ToList();

                    orderedCaptures = scoredCaptures
                        .Select(x => x.Capture)
                        .ToList();

                    var lowConfidenceAccepted = scoredCaptures.Count(x => x.Resolution.Confidence < 0.75);
                    outcomes.RecordIdentityEvaluation(totalEvaluated, scoredCaptures.Count, lowConfidenceAccepted);
                }

                if (page.HasValue || pageSize.HasValue)
                {
                    var currentPage = Math.Max(1, page ?? 1);
                    var size = Math.Clamp(pageSize ?? 5, 1, 100);

                    var totalCount = orderedCaptures.Count;

                    object items;
                    if (hasIdentityScope)
                    {
                        items = scoredCaptures
                            .Skip((currentPage - 1) * size)
                            .Take(size)
                            .Select(x => new
                            {
                                capture = x.Capture,
                                identityConfidence = x.Resolution.Confidence,
                                identitySignals = x.Resolution.Signals
                            })
                            .ToList();
                    }
                    else
                    {
                        items = orderedCaptures
                            .Skip((currentPage - 1) * size)
                            .Take(size)
                            .ToList();
                    }

                    return Results.Ok(new
                    {
                        page = currentPage,
                        pageSize = size,
                        totalCount,
                        totalPages = (int)Math.Ceiling(totalCount / (double)size),
                        items
                    });
                }

                var effectiveCount = count ?? 10;

                if (effectiveCount > 0)
                {
                    object recent;
                    if (hasIdentityScope)
                    {
                        recent = scoredCaptures
                            .Take(effectiveCount)
                            .Select(x => new
                            {
                                capture = x.Capture,
                                identityConfidence = x.Resolution.Confidence,
                                identitySignals = x.Resolution.Signals
                            })
                            .ToList();
                    }
                    else
                    {
                        recent = orderedCaptures
                            .Take(effectiveCount)
                            .ToList();
                    }

                    return Results.Ok(recent);
                }

                return Results.BadRequest("count must be greater than zero.");
            });

            endpoints.MapGet($"{prefix}/captures/identity",
            (IShadowCaptureStore store,
                   string footlookSessionId,
                   string footlookTabId,
                   int page = 1,
                   int pageSize = 25,
                   double minConfidence = 0.65,
                   string? siteHost = null) =>
            {
                if (string.IsNullOrWhiteSpace(footlookSessionId) || string.IsNullOrWhiteSpace(footlookTabId))
                {
                    return Results.BadRequest("footlookSessionId and footlookTabId are required.");
                }

                var resolver = new CaptureIdentityResolver();
                var threshold = Math.Clamp(minConfidence, 0.0, 1.0);
                var currentPage = Math.Max(1, page);
                var size = Math.Clamp(pageSize, 1, 100);

                var scored = store
                    .GetAll()
                    .OrderByDescending(c => c.TimestampUtc)
                    .Select(c =>
                    {
                        var resolution = resolver.Resolve(c, footlookSessionId, footlookTabId, siteHost);
                        return new
                        {
                            capture = c,
                            confidence = resolution.Confidence,
                            signals = resolution.Signals
                        };
                    })
                    .Where(x => x.confidence >= threshold)
                    .ToList();

                var items = scored
                    .Skip((currentPage - 1) * size)
                    .Take(size)
                    .ToList();

                return Results.Ok(new
                {
                    page = currentPage,
                    pageSize = size,
                    totalCount = scored.Count,
                    totalPages = (int)Math.Ceiling(scored.Count / (double)size),
                    minConfidence = threshold,
                    items
                });
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
            (IShadowCaptureStore store,
                   int count = 50) =>
            {
                if (count <= 0)
                {
                    return Results.BadRequest("count must be greater than zero.");
                }

                var captures = store
                    .GetAll()
                    .OrderByDescending(c => c.TimestampUtc)
                    .Take(count)
                    .ToList();

                return Results.Ok(captures);
            });

            endpoints.MapPost($"{prefix}/captures/pause", (CaptureRuntimeState state, FootLookOptions options) =>
            {
                state.Pause();
                options.Enabled = false;
                return Results.Ok(new { CaptureEnabled = false, Message = "Capture paused." });
            });

            endpoints.MapPost($"{prefix}/captures/resume", (CaptureRuntimeState state, FootLookOptions options) =>
            {
                state.Resume();
                options.Enabled = true;
                return Results.Ok(new { CaptureEnabled = true, Message = "Capture resumed." });
            });

            endpoints.MapPost($"{prefix}/pause", (CaptureRuntimeState state, FootLookOptions options) =>
            {
                state.Pause();
                options.Enabled = false;
                return Results.Ok(new { CaptureEnabled = false, Message = "Capture paused." });
            });

            endpoints.MapPost("/footlool/pause", (CaptureRuntimeState state, FootLookOptions options) =>
            {
                state.Pause();
                options.Enabled = false;
                return Results.Ok(new { CaptureEnabled = false, Message = "Capture paused." });
            });

            endpoints.MapPost($"{prefix}/resume", (CaptureRuntimeState state, FootLookOptions options) =>
            {
                state.Resume();
                options.Enabled = true;
                return Results.Ok(new { CaptureEnabled = true, Message = "Capture resumed." });
            });

            endpoints.MapGet($"{prefix}/captures/status", (CaptureRuntimeState state, FootLookOptions options) =>
            {
                var enabled = state.IsCaptureEnabled && options.Enabled;
                return Results.Ok(new { CaptureEnabled = enabled });
            });

            endpoints.MapGet($"{prefix}/privacy/status", (FootLookOptions options) =>
            {
                return Results.Ok(new
                {
                    options.EnablePiiMasking,
                    options.EnablePrivacyAudit,
                    options.RedactionValue,
                    options.AnonymizeClientIp,
                    options.AnonymizeUserAgent,
                    HasPrivacyHashSalt = !string.IsNullOrWhiteSpace(options.PrivacyHashSalt),
                    options.RetentionDays,
                    options.PrivacyAuditMaxEntries,
                    SensitiveHeaders = options.SensitiveHeaders,
                    SensitiveBodyFields = options.SensitiveBodyFields,
                    SensitiveQueryParameters = options.SensitiveQueryParameters
                });
            });

            endpoints.MapGet($"{prefix}/privacy/audit", (PrivacyAuditStore store, int count = 50) =>
            {
                var events = store.GetRecent(count);
                return Results.Ok(new
                {
                    count = events.Count,
                    items = events
                });
            });

            endpoints.MapDelete($"{prefix}/privacy/audit", (PrivacyAuditStore store) =>
            {
                store.Clear();
                return Results.Ok(new { Message = "Privacy audit log cleared." });
            });

            endpoints.MapGet($"{prefix}/reliability/status", (FootLookOptions options, CaptureReliabilityState state, int recent = 20) =>
            {
                var health = state.EvaluateOperationalHealth(options, recent);

                return Results.Ok(new
                {
                    options.EnableRequestDeduplication,
                    options.DeduplicationWindowSeconds,
                    options.SinkWriteRetryCount,
                    options.SinkWriteRetryDelayMs,
                    options.BroadcastFailuresAreNonFatal,
                    Runtime = health.Metrics,
                    Health = new
                    {
                        health.HealthStatus,
                        health.Alerts
                    }
                });
            });

            endpoints.MapGet($"{prefix}/operations/health", (FootLookOptions options, CaptureReliabilityState state, int recent = 20) =>
            {
                var health = state.EvaluateOperationalHealth(options, recent);
                return Results.Ok(new
                {
                    status = health.HealthStatus,
                    alerts = health.Alerts,
                    thresholds = new
                    {
                        options.SloMaxAverageIngestLatencyMs,
                        options.SloMaxP95IngestLatencyMs,
                        options.SloMaxEventLossRatePercent,
                        options.SloMaxDashboardFreshnessSeconds
                    },
                    metrics = health.Metrics
                });
            });

            return endpoints;
        }
    }
}
