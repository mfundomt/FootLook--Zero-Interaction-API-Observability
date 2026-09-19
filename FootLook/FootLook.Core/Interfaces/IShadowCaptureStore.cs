using FootLook.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FootLook.Core.Interfaces
{
    public interface IShadowCaptureStore
    {
        IReadOnlyList<CapturedRequest> GetAll();

        CapturedRequest? GetById(Guid id);

        /// <summary>
        /// Removes the given account from every capture's observers, deleting captures
        /// nobody else is observing. Another account that was observing the same traffic
        /// keeps its copy. A null/empty userId clears nothing.
        /// </summary>
        void Clear(string? userId);
    }
}
