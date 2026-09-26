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
        /// Whether MapFootLookEndpoints serves the built-in live dashboard at /footlook.html (with
        /// /footlook-login.html and /footlook-connect.js). The pages hold no data themselves - every
        /// call they make goes to the authenticated {EndpointBasePath} API. Set to false to run
        /// FootLook headless, e.g. when only the API and live hub are consumed.
        /// </summary>
        public bool EnableDashboard { get; set; } = true;

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

        /// <summary>
        /// Max captures held in memory across all live observation sessions (a capture seen
        /// by several sessions is stored once). Captures are also released when the sessions
        /// that observed them end, so this only bounds a single long-running session.
        /// </summary>
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

        /// <summary>
        /// Also append every capture to captures.jsonl next to the host's binaries. Off by
        /// default: captures live in memory only, and are deleted when the observation session
        /// that recorded them ends. The file cannot honour that - it is one shared append-only
        /// log with no session tags, it holds request and response bodies, and signing out does
        /// not remove anything from it - so turn this on only if you want a durable log and
        /// accept that it outlives sessions. MaxFileSizeBytes and RetentionDays apply to it.
        /// </summary>
        public bool EnableFileSink { get; set; } = false;

        public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB

        public int RetentionDays { get; set; } = 30;

        public string MongoConnectionString { get; set; } = string.Empty;
        public string MongoDatabaseName { get; set; } = "footlook";
        public string MongoCollectionName { get; set; } = "captures";
        public bool UseMongoSink { get; set; } = false;

        /// <summary>
        /// Whether POST {EndpointBasePath}/auth/register accepts new accounts. Every FootLook
        /// endpoint except /health, /auth/register and /auth/login needs a bearer token from
        /// an account, and observation only runs while an account is logged in - so once the
        /// accounts you want exist, set this to false to stop anyone else from creating one.
        /// The first account registered on an instance is its admin (it may pause/resume
        /// capture for everyone, clear the privacy audit log, run self-heal/setup).
        /// </summary>
        public bool AllowRegistration { get; set; } = true;

        /// <summary>
        /// Where the default account store keeps accounts (password hashes included). Empty
        /// means "footlook-users.json" next to the host's binaries. Ignored if you register
        /// your own IFootLookUserStore.
        /// </summary>
        public string UserStorePath { get; set; } = string.Empty;

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
        /// How long an issued bearer token - and the observation session it opened - stays
        /// valid before the developer must log in again via {EndpointBasePath}/auth/login.
        /// When it lapses the session ends and its captures are released, exactly as on
        /// logout - each login is its own observation session with its own capture set.
        /// </summary>
        public double TokenLifetimeHours { get; set; } = 8;

        /// <summary>
        /// How often (seconds) sessions whose token has expired are swept out so their
        /// captures are released even when no request notices the expiry. Sessions that end
        /// by logout, or that a request happens to find expired, are released immediately;
        /// this only bounds how long an idle host holds an expired session's captures.
        /// </summary>
        public int SessionSweepIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Query string parameter the live SignalR hub accepts a bearer token on, since
        /// browsers cannot attach custom headers to a WebSocket upgrade request. Example:
        /// /footlook/live?footlook_token=... .
        /// </summary>
        public string TokenQueryParameterName { get; set; } = "footlook_token";

        /// <summary>
        /// Sign in with Microsoft (Entra ID): POST {EndpointBasePath}/auth/microsoft. See
        /// <see cref="FootLookMicrosoftOptions"/>. Accounts are only persisted when a host
        /// registers an <see cref="Security.IFootLookMicrosoftAccountStore"/> (FootLook.Data's
        /// AddFootLookSqlAccounts); without one the endpoint answers 503 accounts_unavailable.
        /// </summary>
        public FootLookMicrosoftOptions Microsoft { get; set; } = new();

        /// <summary>
        /// Central sign-in through FootLook's own service. Setting <see cref="FootLookCentralOptions.ProjectId"/>
        /// turns it on; see <see cref="FootLookCentralOptions"/>. Bound from FootLook:Central.
        /// </summary>
        public FootLookCentralOptions Central { get; set; } = new();
    }

    /// <summary>
    /// Central sign-in: the developer signs in on FootLook's website, which hands the browser a
    /// short-lived, signed "pass" for this project; the host checks the pass and only then starts
    /// its normal local observation session (POST {EndpointBasePath}/auth/exchange). Captures never
    /// leave this host.
    /// <para>
    /// Turning it on takes one value, <see cref="ProjectId"/>, and NO secret: the host downloads
    /// the service's PUBLIC key by itself (see <see cref="JwksUrl"/>) and uses it only to verify
    /// passes. In central mode this host's own account routes (/auth/register, /auth/login,
    /// /auth/microsoft) answer 404 disabled_in_central_mode. Bound from FootLook:Central.
    /// </para>
    /// </summary>
    public class FootLookCentralOptions
    {
        /// <summary>The default address of FootLook's central service.</summary>
        public const string DefaultIssuer = "https://footlook-auth.azurewebsites.net";

        /// <summary>The default page a browser is sent to in order to sign in and get a pass.</summary>
        public const string DefaultLoginUrl = "https://www.footlook.co.za/connect";

        private string _projectId = string.Empty;
        private string _issuer = DefaultIssuer;
        private string _jwksUrl = string.Empty;
        private string _loginUrl = DefaultLoginUrl;

        /// <summary>
        /// The id FootLook gave your project (for example "prj_abcd1234efgh5678"), shown on the
        /// project's page on the FootLook website. It is a public label, NOT a secret: it is safe to
        /// commit and it is visible to every browser that opens your dashboard. Passes are only
        /// accepted when they were issued for exactly this id. Empty (the default) means central
        /// sign-in is off and the local account routes work as usual.
        /// </summary>
        public string ProjectId
        {
            get => _projectId;
            set => _projectId = value?.Trim() ?? string.Empty;
        }

        /// <summary>True when <see cref="ProjectId"/> is set, i.e. central sign-in is on.</summary>
        public bool IsEnabled => _projectId.Length > 0;

        /// <summary>
        /// The service whose passes this host trusts: a pass's iss claim must equal this exactly.
        /// Defaults to <see cref="DefaultIssuer"/>.
        /// </summary>
        public string Issuer
        {
            get => _issuer;
            set => _issuer = value?.Trim() ?? string.Empty;
        }

        /// <summary>
        /// Where the service's public signing key set (JWKS) is downloaded from. Empty (the default)
        /// means "{Issuer}/.well-known/jwks.json". Must be https, except http on localhost for
        /// local development. The keys are public; no credential is sent.
        /// </summary>
        public string JwksUrl
        {
            get => _jwksUrl.Length > 0 ? _jwksUrl : DefaultJwksUrl;
            // The configuration binder writes back the value it read, which for an unset JwksUrl is the
            // derived default. Storing that would freeze it to the issuer of the moment, so a value equal
            // to the default is kept as "derived" and keeps following the issuer.
            set
            {
                var trimmed = value?.Trim() ?? string.Empty;
                _jwksUrl = string.Equals(trimmed, DefaultJwksUrl, StringComparison.Ordinal) ? string.Empty : trimmed;
            }
        }

        private string DefaultJwksUrl => Issuer.TrimEnd('/') + "/.well-known/jwks.json";

        /// <summary>
        /// The sign-in page the dashboard sends a browser to when it has no session. The dashboard
        /// adds ?project=&lt;ProjectId&gt;&amp;return=&lt;this dashboard's address&gt;.
        /// </summary>
        public string LoginUrl
        {
            get => _loginUrl;
            set => _loginUrl = string.IsNullOrWhiteSpace(value) ? DefaultLoginUrl : value.Trim();
        }

        /// <summary>
        /// Tolerated clock difference (seconds) when checking a pass's exp/nbf/iat. Limited to
        /// 0-60; a larger value is treated as 60.
        /// </summary>
        public int ClockSkewSeconds { get; set; } = 60;
    }

    /// <summary>Settings for validating Microsoft (Entra ID) ID tokens. Bound from FootLook:Microsoft.</summary>
    public class FootLookMicrosoftOptions
    {
        /// <summary>
        /// Application (client) ID of the "FootLook Sign-In" app registration. ID tokens whose
        /// audience is not this value are rejected. It is a public identifier, not a secret.
        /// </summary>
        public string ClientId { get; set; } = "264de9b0-15e7-4887-b686-5b6c18a2f376";

        /// <summary>
        /// OpenID Connect metadata document the signing keys are read from. "common" serves
        /// both work/school and personal accounts; the issuer in it is a template, which
        /// the validator resolves per token from the token's own tid claim.
        /// </summary>
        public string MetadataAddress { get; set; } = "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration";

        /// <summary>Tolerated clock difference when checking exp/nbf/iat.</summary>
        public int ClockSkewSeconds { get; set; } = 60;

        /// <summary>
        /// How old (per its iat claim) an ID token may be when it is presented, in minutes.
        /// The SPA signs in interactively and posts the token straight away, so a short
        /// window narrows how long a leaked token could be replayed (ID tokens otherwise
        /// stay valid for about an hour). 0 disables the check and leaves only exp.
        /// </summary>
        public int MaxIdTokenAgeMinutes { get; set; } = 10;
    }

}
