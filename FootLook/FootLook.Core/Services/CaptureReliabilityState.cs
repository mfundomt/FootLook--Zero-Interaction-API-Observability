using System.Collections.Concurrent;
using System.Threading;
using FootLook.Core.Options;

namespace FootLook.Core.Services
{
    public sealed class CaptureReliabilityState
    {
        private long _processedCount;
        private long _deduplicatedCount;
        private long _persistFailureCount;
        private long _broadcastFailureCount;
        private long _retryAttemptCount;
        private long _lastProcessedUnixTimeSeconds;
        private readonly Queue<double> _ingestLatencySamples = new();
        private readonly object _latencyLock = new();
        private const int MaxLatencySamples = 1000;

        private readonly ConcurrentQueue<ReliabilityEvent> _recentEvents = new();

        public void RecordProcessed(DateTime capturedAtUtc)
        {
            Interlocked.Increment(ref _processedCount);
            Interlocked.Exchange(ref _lastProcessedUnixTimeSeconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            var latencyMs = Math.Max(0, (DateTime.UtcNow - capturedAtUtc).TotalMilliseconds);
            lock (_latencyLock)
            {
                _ingestLatencySamples.Enqueue(latencyMs);
                while (_ingestLatencySamples.Count > MaxLatencySamples)
                {
                    _ingestLatencySamples.Dequeue();
                }
            }
        }

        public void RecordDeduplicated(string reason)
        {
            Interlocked.Increment(ref _deduplicatedCount);
            AddEvent($"deduplicated: {reason}");
        }

        public void RecordRetry(int attemptNumber, string reason)
        {
            Interlocked.Increment(ref _retryAttemptCount);
            AddEvent($"retry #{attemptNumber}: {reason}");
        }

        public void RecordPersistFailure(string reason)
        {
            Interlocked.Increment(ref _persistFailureCount);
            AddEvent($"persist-failed: {reason}");
        }

        public void RecordBroadcastFailure(string reason)
        {
            Interlocked.Increment(ref _broadcastFailureCount);
            AddEvent($"broadcast-failed: {reason}");
        }

        public ReliabilitySnapshot Snapshot(int recentEventCount = 20)
        {
            var safeCount = Math.Clamp(recentEventCount, 1, 100);
            var recent = _recentEvents
                .OrderByDescending(x => x.TimestampUtc)
                .Take(safeCount)
                .ToList();

            var processed = Interlocked.Read(ref _processedCount);
            var persistFailures = Interlocked.Read(ref _persistFailureCount);
            var totalPersistAttempts = processed + persistFailures;
            var eventLossRatePercent = totalPersistAttempts <= 0
                ? 0
                : (persistFailures / (double)totalPersistAttempts) * 100;

            List<double> latencySnapshot;
            lock (_latencyLock)
            {
                latencySnapshot = _ingestLatencySamples.ToList();
            }

            var averageLatencyMs = latencySnapshot.Count == 0 ? 0 : latencySnapshot.Average();
            var p95LatencyMs = 0.0;
            if (latencySnapshot.Count > 0)
            {
                var ordered = latencySnapshot.OrderBy(x => x).ToList();
                var percentileIndex = (int)Math.Ceiling(ordered.Count * 0.95) - 1;
                percentileIndex = Math.Clamp(percentileIndex, 0, ordered.Count - 1);
                p95LatencyMs = ordered[percentileIndex];
            }

            var freshnessSeconds = 0.0;
            var lastProcessedSeconds = Interlocked.Read(ref _lastProcessedUnixTimeSeconds);
            if (lastProcessedSeconds > 0)
            {
                freshnessSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastProcessedSeconds;
                if (freshnessSeconds < 0) freshnessSeconds = 0;
            }

            return new ReliabilitySnapshot(
                ProcessedCount: processed,
                DeduplicatedCount: Interlocked.Read(ref _deduplicatedCount),
                PersistFailureCount: persistFailures,
                BroadcastFailureCount: Interlocked.Read(ref _broadcastFailureCount),
                RetryAttemptCount: Interlocked.Read(ref _retryAttemptCount),
                AverageIngestLatencyMs: averageLatencyMs,
                P95IngestLatencyMs: p95LatencyMs,
                EventLossRatePercent: eventLossRatePercent,
                DashboardFreshnessSeconds: freshnessSeconds,
                RecentEvents: recent);
        }

        public OperationalHealthSnapshot EvaluateOperationalHealth(FootLookOptions options, int recentEventCount = 20)
        {
            var snapshot = Snapshot(recentEventCount);
            var alerts = new List<string>();

            if (snapshot.AverageIngestLatencyMs > options.SloMaxAverageIngestLatencyMs)
            {
                alerts.Add($"Average ingest latency {snapshot.AverageIngestLatencyMs:F1}ms exceeded SLO {options.SloMaxAverageIngestLatencyMs:F1}ms.");
            }

            if (snapshot.P95IngestLatencyMs > options.SloMaxP95IngestLatencyMs)
            {
                alerts.Add($"P95 ingest latency {snapshot.P95IngestLatencyMs:F1}ms exceeded SLO {options.SloMaxP95IngestLatencyMs:F1}ms.");
            }

            if (snapshot.EventLossRatePercent > options.SloMaxEventLossRatePercent)
            {
                alerts.Add($"Event loss rate {snapshot.EventLossRatePercent:F2}% exceeded SLO {options.SloMaxEventLossRatePercent:F2}%.");
            }

            if (snapshot.DashboardFreshnessSeconds > options.SloMaxDashboardFreshnessSeconds)
            {
                alerts.Add($"Dashboard freshness {snapshot.DashboardFreshnessSeconds:F1}s exceeded SLO {options.SloMaxDashboardFreshnessSeconds:F1}s.");
            }

            var healthStatus = alerts.Count == 0
                ? "healthy"
                : alerts.Count == 1 ? "degraded" : "critical";

            return new OperationalHealthSnapshot(healthStatus, alerts, snapshot);
        }

        private void AddEvent(string message)
        {
            _recentEvents.Enqueue(new ReliabilityEvent(DateTime.UtcNow, message));

            while (_recentEvents.Count > 500)
            {
                _recentEvents.TryDequeue(out _);
            }
        }
    }

    public sealed record ReliabilityEvent(DateTime TimestampUtc, string Message);

    public sealed record ReliabilitySnapshot(
        long ProcessedCount,
        long DeduplicatedCount,
        long PersistFailureCount,
        long BroadcastFailureCount,
        long RetryAttemptCount,
        double AverageIngestLatencyMs,
        double P95IngestLatencyMs,
        double EventLossRatePercent,
        double DashboardFreshnessSeconds,
        IReadOnlyList<ReliabilityEvent> RecentEvents);

    public sealed record OperationalHealthSnapshot(
        string HealthStatus,
        IReadOnlyList<string> Alerts,
        ReliabilitySnapshot Metrics);
}
