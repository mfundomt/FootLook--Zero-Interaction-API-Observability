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
        private readonly IHttpContextAccessor? _httpContextAccessor;
        private readonly ConcurrentQueue<CapturedRequest> _request = new ConcurrentQueue<CapturedRequest>();
        private readonly FootLookOptions _options;

        public InMemorySink(FootLookOptions options, IHttpContextAccessor? httpContextAccessor = null)
        {
            _options = options;
            _httpContextAccessor = httpContextAccessor;
        }

        public Task WriteAsync(CapturedRequest request)
        {
            PruneExpiredCaptures();

            _request.Enqueue(request);
            while (_request.Count > _options.MaxInMemoryCaptures)
            {
                _request.TryDequeue(out _);
            }

            return Task.CompletedTask;

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

        public void Clear()
        {
            var scopeId = _httpContextAccessor?.HttpContext?.Request.Cookies["footlook_scope_id"];

            if (string.IsNullOrWhiteSpace(scopeId))
            {
                // No active browser scope -> do not perform a global wipe.
                return;
            }

            while (_request.TryDequeue(out _)) 
            {
            }
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
                _request.TryDequeue(out _);
            }
        }
    }
}
