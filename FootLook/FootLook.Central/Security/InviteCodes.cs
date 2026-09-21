using System.Security.Cryptography;
using System.Text;

namespace FootLook.Central.Security;

/// <summary>
/// Invite codes: "FL-" plus 10 characters from a 32-symbol alphabet with no look-alikes (no I, O, 0, 1),
/// about 50 bits, from a cryptographic random generator. Only the SHA-256 hash of the canonical code is ever
/// stored; the code itself exists in the create-invite response and nowhere else.
/// </summary>
public static class InviteCodes
{
    public const string Prefix = "FL-";
    public const int BodyLength = 10;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate()
    {
        var body = new char[BodyLength];
        for (var i = 0; i < body.Length; i++)
        {
            // GetInt32 is unbiased; 32 symbols means each is exactly 5 bits.
            body[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return Prefix + new string(body);
    }

    /// <summary>
    /// Turns what a person typed (any case, spaces, the hyphen optional) into the canonical code, or false
    /// when it cannot be a code at all.
    /// </summary>
    public static bool TryNormalize(string? input, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 64)
        {
            return false;
        }

        var compact = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c is ' ' or '-' or '\t')
            {
                continue;
            }

            compact.Append(char.ToUpperInvariant(c));
        }

        var text = compact.ToString();
        if (text.Length != 2 + BodyLength || !text.StartsWith("FL", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in text.AsSpan(2))
        {
            if (Alphabet.IndexOf(c) < 0)
            {
                return false;
            }
        }

        canonical = Prefix + text[2..];
        return true;
    }

    /// <summary>Lower-case hex SHA-256 of the canonical code: what the database stores (char(64)).</summary>
    public static string Hash(string canonicalCode) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalCode))).ToLowerInvariant();
}
