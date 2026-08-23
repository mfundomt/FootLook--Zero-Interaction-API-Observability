using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FootLook.Core.Options
{
    public  class FootLookOptions
    {
        public bool EnablePiiMasking { get; set; } = true;

        public bool EnablePrivacyAudit { get; set; } = true;

        public int PrivacyAuditMaxEntries { get; set; } = 2000;

        public string RedactionValue { get; set; } = "[REDACTED]";

        public bool AnonymizeClientIp { get; set; } = true;

        public bool AnonymizeUserAgent { get; set; } = true;

        public string PrivacyHashSalt { get; set; } = string.Empty;

        public bool EnableRequestDeduplication { get; set; } = true;

        public int DeduplicationWindowSeconds { get; set; } = 30;

        public int SinkWriteRetryCount { get; set; } = 3;

        public int SinkWriteRetryDelayMs { get; set; } = 100;

        public bool BroadcastFailuresAreNonFatal { get; set; } = true;

        public double SloMaxAverageIngestLatencyMs { get; set; } = 1000;

        public double SloMaxP95IngestLatencyMs { get; set; } = 2500;

        public double SloMaxEventLossRatePercent { get; set; } = 1.0;

        public double SloMaxDashboardFreshnessSeconds { get; set; } = 30;

        public bool CaptureRequestBody { get; set; } = true;
        public bool CaptureResponseBody { get; set; } = true;
        public int MaxBodyLength { get; set; } = 1024 * 1024; //1MB
        public List<string> IgnoredPaths { get; set; } = new();
        public List<string> SensitiveHeaders { get; set; } = new List<string>() { "Authorization", "Cookie", "Set-Cookie", "X-Api-Key" };

        public List<string> SensitiveBodyFields { get; set; } = new List<string>() { "password", "token", "accessToken", "refreshToken", "credit_card_number", "ssn", "cvv", "secret", "apiKey" };

        public List<string> SensitiveQueryParameters { get; set; } = new List<string>() { "password", "token", "access_token", "refresh_token", "apikey", "api_key", "code", "secret" };

        public string EndpointBasePath { get; set; } = "/footlook";

        public int QueCapacity { get; set; } = 10_000;

        public bool Enabled { get; set; } = true;

        public List<string> AllowedMethods { get; set; } = new List<string>();

        public double SamplingRate { get; set; } = 1.0; // 0.0 to 1.0, where 1.0 means capture all requests

        public string ServiceName { get; set; } = "UnknownService";

        public string EnvironmentName { get; set; } = "UnknownEnvironment";

        public List<string> AllowedContentType { get; set; } = new List<string>() { "application/json", "application/xml", "text/plain", "application/x-www-form-urlencoded" };

        public int MaxInMemoryCaptures { get; set; } = 1000;

        public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB

        public int RetentionDays { get; set; } = 30;

        public string MongoConnectionString { get; set; } = string.Empty;
        public string MongoDatabaseName { get; set; } = "footlook";
        public string MongoCollectionName { get; set; } = "captures";
        public bool UseMongoSink { get; set; } = false;
    }
       
}
