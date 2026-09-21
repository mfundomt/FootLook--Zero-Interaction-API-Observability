namespace FootLook.Central.Options;

/// <summary>Settings for the central service. Bound from the "Central" configuration section.</summary>
public sealed class CentralOptions
{
    public const string DefaultIssuer = "https://footlook-auth.azurewebsites.net";

    /// <summary>The origins the sites may call from when the "Central:AllowedOrigins" list is left empty.</summary>
    public static readonly string[] DefaultAllowedOrigins =
    {
        "https://www.footlook.co.za",
        "https://footlook.co.za",
        "http://localhost:4200",
        "http://localhost:4210",
    };

    /// <summary>
    /// The <c>iss</c> of central session tokens and passes, and the base of the public key address
    /// ({Issuer}/.well-known/jwks.json). Hosts must be configured with the same value.
    /// </summary>
    public string Issuer { get; set; } = DefaultIssuer;

    /// <summary>When false, a Microsoft sign-in for an identity with no account is refused (403 registration_closed).</summary>
    public bool AllowRegistration { get; set; } = true;

    /// <summary>
    /// PKCS8 (or PKCS1) RSA private key in PEM form, at least 2048 bits. A SECRET: supply it as an
    /// application setting / environment variable, never in a file that is committed. Also accepted:
    /// the PEM with literal "\n" for its line breaks, or the whole PEM base64-encoded.
    /// </summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>
    /// Earlier signing keys that are still published in the JWKS (and still accepted for central
    /// session tokens) while passes and tokens signed with them can be alive. PEM; the private part is
    /// not needed, a "PUBLIC KEY" PEM is enough. Used for rotation.
    /// </summary>
    public string[] PreviousSigningKeyPems { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Azure Key Vault RSA key, https://{vault}.vault.azure.net/keys/{name}[/{version}]. When set it is
    /// used instead of <see cref="SigningKeyPem"/>: signing is done inside the vault, so the private key
    /// never reaches this process. Authenticates with DefaultAzureCredential (the App Service managed
    /// identity, which needs the "sign" and "get" key permissions).
    /// </summary>
    public string? KeyVaultKeyUri { get; set; }

    /// <summary>Earlier Key Vault keys whose public parts stay in the JWKS (rotation).</summary>
    public string[] KeyVaultPreviousKeyUris { get; set; } = Array.Empty<string>();

    /// <summary>Origins allowed by CORS. Empty means <see cref="DefaultAllowedOrigins"/>.</summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Read the client address from X-Forwarded-For / X-Forwarded-Proto (the App Service front end sets
    /// them), so per-IP rate limits see the real client. Only the last hop is trusted.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; } = true;

    /// <summary>Per-minute request limits (fixed window) for the rate-limited routes.</summary>
    public CentralRateLimits RateLimits { get; set; } = new();

    /// <summary>Failed invite redemptions a user may make within the window before 429 too_many_attempts.</summary>
    public int RedeemMaxFailures { get; set; } = 10;

    public int RedeemFailureWindowMinutes { get; set; } = 15;

    public IReadOnlyList<string> EffectiveAllowedOrigins =>
        AllowedOrigins.Length > 0 ? AllowedOrigins : DefaultAllowedOrigins;

    public string NormalizedIssuer => Issuer.Trim().TrimEnd('/');
}

public sealed class CentralRateLimits
{
    /// <summary>POST /auth/microsoft, per client IP.</summary>
    public int SignInPerMinute { get; set; } = 20;

    /// <summary>POST /invites/redeem, per user.</summary>
    public int RedeemPerMinute { get; set; } = 20;

    /// <summary>POST /projects/{id}/connect, per user.</summary>
    public int ConnectPerMinute { get; set; } = 30;
}
