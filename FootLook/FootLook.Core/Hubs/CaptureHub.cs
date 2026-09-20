using FootLook.Core.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace FootLook.Core.Hubs
{
    /// <summary>
    /// Authentication is enforced where this hub is mapped (MapFootLookEndpoints applies
    /// FootLookAuthDefaults.UserPolicy) - an unauthenticated/expired-token connection is
    /// rejected before OnConnectedAsync ever runs. This class only owns per-session group
    /// membership for the live feed: a connection joins the group of the observation session
    /// its token belongs to, so it only receives traffic captured while that session was live.
    /// </summary>
    public class CaptureHub : Hub
    {
        private readonly ILogger<CaptureHub> _logger;

        public CaptureHub(ILogger<CaptureHub> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Group name a capture's live broadcast is sent to. Must match how connections
        /// join in <see cref="OnConnectedAsync"/> so the live feed respects the same
        /// per-session isolation the REST capture endpoints enforce.
        /// </summary>
        public static string GroupNameForSession(string sessionId) => $"footlook-session:{sessionId}";

        public override async Task OnConnectedAsync()
        {
            var sessionId = Context.User?.GetSessionId();

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, GroupNameForSession(sessionId));
            }
            else
            {
                // Should be unreachable behind the hub's authorization policy. Never fall
                // back to a shared group - a connection with no session gets nothing.
                _logger.LogWarning(
                    "FootLook live client {ConnectionId} connected without a session id; it will not receive live captures.",
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
