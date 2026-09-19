using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FootLook.Core.Models;
using FootLook.Core.Options;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Core.Security
{
    /// <summary>
    /// Issues and signs the bearer tokens returned by POST {EndpointBasePath}/auth/login.
    /// Validation of tokens on incoming requests is handled separately by the JwtBearer
    /// authentication handler (registered in FootLookServiceCollectionExtensions), which is
    /// given the same signing key via <see cref="SigningKey"/> so the two stay consistent.
    /// Issuing a token also starts the account's observation session - the two are one step,
    /// so there is no way to hold a valid token whose session was never opened.
    /// </summary>
    public class FootLookTokenService
    {
        private readonly FootLookOptions _options;
        private readonly ObservationSessionStore _sessions;
        private readonly SymmetricSecurityKey _signingKey;

        public FootLookTokenService(FootLookOptions options, ObservationSessionStore sessions)
        {
            _options = options;
            _sessions = sessions;
            _signingKey = new SymmetricSecurityKey(ResolveSigningKeyBytes(options));
        }

        public SymmetricSecurityKey SigningKey => _signingKey;

        public (string Token, DateTime ExpiresAtUtc, ObservationSession Session) IssueToken(FootLookUser user)
        {
            var now = DateTime.UtcNow;
            var lifetimeHours = _options.TokenLifetimeHours > 0 ? _options.TokenLifetimeHours : 8;
            var expires = now.AddHours(lifetimeHours);

            var session = _sessions.Start(user.Id, expires);

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id),
                new(JwtRegisteredClaimNames.Email, user.Email),
                new(JwtRegisteredClaimNames.Name, user.DisplayName),
                new(JwtRegisteredClaimNames.Jti, session.SessionId),
                new(FootLookAuthDefaults.SessionIdClaimType, session.SessionId),
                new(FootLookAuthDefaults.AdminClaimType, user.IsAdmin ? "true" : "false"),
            };

            var token = new JwtSecurityToken(
                issuer: FootLookAuthDefaults.Issuer,
                audience: FootLookAuthDefaults.Audience,
                claims: claims,
                notBefore: now,
                expires: expires,
                signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256));

            return (new JwtSecurityTokenHandler().WriteToken(token), expires, session);
        }

        private static byte[] ResolveSigningKeyBytes(FootLookOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.TokenSigningKey))
            {
                var configured = Encoding.UTF8.GetBytes(options.TokenSigningKey);

                // HS256 requires a key of at least 256 bits (32 bytes); a shorter
                // configured value would make signing fail confusingly at first use.
                if (configured.Length >= 32)
                {
                    return configured;
                }
            }

            // No (usable) signing key configured - generate one for this process's
            // lifetime. See FootLookOptions.TokenSigningKey for the tradeoffs.
            return RandomNumberGenerator.GetBytes(32);
        }
    }
}
