using System.Collections.Concurrent;
using System.Threading;

namespace FootLook.Core.Services
{
    public sealed class ProductOutcomeMetricsService
    {
        private long _processedCaptureCount;
        private long _identityEvaluatedCount;
        private long _identityAcceptedCount;
        private long _identityLowConfidenceAcceptedCount;

        private DateTime? _lastSetupAppliedUtc;
        private string _lastSetupProfile = string.Empty;
        private DateTime? _firstInsightAfterSetupUtc;

        private readonly ConcurrentDictionary<string, SessionOutcomeState> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _issueInvestigationStarts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<double> _issueIsolationDurationsSeconds = new();

        public void RecordSetupProfileApplied(string profile)
        {
            _lastSetupAppliedUtc = DateTime.UtcNow;
            _lastSetupProfile = string.IsNullOrWhiteSpace(profile) ? "development" : profile.Trim();
            _firstInsightAfterSetupUtc = null;
        }

        public void RecordCaptureProcessed(DateTime capturedAtUtc)
        {
            Interlocked.Increment(ref _processedCaptureCount);

            if (_lastSetupAppliedUtc.HasValue && !_firstInsightAfterSetupUtc.HasValue)
            {
                _firstInsightAfterSetupUtc = DateTime.UtcNow;
            }
        }

        public void RecordIdentityEvaluation(int evaluated, int accepted, int lowConfidenceAccepted)
        {
            if (evaluated > 0) Interlocked.Add(ref _identityEvaluatedCount, evaluated);
            if (accepted > 0) Interlocked.Add(ref _identityAcceptedCount, accepted);
            if (lowConfidenceAccepted > 0) Interlocked.Add(ref _identityLowConfidenceAcceptedCount, lowConfidenceAccepted);
        }

        public void StartSession(string sessionId, string tabId, string? siteUrl)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;

            var key = sessionId.Trim();
            _sessions[key] = new SessionOutcomeState
            {
                SessionId = key,
                TabId = tabId?.Trim() ?? string.Empty,
                SiteUrl = siteUrl?.Trim() ?? string.Empty,
                StartedUtc = DateTime.UtcNow,
                LastUpdatedUtc = DateTime.UtcNow,
                Active = true
            };
        }

        public void EndSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;

            if (_sessions.TryGetValue(sessionId.Trim(), out var existing))
            {
                existing.Active = false;
                existing.EndedUtc = DateTime.UtcNow;
                existing.LastUpdatedUtc = DateTime.UtcNow;
            }
        }

        public void RecordSessionCapture(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;

            if (!_sessions.TryGetValue(sessionId.Trim(), out var existing))
            {
                return;
            }

            var now = DateTime.UtcNow;
            existing.CaptureCount++;
            if (!existing.FirstCaptureUtc.HasValue) existing.FirstCaptureUtc = now;
            existing.LastCaptureUtc = now;
            existing.LastUpdatedUtc = now;
        }

        public void StartIssueInvestigation(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            _issueInvestigationStarts[key.Trim()] = DateTime.UtcNow;
        }

        public bool CompleteIssueInvestigation(string key, out double durationSeconds)
        {
            durationSeconds = 0;
            if (string.IsNullOrWhiteSpace(key)) return false;

            if (!_issueInvestigationStarts.TryRemove(key.Trim(), out var started))
            {
                return false;
            }

            durationSeconds = Math.Max(0, (DateTime.UtcNow - started).TotalSeconds);
            _issueIsolationDurationsSeconds.Enqueue(durationSeconds);

            while (_issueIsolationDurationsSeconds.Count > 500)
            {
                _issueIsolationDurationsSeconds.TryDequeue(out _);
            }

            return true;
        }

        public ProductOutcomeSnapshot Snapshot()
        {
            var setupToFirstInsightSeconds = (_lastSetupAppliedUtc.HasValue && _firstInsightAfterSetupUtc.HasValue)
                ? Math.Max(0, (_firstInsightAfterSetupUtc.Value - _lastSetupAppliedUtc.Value).TotalSeconds)
                : (double?)null;

            var accepted = Interlocked.Read(ref _identityAcceptedCount);
            var lowAccepted = Interlocked.Read(ref _identityLowConfidenceAcceptedCount);

            var falseCorrelationProxyPercent = accepted <= 0
                ? 0
                : (lowAccepted / (double)accepted) * 100;

            var issueDurations = _issueIsolationDurationsSeconds.ToArray();
            var avgIssueIsolationSeconds = issueDurations.Length == 0
                ? (double?)null
                : issueDurations.Average();

            var activeSessions = _sessions.Values.Count(x => x.Active);

            return new ProductOutcomeSnapshot(
                ProcessedCaptureCount: Interlocked.Read(ref _processedCaptureCount),
                LastSetupProfile: _lastSetupProfile,
                LastSetupAppliedUtc: _lastSetupAppliedUtc,
                SetupToFirstInsightSeconds: setupToFirstInsightSeconds,
                IdentityEvaluatedCount: Interlocked.Read(ref _identityEvaluatedCount),
                IdentityAcceptedCount: accepted,
                IdentityLowConfidenceAcceptedCount: lowAccepted,
                FalseCorrelationProxyPercent: falseCorrelationProxyPercent,
                ActiveSessions: activeSessions,
                AverageIssueIsolationSeconds: avgIssueIsolationSeconds,
                CompletedIssueInvestigations: issueDurations.Length,
                SessionDetails: _sessions.Values
                    .OrderByDescending(x => x.LastUpdatedUtc)
                    .Take(50)
                    .Select(x => new SessionOutcomeSummary(
                        x.SessionId,
                        x.TabId,
                        x.SiteUrl,
                        x.Active,
                        x.StartedUtc,
                        x.EndedUtc,
                        x.CaptureCount,
                        x.FirstCaptureUtc,
                        x.LastCaptureUtc))
                    .ToList());
        }
    }

    public sealed record ProductOutcomeSnapshot(
        long ProcessedCaptureCount,
        string LastSetupProfile,
        DateTime? LastSetupAppliedUtc,
        double? SetupToFirstInsightSeconds,
        long IdentityEvaluatedCount,
        long IdentityAcceptedCount,
        long IdentityLowConfidenceAcceptedCount,
        double FalseCorrelationProxyPercent,
        int ActiveSessions,
        double? AverageIssueIsolationSeconds,
        int CompletedIssueInvestigations,
        IReadOnlyList<SessionOutcomeSummary> SessionDetails);

    public sealed record SessionOutcomeSummary(
        string SessionId,
        string TabId,
        string SiteUrl,
        bool Active,
        DateTime StartedUtc,
        DateTime? EndedUtc,
        int CaptureCount,
        DateTime? FirstCaptureUtc,
        DateTime? LastCaptureUtc);

    internal sealed class SessionOutcomeState
    {
        public string SessionId { get; set; } = string.Empty;
        public string TabId { get; set; } = string.Empty;
        public string SiteUrl { get; set; } = string.Empty;
        public bool Active { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? EndedUtc { get; set; }
        public DateTime? FirstCaptureUtc { get; set; }
        public DateTime? LastCaptureUtc { get; set; }
        public int CaptureCount { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
    }
}
