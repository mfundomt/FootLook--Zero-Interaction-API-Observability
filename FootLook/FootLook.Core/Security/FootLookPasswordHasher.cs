using System.Security.Cryptography;

namespace FootLook.Core.Security
{
    /// <summary>
    /// PBKDF2-HMAC-SHA256 password hashing. The stored format is
    /// <c>v1.{iterations}.{saltBase64}.{hashBase64}</c> so the work factor can be raised later
    /// without invalidating existing hashes - <see cref="Verify"/> reads the iteration count
    /// back out of the stored value instead of assuming the current default.
    /// </summary>
    public static class FootLookPasswordHasher
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int Iterations = 210_000;

        // Verified against when the account doesn't exist, so a login for an unknown email
        // costs the same PBKDF2 work as a wrong password for a real one - otherwise response
        // time would reveal which emails are registered.
        private static readonly string DummyHash = Hash("footlook-dummy-password");

        public static string Hash(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
            return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
        }

        public static bool Verify(string password, string? storedHash)
        {
            var parts = (storedHash ?? string.Empty).Split('.');
            if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations) || iterations <= 0)
            {
                return false;
            }

            try
            {
                var salt = Convert.FromBase64String(parts[2]);
                var expected = Convert.FromBase64String(parts[3]);
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>Burns the same CPU as a real verification and always returns false.</summary>
        public static void BurnVerifyTime(string password) => Verify(password, DummyHash);
    }
}
