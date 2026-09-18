using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace FootLook.Core.Hubs
{
    public class CaptureHub : Hub
    {
        private const string ScopeCookieName = "footlook_scope_id";

        private readonly ILogger<CaptureHub> _logger;

        public CaptureHub(ILogger<CaptureHub> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Group name a capture's live broadcast is sent to. Must match how connections
        /// join in <see cref="OnConnectedAsync"/> so the live feed respects the same
        /// per-browser scope isolation the REST capture endpoints enforce.
        /// </summary>
        public static string GroupNameForScope(string scopeId) => $"footlook-scope:{scopeId}";

        public override async Task OnConnectedAsync()
        {
            var scopeId = Context.GetHttpContext()?.Request.Cookies[ScopeCookieName];

            if (!string.IsNullOrWhiteSpace(scopeId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, GroupNameForScope(scopeId));
            }
            else
            {
                // No scope cookie yet (e.g. hub connected before any REST call minted one).
                // Do not fall back to a global broadcast group - an unscoped connection gets
                // nothing rather than everyone else's traffic.
                _logger.LogWarning(
                    "FootLook live client {ConnectionId} connected without a footlook_scope_id cookie; it will not receive live captures until it has one.",
                    Context.ConnectionId);
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await base.OnDisconnectedAsync(exception);
        }
    }
}
