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
        void Clear();
    }
}
