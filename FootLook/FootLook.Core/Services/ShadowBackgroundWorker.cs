using FootLook.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using FootLook.Core.Hubs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FootLook.Core.Services
{
    public class ShadowBackgroundWorker : BackgroundService
    {
        private readonly IShadowQueue _queue;
        private readonly IShadowSink _sink;
        private readonly ILogger<ShadowBackgroundWorker> _logger;
        private readonly CaptureEvents _events;
        private readonly IHubContext<CaptureHub> _hub;

        public ShadowBackgroundWorker(IShadowQueue  queue, IShadowSink sink, ILogger<ShadowBackgroundWorker> logger, CaptureEvents events, IHubContext<CaptureHub> hub)
        {
            _queue = queue;
            _sink = sink;
            _logger = logger;
            _events = events;
            _hub = hub;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ShadowBackgroundWorker started.");
            try
            {
                await foreach (var capturedRequest in _queue.DequeueAsync(stoppingToken))
                {
                    _logger.LogInformation("Processing captured request: {Method} {Path}", capturedRequest.Method, capturedRequest.Path);
                    await _sink.WriteAsync(capturedRequest);
                    _events.Publish(capturedRequest);

                    //broadcast the captured request to connected SignalR clients
                    await _hub.Clients.All.SendAsync("captureReceived", capturedRequest, cancellationToken: stoppingToken);

                    _logger.LogInformation(
                        "FootLook saved capture {Id} {Method} {Path} with status {StatusCode} in {DurationMs}ms",
                        capturedRequest.Id, capturedRequest.Method, capturedRequest.Path, capturedRequest.StatusCode, capturedRequest.DurationMs);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the service is stopping
                _logger.LogInformation("ShadowBackgroundWorker is stopping.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred in ShadowBackgroundWorker.");
            }
        }

    }
}