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
        /// Clears captures belonging to the given capture scope only. A null/empty
        /// scopeId is treated as "no active scope" and clears nothing, to avoid an
        /// unauthenticated caller wiping every tenant's captures at once.
        /// </summary>
        void Clear(string? scopeId);
    }
}
