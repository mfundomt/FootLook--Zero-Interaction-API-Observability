using System.Net.Mail;
using System.Security.Claims;
using FootLook.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using FootLook.Core.Options;
using FootLook.Core.Models;
using FootLook.Core.Services;
using FootLook.Core.Security;
using FootLook.Core.Hubs;
using FootLook.Core.Dashboard;
using Microsoft.Extensions.DependencyInjection;

namespace FootLook.Core.Extensions
{
    public static class FootLookEndpointExtensions
    {
        private const int MinPasswordLength = 8;
        // PBKDF2 cost grows with input length, so an unbounded password is a cheap way to
        // burn server CPU on an endpoint that is reachable without logging in.
        private const int MaxPasswordLength = 128;

        /// <summary>
        /// The logged-in account's id. Only for account details (e.g. /auth/me) - never for
        /// deciding which captures a caller may see; that is per session, see
        /// <see cref="GetSessionId"/>.
        /// </summary>
        private static string GetUserId(HttpContext httpContext) =>
            httpContext.User.GetUserId() ?? string.Empty;

        /// <summary>
        /// The caller's observation session id (the token's footlook_sid claim). Every
        /// endpoint that reads or deletes captures must go through this (and filter on
        /// <see cref="IsObservedBy"/>) so per-session isolation can't be forgotten on a new
        /// route. The routes are all behind the user policy, so an empty id here only happens
        /// if that wiring is broken - and then it matches no capture rather than all of them.
        /// </summary>
        private static string GetSessionId(HttpContext httpContext) =>
            httpContext.User.GetSessionId() ?? string.Empty;

        private static bool IsObservedBy(CapturedRequest capture, string sessionId) =>
            sessionId.Length > 0 && capture.ObserverSessionIds.Contains(sessionId, StringComparer.Ordinal);

        /// <summary>
        /// The account of a central sign-in, rebuilt from the claims FootLookTokenService put in this
        /// host's own (signature-checked) token. Nothing else identifies such an account: it is never
        /// stored. The account "was created" when its session started.
        /// </summary>
        private static FootLookUser UserFromCentralToken(ClaimsPrincipal principal, string userId, ObservationSession session) =>
            new(
                Id: userId,
                Email: (principal.FindFirst("email") ?? principal.FindFirst(ClaimTypes.Email))?.Value ?? string.Empty,
                DisplayName: (principal.FindFirst("name") ?? principal.FindFirst(ClaimTypes.Name))?.Value ?? string.Empty,
                PasswordHash: string.Empty,
                IsAdmin: string.Equals(principal.FindFirst(FootLookAuthDefaults.AdminClaimType)?.Value, "true", StringComparison.Ordinal),
                CreatedAtUtc: session.StartedAtUtc);

        private static string? ValidateEmail(string? email)
        {
            var trimmed = email?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 254 ||
                !MailAddress.TryCreate(trimmed, out var parsed) ||
                !string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase) ||
                !parsed.Host.Contains('.'))
            {
                return null;
            }

