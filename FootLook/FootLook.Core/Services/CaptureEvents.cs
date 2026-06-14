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
        public void Publish(CapturedRequest request)
        {
            OnRequestCaptured?.Invoke(request);
        }
    }
}
