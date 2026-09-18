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
        private long _nextDedupeSweepUnixMs;
        private const int DedupeSweepIntervalMs = 5000;

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

                        // The sink (CompositeSink in the default registration) retries each
                        // of its own sinks individually and only throws here when every sink
                        // failed to persist this capture - a partial failure is recorded by
                        // the sink itself and still lets the capture through.
                        try
                        {
                            await _sink.WriteAsync(capturedRequest);
                        }
                        catch (Exception persistEx)
                        {
                            _reliabilityState.RecordPersistFailure(persistEx.Message);
                            _logger.LogError(persistEx,
                                "FootLook failed to persist capture {Id} to any sink; skipping event/broadcast for it.",
                                capturedRequest.Id);
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

            SweepExpiredDedupeKeysIfDue(cutoff);

            if (_recentRequestKeys.TryGetValue(dedupeKey, out var seenAt) && seenAt >= cutoff)
            {
                reason = $"key={dedupeKey}";
                return true;
            }

            _recentRequestKeys[dedupeKey] = now;
            return false;
        }

        /// <summary>
        /// Expired dedupe keys used to be swept with a full dictionary scan on every single
        /// request - fine at low volume, an O(n) cost paid on every request at high volume.
        /// This time-gates the sweep to at most once per DedupeSweepIntervalMs regardless of
        /// request rate: entries a little late to be evicted are harmless (TryGetValue's own
        /// cutoff check below still rejects them as expired), but the sweep itself no longer
        /// scales with traffic volume.
        /// </summary>
        private void SweepExpiredDedupeKeysIfDue(DateTime cutoff)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var next = Interlocked.Read(ref _nextDedupeSweepUnixMs);
            if (nowMs < next)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _nextDedupeSweepUnixMs, nowMs + DedupeSweepIntervalMs, next) != next)
            {
                // Another call already claimed this sweep window.
                return;
            }

            foreach (var existing in _recentRequestKeys)
            {
                if (existing.Value < cutoff)
                {
                    _recentRequestKeys.TryRemove(existing.Key, out _);
                }
            }
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

    }
}