            return trimmed.ToLowerInvariant();
        }

        public static IEndpointRouteBuilder MapFootLookEndpoints(this IEndpointRouteBuilder endpoints, FootLookOptions options)
        {
            var prefix = options.EndpointBasePath.TrimEnd('/');

            // /health is intentionally unauthenticated - it exposes no capture data, only
            // that the subsystem is up, so basic liveness checks don't need a token.
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

            // register and login are the only routes reachable without a token - you can't
            // require a token to obtain one. Registration creates an account but deliberately
            // does not log it in: login is the step that opens an observation session.
            endpoints.MapPost($"{prefix}/auth/register", (FootLookOptions options, IFootLookUserStore users, FootLookRegisterRequest? request) =>
            {
                if (!options.AllowRegistration)
                {
                    return Results.Json(new { message = "Registration is closed on this FootLook instance." }, statusCode: StatusCodes.Status403Forbidden);
                }

                var email = ValidateEmail(request?.Email);
                if (email is null)
                {
                    return Results.BadRequest(new { message = "Enter a valid email address." });
                }

                var password = request?.Password ?? string.Empty;
                if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
                {
                    return Results.BadRequest(new { message = $"Password must be {MinPasswordLength}-{MaxPasswordLength} characters." });
                }

                var displayName = request?.DisplayName?.Trim();
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = email[..email.IndexOf('@')];
                }
                else if (displayName.Length > 100)
                {
                    return Results.BadRequest(new { message = "Display name must be 100 characters or fewer." });
                }

                var user = users.TryCreate(email, displayName, FootLookPasswordHasher.Hash(password));
                if (user is null)
                {
                    return Results.Conflict(new { message = "An account with that email already exists." });
                }

                return Results.Created($"{prefix}/auth/me", FootLookUserSummary.From(user));
            }).DisabledInCentralMode();

            // Generates the bearer token and, in the same step, opens a new observation
            // session for this login (with its own, initially empty, capture set) - capture
            // stays off until somebody has logged in.
            endpoints.MapPost($"{prefix}/auth/login", (IFootLookUserStore users, FootLookTokenService tokenService, FootLookLoginRequest? request) =>
            {
                var email = request?.Email?.Trim() ?? string.Empty;
                var password = request?.Password ?? string.Empty;
                var user = email.Length > 0 && password.Length <= MaxPasswordLength ? users.FindByEmail(email) : null;

                if (user is null)
                {
                    FootLookPasswordHasher.BurnVerifyTime(password.Length <= MaxPasswordLength ? password : string.Empty);
                }

                // One message for "no such account" and "wrong password" so the response
                // doesn't reveal which emails are registered.
                if (user is null || !FootLookPasswordHasher.Verify(password, user.PasswordHash))
                {
                    return Results.Json(new { message = "Invalid email or password." }, statusCode: StatusCodes.Status401Unauthorized);
                }

                var (token, expiresAtUtc, _) = tokenService.IssueToken(user);
                return Results.Ok(new { token, expiresAtUtc, user = FootLookUserSummary.From(user) });
            }).DisabledInCentralMode();

            // Sign in with Microsoft (Entra ID): the third unauthenticated route, see FootLookMicrosoftEndpoint.
            endpoints.MapFootLookMicrosoftSignIn(prefix);

            // Central sign-in (GET /auth/config, POST /auth/exchange), see FootLookCentralEndpoint. In central
            // mode it is the way in, and the three local account routes above answer 404 instead.
            endpoints.MapFootLookCentralSignIn(prefix);

            // Everything else requires a valid bearer token (obtained above) whose
            // observation session is still open. Reads/writes that only touch the caller's
            // own session's captures live in `group`; actions that affect every caller at once
            // (pause/resume, privacy-audit clear, self-heal/setup) live in `adminGroup` and
            // additionally require an admin token.
            var group = endpoints.MapGroup(prefix).RequireAuthorization(FootLookAuthDefaults.UserPolicy);
            var adminGroup = endpoints.MapGroup(prefix).RequireAuthorization(FootLookAuthDefaults.AdminPolicy);

            // The logged-in developer's details, plus the JWT this request was made with, so
            // it can be copied straight into Swagger's Authorize box (or any client's
            // Authorization: Bearer header).
            group.MapGet("/auth/me", async (HttpContext httpContext, IFootLookUserStore users, ObservationSessionStore sessions) =>
            {
                var userId = GetUserId(httpContext);
                var session = sessions.Get(httpContext.User.GetSessionId());
                // Central sign-in: the identity was proven by a pass and lives only in this host's own
                // token (no store holds it), so it is read back from the token's claims. Only in central
                // mode and only for the "central:" id the exchange route issues; local mode is untouched.
                var user = options.Central.IsEnabled && session is not null && userId.StartsWith(FootLookCentralEndpoint.UserIdPrefix, StringComparison.Ordinal)
                    ? UserFromCentralToken(httpContext.User, userId, session)
                    // Accounts that signed in with Microsoft live in their own store (see IFootLookMicrosoftAccountStore).
                    : users.FindById(userId)
                        ?? (userId.Length > 0 && httpContext.RequestServices.GetService<IFootLookMicrosoftAccountStore>() is { } microsoftAccounts
                            ? await microsoftAccounts.FindByIdAsync(userId, httpContext.RequestAborted)
                            : null);
                if (user is null || session is null)
                {
                    return Results.Json(new { message = "Authentication required" }, statusCode: StatusCodes.Status401Unauthorized);
                }

                var authorization = httpContext.Request.Headers.Authorization.ToString();
                var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? authorization["Bearer ".Length..].Trim()
                    // The SignalR hub isn't this route, but a client that authenticated via
                    // the query string (the only other accepted form) still gets its token back.
                    : httpContext.Request.Query["footlook_token"].ToString();

                return Results.Ok(new
                {
                    user = FootLookUserSummary.From(user),
                    token,
                    tokenExpiresAtUtc = session.ExpiresAtUtc,
                    sessionId = session.SessionId,
                    sessionStartedAtUtc = session.StartedAtUtc,
                    observationActive = true
                });
            });

            // Ends this login's observation session. The token stops validating immediately,
            // this session's captures are deleted (ones another live session also observed
            // stay for that session), and if no other session is live, capture stops. Another
            // login of the same account is a separate session and is untouched.
            group.MapPost("/auth/logout", (HttpContext httpContext, ObservationSessionStore sessions, IShadowCaptureStore store) =>
            {
                var sessionId = GetSessionId(httpContext);
                sessions.End(sessionId);
                // The capture store also releases on the session-ended event; doing it here
                // as well keeps logout correct for a store that isn't wired to that event.
                store.Clear(sessionId);
                return Results.Ok(new { message = "Signed out. Observation session ended." });
            });

            group.MapGet("/dashboard/ops", (FootLookOptions options, CaptureReliabilityState reliabilityState, PrivacyAuditStore privacyAuditStore) =>
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

            group.MapGet("/dev/diagnostics", (FootLookOptions options, FootLookDeveloperExperienceService devx) =>
            {
                var diagnostics = devx.RunDiagnostics(options);
                return Results.Ok(diagnostics);
            });

            adminGroup.MapPost("/dev/self-heal", (FootLookOptions options, FootLookDeveloperExperienceService devx) =>
            {
                var result = devx.ApplySelfHealing(options);
                return Results.Ok(new
                {
                    message = "Self-healing checks applied.",
                    result.AppliedFixes,
                    result.Diagnostics
                });
            });

            adminGroup.MapPost("/dev/setup", (FootLookOptions options, FootLookDeveloperExperienceService devx, ProductOutcomeMetricsService outcomes, string? profile) =>
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

            group.MapGet("/outcomes/metrics", (ProductOutcomeMetricsService outcomes) =>
            {
                return Results.Ok(outcomes.Snapshot());
            });

            group.MapPost("/outcomes/session/start", (ProductOutcomeMetricsService outcomes, string sessionId, string tabId, string? siteUrl) =>
            {
                outcomes.StartSession(sessionId, tabId, siteUrl);
                return Results.Ok(new { Message = "Outcome session started.", sessionId, tabId });
            });

            group.MapPost("/outcomes/session/end", (ProductOutcomeMetricsService outcomes, string sessionId) =>
            {
                outcomes.EndSession(sessionId);
                return Results.Ok(new { Message = "Outcome session ended.", sessionId });
            });

            group.MapPost("/outcomes/issue/start", (ProductOutcomeMetricsService outcomes, string key) =>
            {
                outcomes.StartIssueInvestigation(key);
                return Results.Ok(new { Message = "Issue investigation started.", key });
            });

            group.MapPost("/outcomes/issue/complete", (ProductOutcomeMetricsService outcomes, string key) =>
            {
                var completed = outcomes.CompleteIssueInvestigation(key, out var durationSeconds);
                return completed
                    ? Results.Ok(new { Message = "Issue investigation completed.", key, durationSeconds })
                    : Results.NotFound(new { Message = "No investigation found for key.", key });
            });

            group.MapGet("/captures", (HttpContext httpContext, IShadowCaptureStore store, int page = 1, int pageSize = 50, int? minStatusCode = null, long? minDuration = null,
              string? correlationId = null, string sortBy = "timestamp", string sortDirection = "desc", bool failedOnly = false, string? pathContains = null) =>
            {
                var captures = store.GetAll().AsEnumerable();
                var sessionId = GetSessionId(httpContext);

                captures = captures.Where(c => IsObservedBy(c, sessionId));

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

            group.MapGet("/captures/stats",
            (HttpContext httpContext, IShadowCaptureStore store) =>
            {
                var sessionId = GetSessionId(httpContext);
                var captures = store.GetAll()
                    .Where(c => IsObservedBy(c, sessionId))
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


            group.MapGet("/captures/recent",
            (HttpContext httpContext,
                   IShadowCaptureStore store,
                   ProductOutcomeMetricsService outcomes,
                   CaptureIdentityResolver identityResolver,
                   int? count = null,
                   int? page = null,
                   int? pageSize = null,
                   string? footlookSessionId = null,
                   string? footlookTabId = null,
                   double minConfidence = 0.65,
                   string? siteHost = null) =>
            {
                var sessionId = GetSessionId(httpContext);
                var orderedCaptures = store
                    .GetAll()
                    .Where(c => IsObservedBy(c, sessionId))
                    .OrderByDescending(c => c.TimestampUtc)
                    .ToList();
                var totalEvaluated = orderedCaptures.Count;

                var hasIdentityScope =
                    !string.IsNullOrWhiteSpace(footlookSessionId)
                    && !string.IsNullOrWhiteSpace(footlookTabId);

                var threshold = Math.Clamp(minConfidence, 0.0, 1.0);
                List<(CapturedRequest Capture, IdentityResolutionResult Resolution)> scoredCaptures = new();

                if (hasIdentityScope)
                {
                    scoredCaptures = orderedCaptures
                        .Select(c => (Capture: c, Resolution: identityResolver.Resolve(c, footlookSessionId!, footlookTabId!, siteHost)))
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

            group.MapGet("/captures/identity",
            (HttpContext httpContext,
                   IShadowCaptureStore store,
                   CaptureIdentityResolver resolver,
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

                var sessionId = GetSessionId(httpContext);
                var threshold = Math.Clamp(minConfidence, 0.0, 1.0);
                var currentPage = Math.Max(1, page);
                var size = Math.Clamp(pageSize, 1, 100);

                var scored = store
                    .GetAll()
                    .Where(c => IsObservedBy(c, sessionId))
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

            group.MapGet(
                "/captures/{id:guid}",
                (HttpContext httpContext, IShadowCaptureStore store, Guid id) =>
                {
                    var sessionId = GetSessionId(httpContext);
                    var capture = store.GetById(id);

                    // Not-observed-by-your-session is reported the same as "not found" so a caller
                    // can't use this route to probe for the existence of captures from before
                    // their session or from another session.
                    return capture is not null && IsObservedBy(capture, sessionId)
            ? Results.Ok(capture)
            : Results.NotFound();
                });

            group.MapDelete("/captures", (HttpContext httpContext, IShadowCaptureStore store) =>
            {
                store.Clear(GetSessionId(httpContext));
                return Results.Ok(new
                {
                    MessageProcessingHandler = "FootLook captures cleared."
                });
            });

            group.MapGet("/captures/history",
            (HttpContext httpContext,
                   IShadowCaptureStore store,
                   int count = 50) =>
            {
                if (count <= 0)
                {
                    return Results.BadRequest("count must be greater than zero.");
                }

                var sessionId = GetSessionId(httpContext);
                var captures = store
                    .GetAll()
                    .Where(c => IsObservedBy(c, sessionId))
                    .OrderByDescending(c => c.TimestampUtc)
                    .Take(count)
                    .ToList();

                return Results.Ok(captures);
            });

            // Pause/resume affect capture for every caller at once (ShadowMiddleware observes
            // all host traffic, not just the calling session's own traffic), so these require
            // an admin account rather than the baseline access used for reading/clearing one's own
            // session's captures.
            adminGroup.MapPost("/captures/pause", (CaptureRuntimeState state, FootLookOptions options) =>
            {
                state.Pause();
                options.Enabled = false;
                return Results.Ok(new { CaptureEnabled = false, Message = "Capture paused." });
            });

            adminGroup.MapPost("/captures/resume", (CaptureRuntimeState state, FootLookOptions options) =>
            {
                state.Resume();
                options.Enabled = true;
                return Results.Ok(new { CaptureEnabled = true, Message = "Capture resumed." });
            });

            adminGroup.MapPost("/pause", (CaptureRuntimeState state, FootLookOptions options) =>
            {
                state.Pause();
                options.Enabled = false;
                return Results.Ok(new { CaptureEnabled = false, Message = "Capture paused." });
            });

            adminGroup.MapPost("/resume", (CaptureRuntimeState state, FootLookOptions options) =>
            {
                state.Resume();
                options.Enabled = true;
                return Results.Ok(new { CaptureEnabled = true, Message = "Capture resumed." });
            });

            group.MapGet("/captures/status", (CaptureRuntimeState state, FootLookOptions options) =>
            {
                var enabled = state.IsCaptureEnabled && options.Enabled;
                return Results.Ok(new { CaptureEnabled = enabled });
            });

            group.MapGet("/privacy/status", (FootLookOptions options) =>
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

            group.MapGet("/privacy/audit", (PrivacyAuditStore store, int count = 50) =>
            {
                var events = store.GetRecent(count);
                return Results.Ok(new
                {
                    count = events.Count,
                    items = events
                });
            });

            adminGroup.MapDelete("/privacy/audit", (PrivacyAuditStore store) =>
            {
                store.Clear();
                return Results.Ok(new { Message = "Privacy audit log cleared." });
            });

            group.MapGet("/reliability/status", (FootLookOptions options, CaptureReliabilityState state, int recent = 20) =>
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

            group.MapGet("/operations/health", (FootLookOptions options, CaptureReliabilityState state, int recent = 20) =>
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

            // Mapped here rather than left to the host so its auth requirement can never
            // drift from the REST endpoints above - previously the demo mapped this itself
            // with a hardcoded path that happened to match EndpointBasePath by coincidence.
            endpoints.MapHub<CaptureHub>($"{prefix}/live")
                .RequireAuthorization(FootLookAuthDefaults.UserPolicy);

            // The live dashboard ships inside this assembly, so the host needs no wwwroot copy or
            // UseStaticFiles to get it.
            if (options.EnableDashboard)
            {
                FootLookDashboard.Map(endpoints, options);
            }

            return endpoints;
        }
    }
}
