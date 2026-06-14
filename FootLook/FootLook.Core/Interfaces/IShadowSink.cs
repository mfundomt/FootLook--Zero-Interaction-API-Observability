using FootLook.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FootLook.Core.Interfaces
{
    /// <summary>
    /// This interface defines a contract for a shadow sink, which is responsible for receiving and processing captured requests.
    /// </summary>
    public interface IShadowSink
    {
        Task WriteAsync(CapturedRequest request);
    }
}
