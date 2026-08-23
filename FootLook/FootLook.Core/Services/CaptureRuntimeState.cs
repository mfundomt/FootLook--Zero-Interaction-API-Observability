using System.Threading;

namespace FootLook.Core.Services
{
    public class CaptureRuntimeState
    {
        private int _isCaptureEnabled = 1;

        public bool IsCaptureEnabled => Volatile.Read(ref _isCaptureEnabled) == 1;

        public void Pause() => Interlocked.Exchange(ref _isCaptureEnabled, 0);

        public void Resume() => Interlocked.Exchange(ref _isCaptureEnabled, 1);
    }
}
