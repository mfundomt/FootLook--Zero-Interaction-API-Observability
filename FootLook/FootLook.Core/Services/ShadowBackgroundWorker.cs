using FootLook.Core.Interfaces;
using FootLook.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using FootLook.Core.Hubs;
using FootLook.Core.Options;
using System.Collections.Concurrent;

namespace FootLook.Core.Services
{
    public class ShadowBackgroundWorker : BackgroundService
    {
        private readonly IShadowQueue _queue;
        private readonly IShadowSink _sink;
        private readonly ILogger<ShadowBackgroundWorker> _logger;
        private readonly CaptureEvents _events;
        private readonly IHubContext<CaptureHub> _hub;
        private readonly FootLookOptions _options;
        private readonly CaptureReliabilityState _reliabilityState;
        private readonly ProductOutcomeMetricsService _productOutcomeMetrics;
        private readonly ConcurrentDictionary<string, DateTime> _recentRequestKeys = new(StringComparer.OrdinalIgnoreCase);

        public ShadowBackgroundWorker(
            IShadowQueue  queue,
            IShadowSink sink,
            ILogger<ShadowBackgroundWorker> logger,
            CaptureEvents events,
            IHubContext<CaptureHub> hub,
            FootLookOptions options,
            CaptureReliabilityState reliabilityState,
            ProductOutcomeMetricsService productOutcomeMetrics)
        {
            _queue = queue;
            _sink = sink;
            _logger = logger;
            _events = events;
            _hub = hub;
            _options = options;
            _reliabilityState = reliabilityState;
            _productOutcomeMetrics = productOutcomeMetrics;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ShadowBackgroundWorker started.");
            try
            {
                await foreach (var capturedRequest in _queue.DequeueAsync(stoppingToken))
                {
                    try
                    {
                        if (IsDuplicateRequest(capturedRequest, out var duplicateReason))
                        {
                            _reliabilityState.RecordDeduplicated(duplicateReason);
                            continue;
                        }

                        _logger.LogInformation("Processing captured request: {Method} {Path}", capturedRequest.Method, capturedRequest.Path);

                        var persisted = await TryWriteWithRetryAsync(capturedRequest, stoppingToken);
                        if (!persisted)
                        {
                            continue;
                        }

                        _events.Publish(capturedRequest);

                        try
                        {
                            if (!string.IsNullOrWhiteSpace(capturedRequest.CaptureScopeId))
                            {
                                await _hub.Clients
                                    .Group(CaptureHub.GroupNameForScope(capturedRequest.CaptureScopeId))
                                    .SendAsync("captureReceived", capturedRequest, cancellationToken: stoppingToken);
                            }
                            else
                            {
                                // No scope on the capture (shouldn't happen once ShadowMiddleware
                                // always sets one) - drop the broadcast rather than fan it out to
                                // every connected dashboard regardless of scope.
                                _logger.LogWarning(
                                    "FootLook capture {Id} has no CaptureScopeId; skipping live broadcast to avoid a cross-scope leak.",
                                    capturedRequest.Id);
                            }
                        }
                        catch (Exception broadcastEx)
                        {
                            _reliabilityState.RecordBroadcastFailure(broadcastEx.Message);

                            if (!_options.BroadcastFailuresAreNonFatal)
                            {
                                throw;
                            }

                            _logger.LogWarning(broadcastEx,
                                "FootLook broadcast failed for capture {Id}; continuing because BroadcastFailuresAreNonFatal=true.",
                                capturedRequest.Id);
                        }

                        _reliabilityState.RecordProcessed(capturedRequest.TimestampUtc);
                        _productOutcomeMetrics.RecordCaptureProcessed(capturedRequest.TimestampUtc);

                        if (TryGetHeader(capturedRequest.Headers, "x-footlook-session-id", out var sessionId) &&
                            !string.IsNullOrWhiteSpace(sessionId))
                        {
                            _productOutcomeMetrics.RecordSessionCapture(sessionId);
                        }

                        _logger.LogInformation(
                            "FootLook saved capture {Id} {Method} {Path} with status {StatusCode} in {DurationMs}ms",
                            capturedRequest.Id, capturedRequest.Method, capturedRequest.Path, capturedRequest.StatusCode, capturedRequest.DurationMs);
                    }
                    catch (Exception itemEx)
                    {
                        _reliabilityState.RecordPersistFailure(itemEx.Message);
                        _logger.LogError(itemEx,
                            "FootLook failed processing one capture item {Id}; worker continues.",
                            capturedRequest.Id);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the service is stopping
                _logger.LogInformation("ShadowBackgroundWorker is stopping.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred in ShadowBackgroundWorker.");
            }
        }

        private bool IsDuplicateRequest(CapturedRequest request, out string reason)
        {
            reason = string.Empty;

            if (!_options.EnableRequestDeduplication)
            {
                return false;
            }

            var dedupeKey = BuildDedupeKey(request);
            if (string.IsNullOrWhiteSpace(dedupeKey))
            {
                return false;
            }

            var now = DateTime.UtcNow;
            var windowSeconds = Math.Max(1, _options.DeduplicationWindowSeconds);
            var cutoff = now.AddSeconds(-windowSeconds);

            foreach (var existing in _recentRequestKeys)
            {
                if (existing.Value < cutoff)
                {
                    _recentRequestKeys.TryRemove(existing.Key, out _);
                }
            }

            if (_recentRequestKeys.TryGetValue(dedupeKey, out var seenAt) && seenAt >= cutoff)
            {
                reason = $"key={dedupeKey}";
                return true;
            }

            _recentRequestKeys[dedupeKey] = now;
            return false;
        }

        private static string BuildDedupeKey(CapturedRequest request)
        {
            if (request.Headers is not null &&
                TryGetHeader(request.Headers, "Idempotency-Key", out var idempotencyKey) &&
                !string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return $"idempotency:{request.Method}:{request.Path}:{idempotencyKey.Trim()}";
            }

            if (!string.IsNullOrWhiteSpace(request.CorrelationId))
            {
                return $"correlation:{request.Method}:{request.Path}:{request.CorrelationId}";
            }

            if (!string.IsNullOrWhiteSpace(request.TraceId) && !string.IsNullOrWhiteSpace(request.SpanId))
            {
                return $"trace:{request.TraceId}:{request.SpanId}";
            }

            return string.Empty;
        }

        private static bool TryGetHeader(Dictionary<string, string> headers, string key, out string value)
        {
            value = string.Empty;
            if (headers.Count == 0)
            {
                return false;
            }

            foreach (var item in headers)
            {
                if (!string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = item.Value;
                return true;
            }

            return false;
        }

        private async Task<bool> TryWriteWithRetryAsync(CapturedRequest request, CancellationToken cancellationToken)
        {
            var retryCount = Math.Max(0, _options.SinkWriteRetryCount);
            var baseDelayMs = Math.Max(10, _options.SinkWriteRetryDelayMs);

            for (var attempt = 0; attempt <= retryCount; attempt++)
            {
                try
                {
                    await _sink.WriteAsync(request);
                    return true;
                }
                catch (Exception ex) when (attempt < retryCount)
                {
                    var attemptNo = attempt + 1;
                    _reliabilityState.RecordRetry(attemptNo, ex.Message);
                    _logger.LogWarning(ex,
                        "FootLook sink write failed for capture {Id}. Retry {Attempt}/{MaxAttempts}.",
                        request.Id,
                        attemptNo,
                        retryCount);

                    var backoff = baseDelayMs * (int)Math.Pow(2, attempt);
                    await Task.Delay(backoff, cancellationToken);
                }
                catch (Exception ex)
                {
                    _reliabilityState.RecordPersistFailure(ex.Message);
                    _logger.LogError(ex,
                        "FootLook sink write failed permanently for capture {Id} after {Attempts} attempts.",
                        request.Id,
                        retryCount + 1);
                    return false;
                }
            }

            return false;
        }

    }
}