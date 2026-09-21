namespace FootLook.Core.Security
{
    /// <summary>
    /// Remembers which pass ids (jti) have been used, so a pass works once. A pass that is copied
    /// out of a browser (history, logs, a shared screen) is then useless after the first exchange.
    /// <para>
    /// In memory only: a restart forgets the ids, but a pass lives a few minutes and the restart
    /// also ends every session, so the window is small. Each id is kept until its pass can no longer
    /// be accepted, then pruned. The cache is bounded (<see cref="MaxEntries"/>); when it is full of
    /// still-live ids, new passes are refused rather than the memory growing.
    /// </para>
    /// </summary>
    public sealed class PassReplayCache
    {
        public const int DefaultMaxEntries = 10_000;

        private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(1);

        private readonly object _gate = new();
        private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
        private readonly TimeProvider _time;
        private DateTimeOffset _lastPruneUtc;

        public PassReplayCache(TimeProvider? timeProvider = null, int maxEntries = DefaultMaxEntries)
        {
            _time = timeProvider ?? TimeProvider.System;
            MaxEntries = maxEntries;
            _lastPruneUtc = _time.GetUtcNow();
        }

        public int MaxEntries { get; }

        /// <summary>Number of ids currently remembered (including ones due for pruning).</summary>
        public int Count
        {
            get { lock (_gate) { return _seen.Count; } }
        }

        /// <summary>
        /// Records the id. Returns false when it was already used (and is still remembered), or when
        /// the cache is full - in both cases the pass must be refused.
        /// </summary>
        /// <param name="jwtId">The pass's jti.</param>
        /// <param name="rememberUntilUtc">The moment the pass can no longer be accepted (its expiry plus the clock skew).</param>
        public bool TryRegister(string jwtId, DateTimeOffset rememberUntilUtc)
        {
            var now = _time.GetUtcNow();
            lock (_gate)
            {
                if (_seen.Count >= MaxEntries || now - _lastPruneUtc >= PruneInterval)
                {
                    Prune(now);
                }

                if (_seen.TryGetValue(jwtId, out var until) && until > now)
                {
                    return false;
                }

                if (!_seen.ContainsKey(jwtId) && _seen.Count >= MaxEntries)
                {
                    return false;
                }

                _seen[jwtId] = rememberUntilUtc;
                return true;
            }
        }

        private void Prune(DateTimeOffset now)
        {
            _lastPruneUtc = now;
            var expired = _seen.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList();
            foreach (var key in expired)
            {
                _seen.Remove(key);
            }
        }
    }
}
