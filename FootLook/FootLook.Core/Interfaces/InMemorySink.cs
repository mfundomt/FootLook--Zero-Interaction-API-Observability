using FootLook.Core.Models;
using FootLook.Core.Interfaces;
using FootLook.Core.Options;
using FootLook.Core.Security;
using Microsoft.AspNetCore.Http;

namespace FootLook.Core.Interfaces
{
    /// <summary>
    /// Holds captures in memory, each tagged with the observation sessions allowed to see
    /// it (<see cref="CapturedRequest.ObserverSessionIds"/>). When given the
    /// <see cref="ObservationSessionStore"/> it releases a session's captures the moment
    /// that session ends - logout, token expiry or pruning - so history never outlives the
    /// session that observed it and never piles up in memory.
    /// All state is guarded by one lock. It is only ever taken briefly and never while
    /// calling into the session store or anything else that could call back, so the
    /// per-request hot path (ShadowMiddleware -> session store) can't deadlock with it.
    /// </summary>
    public class InMemorySink : IShadowSink, IShadowCaptureStore
    {
        private readonly object _gate = new();
        private Queue<CapturedRequest> _request = new();
        private readonly FootLookOptions _options;
        private readonly ObservationSessionStore? _sessions;
        private long _approximateBytes;

        public InMemorySink(FootLookOptions options, ObservationSessionStore? sessions = null)
        {
            _options = options;
            _sessions = sessions;

            if (_sessions is not null)
            {
                _sessions.SessionEnded += session => Clear(session.SessionId);
            }
        }

        public Task WriteAsync(CapturedRequest request)
        {
            lock (_gate)
            {
                PruneExpiredCaptures();

                _request.Enqueue(request);
                _approximateBytes += EstimateSize(request);

                // MaxInMemoryCaptures alone assumes captures are small; a byte cap catches the
                // case where MaxBodyLength is configured high and captures are individually
                // large, so a handful of them could otherwise balloon memory well past
                // MaxInMemoryCaptures ever kicking in on count alone.
                while (_request.Count > _options.MaxInMemoryCaptures || ExceedsByteCap())
                {
                    if (!_request.TryDequeue(out var evicted))
                    {
                        break;
                    }

                    _approximateBytes -= EstimateSize(evicted);
                }
            }

            ReleaseEndedObservers(request);

            return Task.CompletedTask;

        }

        /// <summary>
        /// A capture is tagged in ShadowMiddleware and written here some time later (the
        /// queue sits in between). If one of its sessions ended in that gap, that session's
        /// release already ran and found nothing, so the capture would keep the dead session
        /// alive in its tags forever. Checking again after the write closes the gap: either
        /// the session is already gone and is released here, or it is still live and its
        /// eventual end runs after this write and releases it then.
        /// </summary>
        private void ReleaseEndedObservers(CapturedRequest request)
        {
            if (_sessions is null)
            {
                return;
            }

            foreach (var sessionId in request.ObserverSessionIds)
            {
                if (!_sessions.IsActive(sessionId))
                {
                    Clear(sessionId);
                }
            }
        }

        private bool ExceedsByteCap()
        {
            var cap = _options.MaxInMemoryCaptureBytes;
            return cap > 0 && _approximateBytes > cap;
        }

        private static long EstimateSize(CapturedRequest request)
        {
            long size = (request.RequestBody?.Length ?? 0) + (request.ResponseBody?.Length ?? 0);

            if (request.Headers is not null)
            {
                foreach (var header in request.Headers)
                {
                    size += header.Key.Length + (header.Value?.Length ?? 0);
                }
            }

            // .NET strings are UTF-16 (2 bytes/char); this is a deliberately rough
            // estimate for a soft cap, not an exact accounting of object overhead.
            return size * 2;
        }

        /// <summary>
        /// Retrieves a read-only list containing all captured requests, across every
        /// session. Callers must filter on <see cref="CapturedRequest.ObserverSessionIds"/>
        /// before showing anything to a developer.
        /// </summary>
        /// <returns>A read-only list of <see cref="CapturedRequest"/> objects representing all requests captured so far. The
        /// list will be empty if no requests have been captured.</returns>
        public IReadOnlyList<CapturedRequest> GetAll()
        {
            lock (_gate)
            {
                PruneExpiredCaptures();
                return _request.ToList();
            }
        }

        public void Clear(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                // No session -> do not perform a global wipe.
                return;
            }

            lock (_gate)
            {
                // Nothing observed by this session (the common case for a repeat call, e.g.
                // logout after the store was already cleared) -> nothing to rebuild.
                if (!_request.Any(c => c.ObserverSessionIds.Contains(sessionId, StringComparer.Ordinal)))
                {
                    return;
                }

                // A capture other sessions were also observing survives with this session
                // removed from its observers; one only this session could see is dropped.
                // Rebuilt in order so FIFO eviction and retention pruning keep working.
                var retained = new Queue<CapturedRequest>(_request.Count);
                long bytes = 0;
                foreach (var captured in _request)
                {
                    var keep = captured;
                    if (captured.ObserverSessionIds.Contains(sessionId, StringComparer.Ordinal))
                    {
                        var remaining = captured.ObserverSessionIds
                            .Where(id => !string.Equals(id, sessionId, StringComparison.Ordinal))
                            .ToList();
                        if (remaining.Count == 0)
                        {
                            continue;
                        }

                        keep = captured with { ObserverSessionIds = remaining };
                    }

                    retained.Enqueue(keep);
                    bytes += EstimateSize(keep);
                }

                _request = retained;
                _approximateBytes = bytes;
            }
        }

        public CapturedRequest? GetById(Guid id)
        {
            lock (_gate)
            {
                PruneExpiredCaptures();
                return _request.FirstOrDefault(r => r.Id == id);
            }
        }

        // Caller holds _gate.
        private void PruneExpiredCaptures()
        {
            if (_options.RetentionDays <= 0)
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
            while (_request.TryPeek(out var oldest) && oldest.TimestampUtc < cutoff)
            {
                if (_request.TryDequeue(out var evicted))
                {
                    _approximateBytes -= EstimateSize(evicted);
                }
            }
        }
    }
}
