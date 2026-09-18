using System.Security.Cryptography;
using System.Text;
using FootLook.Core.Options;

namespace FootLook.Core.Security
{
    /// <summary>
    /// Shared, constant-time key lookup used by both the REST endpoint filter and the
    /// SignalR hub, so the two auth surfaces can't drift out of sync with each other.
    /// </summary>
    public static class FootLookApiKeyMatcher
    {
        public static FootLookApiKey? Match(FootLookOptions options, string? providedKey)
        {
            if (string.IsNullOrWhiteSpace(providedKey))
            {
                return null;
            }

            var providedBytes = Encoding.UTF8.GetBytes(providedKey);

            foreach (var configured in options.ApiKeys)
            {
                if (string.IsNullOrEmpty(configured.Key))
                {
                    continue;
                }

                var configuredBytes = Encoding.UTF8.GetBytes(configured.Key);

                // Fixed-time comparison so response timing can't be used to brute-force a
                // key character by character.
                if (configuredBytes.Length == providedBytes.Length &&
                    CryptographicOperations.FixedTimeEquals(configuredBytes, providedBytes))
                {
                    return configured;
                }
            }

            return null;
        }
    }
}
