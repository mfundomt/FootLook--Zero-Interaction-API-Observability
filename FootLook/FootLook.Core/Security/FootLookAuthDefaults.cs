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

        /// <summary>Claim type carrying whether the token's credential was admin-flagged.</summary>
        public const string AdminClaimType = "footlook_admin";

        /// <summary>Policy required by routes any valid FootLook token may call.</summary>
        public const string UserPolicy = "FootLookUser";

        /// <summary>Policy required by routes that affect every caller at once.</summary>
        public const string AdminPolicy = "FootLookAdmin";
    }
}
