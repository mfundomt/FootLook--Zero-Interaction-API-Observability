using System.Collections.Concurrent;

namespace FootLook.Core.Security
{
    /// <summary>One login's observation session; its id is carried in the JWT's session claim.</summary>
    public sealed record ObservationSession(string SessionId, string UserId, DateTime StartedAtUtc, DateTime ExpiresAtUtc);

    /// <summary>
    /// The gate on observation. Logging in starts a session here; logging out (or the token
    /// expiring) ends it. ShadowMiddleware captures nothing while no session is active, and
    /// tags each capture with the sessions that were active when it happened - so a session
    /// only ever sees traffic that was recorded during its own lifetime, never traffic from
    /// before it started or from an earlier session (even one of the same account).
    /// Every way a session can end (logout, expiry noticed on a request, pruning, the
    /// periodic sweep) raises <see cref="SessionEnded"/> exactly once, so the capture store
    /// can release that session's captures instead of leaking them.
    /// Sessions are in-memory on purpose: a host restart ends observation (and, since
    /// captures are held in memory too, clears them) and the developer logs in again,
    /// rather than capture silently resuming for a login nobody remembers.
    /// </summary>
    public sealed class ObservationSessionStore
    {
        private readonly ConcurrentDictionary<string, ObservationSession> _sessions = new(StringComparer.Ordinal);

        /// <summary>
        /// Raised once for every session that stops being live, after it has been removed
        /// and with no lock held (handlers are free to take their own locks). It runs on
        /// whichever thread noticed - a logout request, a request that found the session
        /// expired, or the sweep - so handlers must be quick; a handler that throws is
        /// swallowed so it can't fail an unrelated request.
        /// </summary>
        public event Action<ObservationSession>? SessionEnded;

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
                Remove(sessionId);
                return null;
            }

            return session;
        }

        public bool IsActive(string? sessionId) => Get(sessionId) is not null;

        public bool End(string? sessionId) =>
            !string.IsNullOrEmpty(sessionId) && Remove(sessionId);

        /// <summary>
        /// Ids of every live session. Empty means observation is off. Runs on every request
        /// through ShadowMiddleware, so it's a single pass over a handful of sessions with
        /// no locking.
        /// </summary>
        public IReadOnlyList<string> GetActiveSessionIds()
        {
            if (_sessions.IsEmpty)
            {
                return Array.Empty<string>();
            }

            var now = DateTime.UtcNow;
            List<string>? ids = null;

            foreach (var entry in _sessions)
            {
                if (entry.Value.ExpiresAtUtc <= now)
                {
                    Remove(entry.Key);
                    continue;
                }

                ids ??= new List<string>();
                ids.Add(entry.Key);
            }

            return ids ?? (IReadOnlyList<string>)Array.Empty<string>();
        }

        /// <summary>
        /// Ends every session whose lifetime has run out. Called on login and by the
        /// periodic sweep, so an expired session's captures are released even when nobody
        /// makes a request that would notice it.
        /// </summary>
        public void PruneExpired()
        {
            var now = DateTime.UtcNow;
            foreach (var entry in _sessions)
            {
                if (entry.Value.ExpiresAtUtc <= now)
                {
                    Remove(entry.Key);
                }
            }
        }

        // TryRemove picks a single winner when several threads notice the same session
        // ending, so SessionEnded fires once per session.
        private bool Remove(string sessionId)
        {
            if (!_sessions.TryRemove(sessionId, out var session))
            {
                return false;
            }

            try
            {
                SessionEnded?.Invoke(session);
            }
            catch
            {
                // Releasing captures must never break the login/logout/request that happened
                // to notice the session end.
            }

            return true;
        }
    }
}
