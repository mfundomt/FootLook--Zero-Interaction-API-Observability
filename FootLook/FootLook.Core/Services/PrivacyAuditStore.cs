using System.Collections.Concurrent;

namespace FootLook.Core.Services
{
    public sealed class PrivacyAuditStore
    {
        private readonly ConcurrentQueue<PrivacyAuditEntry> _entries = new();

        public PrivacyAuditStore()
        {
        }

        public void Add(PrivacyAuditEntry entry, int maxEntries)
        {
            _entries.Enqueue(entry);

            while (_entries.Count > Math.Max(100, maxEntries))
            {
                _entries.TryDequeue(out _);
            }
        }

        public IReadOnlyList<PrivacyAuditEntry> GetRecent(int count)
        {
            var safeCount = Math.Clamp(count, 1, 500);
            return _entries
                .OrderByDescending(x => x.TimestampUtc)
                .Take(safeCount)
                .ToList();
        }

        public void Clear() => _entries.Clear();
    }

    public sealed record PrivacyAuditEntry(
        DateTime TimestampUtc,
        string Method,
        string Path,
        string CorrelationId,
        int MaskedHeaders,
        int MaskedQueryParameters,
        int MaskedRequestBodyFields,
        int MaskedResponseBodyFields,
        bool ClientIpAnonymized,
        bool UserAgentAnonymized,
        string RedactionValue);
}
