using System.Collections.Concurrent;

namespace FootLook.Core.Security
{
    /// <summary>One login's observation session; its id is carried in the JWT's session claim.</summary>
    public sealed record ObservationSession(string SessionId, string UserId, DateTime StartedAtUtc, DateTime ExpiresAtUtc);

    /// <summary>
    /// The gate on observation. Logging in starts a session here; logging out (or the token
    /// expiring) ends it. ShadowMiddleware captures nothing while no session is active, and
    /// tags each capture with the users whose sessions were active when it happened - so an
    /// account only ever sees traffic that was recorded during its own session.
    /// Sessions are in-memory on purpose: a host restart ends observation and the developer
    /// logs in again, rather than capture silently resuming for a login nobody remembers.
    /// </summary>
    public sealed class ObservationSessionStore
    {
        private readonly ConcurrentDictionary<string, ObservationSession> _sessions = new(StringComparer.Ordinal);

        public ObservationSession Start(string userId, DateTime expiresAtUtc)
        {
            PruneExpired();

            var session = new ObservationSession(Guid.NewGuid().ToString("N"), userId, DateTime.UtcNow, expiresAtUtc);
            _sessions[session.SessionId] = session;
            return session;
        }

        public ObservationSession? Get(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
            {
                return null;
            }

            if (session.ExpiresAtUtc <= DateTime.UtcNow)
            {
                _sessions.TryRemove(sessionId, out _);
                return null;
            }

            return session;
        }

        public bool IsActive(string? sessionId) => Get(sessionId) is not null;

        public bool End(string? sessionId) =>
            !string.IsNullOrEmpty(sessionId) && _sessions.TryRemove(sessionId, out _);

        /// <summary>
        /// Distinct users with at least one live session. Empty means observation is off.
        /// Runs on every request through ShadowMiddleware, so it's a single pass over a
        /// handful of sessions with no locking.
        /// </summary>
        public IReadOnlyList<string> GetActiveUserIds()
        {
            if (_sessions.IsEmpty)
            {
                return Array.Empty<string>();
            }

            var now = DateTime.UtcNow;
            List<string>? ids = null;

            foreach (var session in _sessions.Values)
            {
                if (session.ExpiresAtUtc <= now)
                {
                    _sessions.TryRemove(session.SessionId, out _);
                    continue;
                }

                ids ??= new List<string>();
                if (!ids.Contains(session.UserId))
                {
                    ids.Add(session.UserId);
                }
            }

            return ids ?? (IReadOnlyList<string>)Array.Empty<string>();
        }

        private void PruneExpired()
        {
            var now = DateTime.UtcNow;
            foreach (var session in _sessions.Values)
            {
                if (session.ExpiresAtUtc <= now)
                {
                    _sessions.TryRemove(session.SessionId, out _);
                }
            }
        }
    }
}
