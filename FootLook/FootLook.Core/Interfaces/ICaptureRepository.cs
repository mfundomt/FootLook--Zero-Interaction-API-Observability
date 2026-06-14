using FootLook.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FootLook.Data.Repositories
{
    public interface ICaptureRepository
    {
        Task<List<CapturedRequest>> GetRecentAsync(int count);

        Task<CaptureStats> GetStatsAsync();

        Task<List<CapturedRequest>> SearchAsync(
            string? path = null,
            int? minStatusCode = null,
            long? minDuration = null,
            string? correlationId = null);
    }
}
