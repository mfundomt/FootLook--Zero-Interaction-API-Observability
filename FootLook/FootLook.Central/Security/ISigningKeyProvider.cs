using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Central.Security;

/// <summary>The public half of a signing key, as published in the JWKS.</summary>
public sealed record SigningPublicKey(string KeyId, byte[] Modulus, byte[] Exponent)
{
    public RsaSecurityKey ToSecurityKey() =>
        new(new RSAParameters { Modulus = Modulus, Exponent = Exponent }) { KeyId = KeyId };

    /// <summary>The RFC 7638 JWK thumbprint (base64url SHA-256): a stable id derived from the key itself.</summary>
    public static string ThumbprintOf(byte[] modulus, byte[] exponent)
    {
        var canonical = JsonSerializer.Serialize(new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["e"] = Base64UrlEncoder.Encode(exponent),
            ["kty"] = "RSA",
            ["n"] = Base64UrlEncoder.Encode(modulus),
        });
        return Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Big-endian integers with no leading zero bytes, as JWK requires.</summary>
    public static byte[] TrimLeadingZeros(byte[] value)
    {
        var skip = 0;
        while (skip < value.Length - 1 && value[skip] == 0)
        {
            skip++;
        }

        return skip == 0 ? value : value[skip..];
    }

    public static SigningPublicKey From(RSAParameters parameters)
    {
        var modulus = TrimLeadingZeros(parameters.Modulus!);
        var exponent = TrimLeadingZeros(parameters.Exponent!);
        return new SigningPublicKey(ThumbprintOf(modulus, exponent), modulus, exponent);
    }
}

/// <summary>Thrown at startup when no usable signing key can be set up. The message never contains key material.</summary>
public sealed class SigningKeyConfigurationException : InvalidOperationException
{
    public SigningKeyConfigurationException(string message) : base(message)
    {
    }

    public SigningKeyConfigurationException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// The central signing key. Everything central signs (session tokens, passes) is RS256 and goes through
/// <see cref="SignAsync"/>, so the private key can live somewhere this process cannot read it (Key Vault).
/// </summary>
public interface ISigningKeyProvider
{
    /// <summary>The <c>kid</c> written into the header of everything signed now.</summary>
    string KeyId { get; }

    /// <summary>
    /// Every public key that must be published and accepted: the current key first, then earlier keys kept
    /// while tokens signed with them can still be alive (rotation).
    /// </summary>
    IReadOnlyList<SigningPublicKey> PublicKeys { get; }

    /// <summary>RSASSA-PKCS1-v1_5 with SHA-256 over <paramref name="data"/> with the current key.</summary>
    Task<byte[]> SignAsync(byte[] data, CancellationToken cancellationToken = default);
}
