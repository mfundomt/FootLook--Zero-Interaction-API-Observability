using System.Collections.Concurrent;
using FootLook.Central.Options;

namespace FootLook.Central.Security;

/// <summary>
/// Slows down guessing of invite codes: a user who fails to redeem too many times in a window is refused
/// (429 too_many_attempts) without the code being checked at all. Kept in memory, per instance; a restart
/// or a second instance resets it, which the per-user rate limiter and the ~50-bit code space make harmless.
/// </summary>
public sealed class RedeemThrottle
{
    private const int PruneAbove = 10_000;

    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _failures = new();
    private readonly TimeProvider _time;
    private readonly int _maxFailures;
    private readonly TimeSpan _window;

    public RedeemThrottle(TimeProvider time, CentralOptions options)
    {
        _time = time;
        _maxFailures = Math.Max(1, options.RedeemMaxFailures);
        _window = TimeSpan.FromMinutes(Math.Max(1, options.RedeemFailureWindowMinutes));
    }

    public bool IsBlocked(Guid userId)
    {
        if (!_failures.TryGetValue(userId, out var queue))
        {
            return false;
        }

        lock (queue)
        {
            Trim(queue, _time.GetUtcNow());
            return queue.Count >= _maxFailures;
        }
    }

    public void RecordFailure(Guid userId)
    {
        var now = _time.GetUtcNow();
        var queue = _failures.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            Trim(queue, now);
            queue.Enqueue(now);
        }

        if (_failures.Count > PruneAbove)
        {
            foreach (var (key, value) in _failures)
            {
                lock (value)
                {
                    Trim(value, now);
                    if (value.Count == 0)
                    {
                        _failures.TryRemove(key, out _);
                    }
                }
            }
        }
    }

    private void Trim(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && now - queue.Peek() >= _window)
        {
            queue.Dequeue();
        }
    }
}
