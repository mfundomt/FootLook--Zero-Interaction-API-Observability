using System.Security.Claims;

namespace FootLook.Core.Security
{
    /// <summary>
    /// Shared constants for FootLook's bearer-token authentication. Kept in one place so
    /// token issuance (FootLookTokenService), validation (service registration), and
    /// authorization policies (endpoint mapping) can't drift out of sync with each other.
    /// </summary>
    public static class FootLookAuthDefaults
    {
        /// <summary>
        /// Named authentication scheme FootLook registers, distinct from any scheme the
        /// host application may already use, so adding FootLook auth never changes the
        /// host's own default authentication behavior.
        /// </summary>
        public const string SchemeName = "FootLookBearer";

        public const string Issuer = "footlook";
        public const string Audience = "footlook-api";

        /// <summary>Claim type carrying whether the token's account is an admin.</summary>
        public const string AdminClaimType = "footlook_admin";

        /// <summary>Claim type carrying the id of the observation session the token opened.</summary>
        public const string SessionIdClaimType = "footlook_sid";

        /// <summary>Policy required by routes any logged-in developer may call.</summary>
        public const string UserPolicy = "FootLookUser";

        /// <summary>Policy required by routes that affect every caller at once.</summary>
        public const string AdminPolicy = "FootLookAdmin";
    }

    public static class FootLookClaims
    {
        /// <summary>
        /// The account id from a validated token. Falls back to the mapped NameIdentifier
        /// claim because whether "sub" is renamed on the way in depends on which JWT handler
        /// the host's JwtBearer options end up using.
        /// </summary>
        public static string? GetUserId(this ClaimsPrincipal principal) =>
            principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        public static string? GetSessionId(this ClaimsPrincipal principal) =>
            principal.FindFirst(FootLookAuthDefaults.SessionIdClaimType)?.Value;
    }
}
