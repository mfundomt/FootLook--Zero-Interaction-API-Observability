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
    /// rejected before OnConnectedAsync ever runs. This class only owns per-account group
    /// membership for the live feed.
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
        /// per-account isolation the REST capture endpoints enforce.
        /// </summary>
        public static string GroupNameForUser(string userId) => $"footlook-user:{userId}";

        public override async Task OnConnectedAsync()
        {
            var userId = Context.User?.GetUserId();

            if (!string.IsNullOrWhiteSpace(userId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, GroupNameForUser(userId));
            }
            else
            {
                // Should be unreachable behind the hub's authorization policy. Never fall
                // back to a shared group - a connection with no account gets nothing.
                _logger.LogWarning(
                    "FootLook live client {ConnectionId} connected without an account id; it will not receive live captures.",
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
