using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FootLook.Core.Models
{
    /// <summary>
    /// This class is record type that represents a captured request. 
    /// It can be used to store information about a request that has been captured for later analysis or processing. 
    /// The properties of this class can be expanded to include details such as the request URL, headers, body, and 
    /// other relevant information depending on the requirements of the application.
    /// </summary>
    public record CapturedRequest
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

        public string Method { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public Dictionary<string, string> Headers { get; init; } = new();

        public string? RequestBody { get; init; }

        public string? ResponseBody { get; init; }

        public int StatusCode { get; init; }

        public long DurationMs { get; init; }

        public string? Exception { get; init; } 

        public string CorrelationId { get; init; } = string.Empty;

        public string ServiceName { get; init; } = string.Empty;
        public string EnvironmentName { get; init; } = string.Empty;    

        public long RequestSizeBytes { get; init; }
        public long ResponseSizeBytes { get; init; }

        public string? RequestContentType { get; init; }    

        public string ? ResponseContentType { get; init; }

        public bool RequestBodyCaptured { get; init; }

        public bool ResponseBodyCaptured { get; init; }

        public string? RequestBodySkippedReason { get; init; }

        public string? ResponseBodySkippedReason { get; init; }

        public string? UserAgent { get; init; }

        public string? ClientIp { get; init; }  


    }
}
