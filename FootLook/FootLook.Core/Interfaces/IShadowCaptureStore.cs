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
        /// Removes the given observation session from every capture's observers, deleting
        /// captures no other live session is observing (which frees their memory). Another
        /// session that was observing the same traffic - even of the same account - keeps
        /// its copy. Used by DELETE /captures, logout, and session expiry. A null/empty
        /// sessionId clears nothing.
        /// </summary>
        void Clear(string? sessionId);
    }
}
