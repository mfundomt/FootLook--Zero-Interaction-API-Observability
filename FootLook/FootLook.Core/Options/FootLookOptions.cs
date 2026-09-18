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

        /// <summary>
        /// JSON field names to mask in captured request/response bodies (case-insensitive
        /// exact match). Matching is text-pattern based, not JSON-tree based, so it already
        /// finds a configured name at any nesting depth or inside arrays - "password" is
        /// masked the same whether it's top-level, inside "user.credentials.password", or
        /// repeated across every element of an array. What it cannot do is catch a field
        /// under a name that isn't in this list ("userSSN" when only "ssn" is configured,
        /// or an app-specific field like "clientSecretKey") - add the specific names your
        /// application actually uses. Detecting sensitive VALUES regardless of field name
        /// (e.g. recognizing a credit-card-shaped number in an arbitrarily-named field) is
        /// a distinct, heuristic-based feature that isn't implemented - it would need
        /// pattern/shape detection (Luhn-checked card numbers, SSN formats, etc.) with a
        /// real false-positive risk, and is out of scope here.
        /// </summary>
        public List<string> SensitiveBodyFields { get; set; } = new List<string>()
        {
            "password", "token", "accessToken", "refreshToken", "credit_card_number",
            "creditCardNumber", "cardNumber", "card_number", "ssn", "socialSecurityNumber",
            "social_security_number", "cvv", "cvc", "pin", "secret", "clientSecret",
            "client_secret", "apiKey", "api_key", "privateKey", "private_key"
        };

        /// <summary>Same matching behavior/limitations as <see cref="SensitiveBodyFields"/>, applied to query string parameter names.</summary>
        public List<string> SensitiveQueryParameters { get; set; } = new List<string>()
        {
            "password", "token", "access_token", "refresh_token", "apikey", "api_key",
            "code", "secret", "client_secret", "clientSecret"
        };

        public string EndpointBasePath { get; set; } = "/footlook";

        /// <summary>
        /// Max in-flight captures buffered between ShadowMiddleware and
        /// ShadowBackgroundWorker. This is in-process, in-memory state (not durable across
        /// a restart) - under sustained overload beyond this capacity, the oldest queued
        /// capture is dropped to make room for new ones rather than blocking request
        /// threads. Drops are counted and visible via QueueDropCount on
        /// GET {EndpointBasePath}/reliability/status.
        /// </summary>
        public int QueCapacity { get; set; } = 10_000;

        public bool Enabled { get; set; } = true;

        public List<string> AllowedMethods { get; set; } = new List<string>();

        public double SamplingRate { get; set; } = 1.0; // 0.0 to 1.0, where 1.0 means capture all requests

        public string ServiceName { get; set; } = "UnknownService";

        public string EnvironmentName { get; set; } = "UnknownEnvironment";

        public List<string> AllowedContentType { get; set; } = new List<string>() { "application/json", "application/xml", "text/plain", "application/x-www-form-urlencoded" };

        public int MaxInMemoryCaptures { get; set; } = 1000;

        /// <summary>
        /// Soft cap on the in-memory store's total estimated size (captured body text +
        /// headers, roughly), in bytes. MaxInMemoryCaptures alone assumes captures are
        /// small - with MaxBodyLength set high, a worst case of MaxInMemoryCaptures
        /// large-bodied captures could still balloon memory well past what most hosts
        /// intend. Whichever cap is hit first evicts the oldest capture. 0 disables the
        /// byte cap and relies on MaxInMemoryCaptures alone (previous behavior).
        /// </summary>
        public long MaxInMemoryCaptureBytes { get; set; } = 200 * 1024 * 1024; // 200MB

        public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB

        public int RetentionDays { get; set; } = 30;

        public string MongoConnectionString { get; set; } = string.Empty;
        public string MongoDatabaseName { get; set; } = "footlook";
        public string MongoCollectionName { get; set; } = "captures";
        public bool UseMongoSink { get; set; } = false;

        /// <summary>
        /// When true (the default), every /footlook/* endpoint except /health and
        /// /auth/token requires a valid FootLook bearer token, obtained by exchanging one
        /// of <see cref="ApiKeys"/> at POST {EndpointBasePath}/auth/token. This is on by
        /// default because FootLook observes and can expose live production traffic - it
        /// should not be reachable by anyone who can route to the host. Set to false only
        /// for local/throwaway setups where the host is not reachable by anyone untrusted.
        /// </summary>
        public bool RequireAuthentication { get; set; } = true;

        /// <summary>
        /// Login credentials exchangeable for a bearer token at {EndpointBasePath}/auth/token.
        /// A credential with IsAdmin=false yields a token that can read/clear only its own
        /// capture scope; IsAdmin=true is required for a token that can perform actions
        /// affecting every caller at once (pause/resume capture, clearing the privacy audit
        /// log, self-heal, setup profiles), since those are host-wide, not per-caller.
        /// These credentials are only ever sent once, at login - never on every request.
        /// </summary>
        public List<FootLookApiKey> ApiKeys { get; set; } = new();

        /// <summary>
        /// Symmetric key (HMAC-SHA256) FootLook signs and validates bearer tokens with. If
        /// left empty, a random key is generated for this process's lifetime - fine for a
        /// single-instance/local setup, but tokens will stop validating across a restart and
        /// won't validate across multiple instances behind a load balancer. Set this
        /// explicitly (32+ bytes, from configuration/user-secrets/environment - never
        /// hardcoded in source) for any production or multi-instance deployment.
        /// </summary>
        public string TokenSigningKey { get; set; } = string.Empty;

        /// <summary>
        /// How long an issued bearer token remains valid before the caller must log in
        /// again via {EndpointBasePath}/auth/token.
        /// </summary>
        public double TokenLifetimeHours { get; set; } = 8;

        /// <summary>
        /// Query string parameter the live SignalR hub accepts a bearer token on, since
        /// browsers cannot attach custom headers to a WebSocket upgrade request. Example:
        /// /footlook/live?footlook_token=... . Only used when RequireAuthentication is true.
        /// </summary>
        public string TokenQueryParameterName { get; set; } = "footlook_token";
    }

}
