using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FootLook.Core.Models
{
    public class CaptureStats
    {
        public int TotalRequests { get; set; }

        public int FailedRequests { get; set; }

        public double AverageDurationMs { get; set; }

        public int SlowRequests { get; set; }

        public int RequestsPerMinute { get; set; }

        public IEnumerable<TopEndpointStats> TopEndpoints { get; set; }
            = Enumerable.Empty<TopEndpointStats>();

        public IEnumerable<TopEndpointStats> TopSlowEndpoints { get; set; }
            = Enumerable.Empty<TopEndpointStats>();
    }
}
