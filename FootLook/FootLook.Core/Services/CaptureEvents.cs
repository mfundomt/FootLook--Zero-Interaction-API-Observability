using FootLook.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FootLook.Core.Services
{
    public class CaptureEvents
    {
        public event Action<CapturedRequest>? OnRequestCaptured;

        private volatile bool _paused;
        public void Pause() => _paused = true;

        public void Resume() => _paused = false;

        public bool IsPaused => _paused;

        public void Publish(CapturedRequest request)
        {
            if (_paused) return;
            OnRequestCaptured?.Invoke(request);
        }

    }
}
