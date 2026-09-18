using FootLook.Core.Options;
using FootLook.Core.Security;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Threading.Tasks;

namespace FootLook.Core.Hubs
{
    public class CaptureHub : Hub
    {
        private readonly FootLookOptions _options;

        public CaptureHub(FootLookOptions options)
        {
            _options = options;
        }

        public override async Task OnConnectedAsync()
        {
            if (_options.RequireApiKey)
            {
                var httpContext = Context.GetHttpContext();

                // Browsers cannot attach custom headers to a WebSocket upgrade request, so
                // the key travels as a query string parameter on the hub URL for that
                // transport; long-polling/SSE can still send it as a header.
                var providedKey = httpContext?.Request.Query[_options.ApiKeyQueryParameterName].ToString();

                if (string.IsNullOrWhiteSpace(providedKey))
                {
                    providedKey = httpContext?.Request.Headers[_options.ApiKeyHeaderName].ToString();
                }

                var matchedKey = FootLookApiKeyMatcher.Match(_options, providedKey);

                if (matchedKey is null)
                {
                    // Aborting here rejects the connection before it joins any group, so an
                    // unauthenticated client never receives a single captureReceived event.
                    Context.Abort();
                    return;
                }
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await base.OnDisconnectedAsync(exception);
        }
    }
}
