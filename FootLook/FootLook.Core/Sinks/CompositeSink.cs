using FootLook.Core.Interfaces;
using FootLook.Core.Models;
using FootLook.Core.Options;
using FootLook.Core.Services;
using Microsoft.Extensions.Logging;

namespace FootLook.Core.Sinks
{
    /// <summary>
    /// Fans a capture out to every registered sink independently, retrying each sink
    /// on its own (so a slow/failed sink never causes a duplicate write to a sink that
    /// already succeeded), and only fails the write as a whole when every sink is down.
    /// A failure on some-but-not-all sinks is recorded via <see cref="CaptureReliabilityState"/>
    /// and logged rather than silently swallowed, so operators can actually see it.
    /// </summary>
    public class CompositeSink : IShadowSink
    {
        private readonly IReadOnlyList<IShadowSink> _sinks;
        private readonly FootLookOptions _options;
        private readonly CaptureReliabilityState _reliabilityState;
        private readonly ILogger<CompositeSink> _logger;

        public CompositeSink(
            IEnumerable<IShadowSink> sinks,
            FootLookOptions options,
            CaptureReliabilityState reliabilityState,
            ILogger<CompositeSink> logger)
        {
            _sinks = sinks.ToList();
            _options = options;
            _reliabilityState = reliabilityState;
            _logger = logger;
        }

        public async Task WriteAsync(CapturedRequest request)
        {
            var failures = new List<(string SinkName, string Reason)>();

            foreach (var sink in _sinks)
            {
                var sinkName = sink.GetType().Name;
                var outcome = await TryWriteToSinkAsync(sink, sinkName, request);

                if (!outcome.Succeeded)
                {
                    failures.Add((sinkName, outcome.FailureReason!));
                }
            }

            if (failures.Count == 0)
            {
                return;
            }

            var failedNames = string.Join(", ", failures.Select(f => f.SinkName));

            if (failures.Count == _sinks.Count)
            {
                // Nothing persisted this capture anywhere - the caller must know, so it
                // can retry, skip broadcasting it as if it were saved, etc.
                var reason = $"All sinks failed for capture {request.Id}: {failedNames}";
                _reliabilityState.RecordPersistFailure(reason);
                _logger.LogError("FootLook: {Reason}", reason);
                throw new AggregateException(
                    $"FootLook: every sink failed to write capture {request.Id} ({failedNames}).",
                    failures.Select(f => new InvalidOperationException($"{f.SinkName}: {f.Reason}")));
            }

            // Partial failure: the capture is still durable via the sinks that succeeded,
            // so processing continues, but this must not be invisible.
            foreach (var (sinkName, reason) in failures)
            {
                var message = $"Sink {sinkName} failed to write capture {request.Id}: {reason}";
                _reliabilityState.RecordPersistFailure(message);
                _logger.LogWarning(
                    "FootLook: {Message}. Capture is still available via the sinks that succeeded.",
                    message);
            }
        }

        private async Task<(bool Succeeded, string? FailureReason)> TryWriteToSinkAsync(
            IShadowSink sink,
            string sinkName,
            CapturedRequest request)
        {
            var retryCount = Math.Max(0, _options.SinkWriteRetryCount);
            var baseDelayMs = Math.Max(10, _options.SinkWriteRetryDelayMs);

            for (var attempt = 0; attempt <= retryCount; attempt++)
            {
                try
                {
                    await sink.WriteAsync(request);
                    return (true, null);
                }
                catch (Exception ex) when (attempt < retryCount)
                {
                    var attemptNo = attempt + 1;
                    _reliabilityState.RecordRetry(attemptNo, $"{sinkName}: {ex.Message}");
                    _logger.LogWarning(ex,
                        "FootLook sink {SinkName} write failed for capture {Id}. Retry {Attempt}/{MaxAttempts}.",
                        sinkName, request.Id, attemptNo, retryCount);

                    var backoff = baseDelayMs * (int)Math.Pow(2, attempt);
                    await Task.Delay(backoff);
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
            }

            return (false, "retry attempts exhausted");
        }
    }
}
