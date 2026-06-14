using FootLook.Core.Models;
using FootLook.Core.Interfaces;
using System.Collections.Concurrent;
using FootLook.Core.Options;

namespace FootLook.Core.Interfaces
{
    /// <summary>
    /// 
    /// </summary>
    public class InMemorySink : IShadowSink, IShadowCaptureStore
    {
        private readonly ConcurrentQueue<CapturedRequest> _request = new ConcurrentQueue<CapturedRequest>();
        private readonly FootLookOptions _options;

        public InMemorySink(FootLookOptions options)
        {
            _options = options;
        }

        public Task WriteAsync(CapturedRequest request)
        {
            _request.Enqueue(request);
            if (_request.Count > _options.MaxInMemoryCaptures)
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
            return _request.ToList();
        }

        public void Clear()
        {
            while (_request.TryDequeue(out _)) 
            {
            }
        }

        public CapturedRequest? GetById(Guid id)
        {
           return _request.FirstOrDefault(r => r.Id == id);
        }
    }
}
