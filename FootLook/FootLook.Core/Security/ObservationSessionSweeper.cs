using Microsoft.Extensions.Hosting;
using FootLook.Core.Options;

namespace FootLook.Core.Security
{
    /// <summary>
    /// Periodically ends sessions whose token lifetime has run out, so their captures are
    /// released even if no request ever notices the expiry (an idle host, or a developer
    /// who just closed the tab). Expiry noticed on a request or a login is handled inline
    /// by <see cref="ObservationSessionStore"/>; this only covers the quiet case.
    /// </summary>
    public sealed class ObservationSessionSweeper : BackgroundService
    {
        private readonly ObservationSessionStore _sessions;
        private readonly FootLookOptions _options;

        public ObservationSessionSweeper(ObservationSessionStore sessions, FootLookOptions options)
        {
            _sessions = sessions;
            _options = options;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, _options.SessionSweepIntervalSeconds));
            using var timer = new PeriodicTimer(interval);

            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    _sessions.PruneExpired();
                }
            }
            catch (OperationCanceledException)
            {
                // Host is shutting down.
            }
        }
    }
}
