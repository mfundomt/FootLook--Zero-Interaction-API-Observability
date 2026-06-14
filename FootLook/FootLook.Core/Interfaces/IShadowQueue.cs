using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FootLook.Core.Models;

namespace FootLook.Core.Interfaces
{
    public interface IShadowQueue
    {
        ValueTask EnqueueAsync(CapturedRequest request);
        IAsyncEnumerable<CapturedRequest> DequeueAsync(CancellationToken cancellationToken);
    }
}
