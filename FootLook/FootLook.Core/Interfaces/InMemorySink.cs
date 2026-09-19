using FootLook.Core.Models;
using FootLook.Core.Interfaces;
using System.Collections.Concurrent;
using FootLook.Core.Options;
using Microsoft.AspNetCore.Http;

namespace FootLook.Core.Interfaces
{
    /// <summary>
    /// 
    /// </summary>
    public class InMemorySink : IShadowSink, IShadowCaptureStore
    {
        private readonly ConcurrentQueue<CapturedRequest> _request = new ConcurrentQueue<CapturedRequest>();
        private readonly FootLookOptions _options;
        private long _approximateBytes;

        public InMemorySink(FootLookOptions options)
        {
            _options = options;
        }

        public Task WriteAsync(CapturedRequest request)
        {
            PruneExpiredCaptures();

            _request.Enqueue(request);
            Interlocked.Add(ref _approximateBytes, EstimateSize(request));

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

                Interlocked.Add(ref _approximateBytes, -EstimateSize(evicted));
            }

            return Task.CompletedTask;

        }

        private bool ExceedsByteCap()
        {
            var cap = _options.MaxInMemoryCaptureBytes;
            return cap > 0 && Interlocked.Read(ref _approximateBytes) > cap;
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
        /// Retrieves a read-only list containing all captured requests.
        /// </summary>
        /// <returns>A read-only list of <see cref="CapturedRequest"/> objects representing all requests captured so far. The
        /// list will be empty if no requests have been captured.</returns>
        public IReadOnlyList<CapturedRequest> GetAll()
        {
            PruneExpiredCaptures();
            return _request.ToList();
        }

        public void Clear(string? userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                // No account -> do not perform a global wipe.
                return;
            }

            // ConcurrentQueue has no in-place filtered removal, so drain everything and
            // re-enqueue what's left. A capture other accounts were also observing survives
            // with this account removed from its observers; one only this account could see
            // is dropped.
            var retained = new List<CapturedRequest>();
            while (_request.TryDequeue(out var captured))
            {
                if (!captured.ObserverIds.Contains(userId, StringComparer.Ordinal))
                {
                    retained.Add(captured);
                    continue;
                }

                var remaining = captured.ObserverIds.Where(id => !string.Equals(id, userId, StringComparison.Ordinal)).ToList();
                if (remaining.Count > 0)
                {
                    retained.Add(captured with { ObserverIds = remaining });
                }
            }

            foreach (var captured in retained)
            {
                _request.Enqueue(captured);
            }

            RecomputeApproximateBytes();
        }

        public CapturedRequest? GetById(Guid id)
        {
            PruneExpiredCaptures();
           return _request.FirstOrDefault(r => r.Id == id);
        }

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
                    Interlocked.Add(ref _approximateBytes, -EstimateSize(evicted));
                }
            }
        }

        /// <summary>
        /// Recomputes the byte estimate from scratch. Only needed after an operation like
        /// Clear that removes an arbitrary subset of entries rather than dequeuing from the
        /// front - incremental subtraction there would require knowing exactly what was
        /// removed, which the drain-and-reinsert approach already discards.
        /// </summary>
        private void RecomputeApproximateBytes()
        {
            long total = 0;
            foreach (var captured in _request)
            {
                total += EstimateSize(captured);
            }

            Interlocked.Exchange(ref _approximateBytes, total);
        }
    }
}
