using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace FootLook.Central.Security;

/// <summary>
/// A signing key held in this process: loaded from a PEM (an application setting), or - development
/// only - generated fresh at startup. Earlier keys are published for rotation.
/// </summary>
public sealed class LocalRsaSigningKeyProvider : ISigningKeyProvider, IDisposable
{
    public const int MinimumKeyBits = 2048;

    private readonly RSA _rsa;
    private readonly object _gate = new();

    public string KeyId { get; }

    public IReadOnlyList<SigningPublicKey> PublicKeys { get; }

    /// <summary>True for the throwaway key generated when nothing is configured (development only).</summary>
    public bool IsEphemeral { get; }

    public LocalRsaSigningKeyProvider(RSA privateKey, IEnumerable<SigningPublicKey>? previousKeys = null, bool isEphemeral = false)
    {
        if (privateKey.KeySize < MinimumKeyBits)
        {
            throw new SigningKeyConfigurationException($"The signing key must be an RSA key of at least {MinimumKeyBits} bits.");
        }

        _rsa = privateKey;
        IsEphemeral = isEphemeral;

        var current = SigningPublicKey.From(privateKey.ExportParameters(false));
        KeyId = current.KeyId;

        var keys = new List<SigningPublicKey> { current };
        foreach (var previous in previousKeys ?? Array.Empty<SigningPublicKey>())
        {
            if (keys.All(k => k.KeyId != previous.KeyId))
            {
                keys.Add(previous);
            }
        }

        PublicKeys = keys;
    }

    /// <summary>Loads the private key from PEM text (see <see cref="PemText.Normalize"/> for the accepted forms).</summary>
    public static LocalRsaSigningKeyProvider FromPem(string pem, IEnumerable<string>? previousPems = null)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(PemText.Normalize(pem));
            // A public-only PEM imports without error but cannot sign: fail here, not at the first sign-in.
            try
            {
                rsa.SignData(new byte[] { 1 }, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (CryptographicException)
            {
                throw new SigningKeyConfigurationException("Central:SigningKeyPem does not contain a private key.");
            }

            return new LocalRsaSigningKeyProvider(rsa, ParsePublicKeys(previousPems ?? Array.Empty<string>(), "Central:PreviousSigningKeyPems"));
        }
        catch (SigningKeyConfigurationException)
        {
            rsa.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or FormatException)
        {
            rsa.Dispose();
            throw new SigningKeyConfigurationException("Central:SigningKeyPem is not a valid RSA private key in PEM form (PKCS8 or PKCS1).");
        }
    }

    /// <summary>Generates a throwaway key. Everything it signed is invalid after a restart.</summary>
    public static LocalRsaSigningKeyProvider CreateEphemeral(ILogger? logger = null)
    {
        logger?.LogWarning(
            "No signing key is configured (Central:SigningKeyPem / Central:KeyVaultKeyUri). Using a throwaway key generated now: " +
            "session tokens and passes issued before a restart stop working, and no host can trust this instance. Development only.");
        return new LocalRsaSigningKeyProvider(RSA.Create(2048), isEphemeral: true);
    }

    /// <summary>Parses public (or private) PEMs into the public keys to publish.</summary>
    public static IReadOnlyList<SigningPublicKey> ParsePublicKeys(IEnumerable<string> pems, string settingName)
    {
        var keys = new List<SigningPublicKey>();
        foreach (var pem in pems.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(PemText.Normalize(pem));
                keys.Add(SigningPublicKey.From(rsa.ExportParameters(false)));
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException or FormatException)
            {
                throw new SigningKeyConfigurationException($"{settingName} contains an entry that is not a valid RSA key in PEM form.");
            }
        }

        return keys;
    }

    public Task<byte[]> SignAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
    }

    public void Dispose() => _rsa.Dispose();
}

internal static class PemText
{
    /// <summary>
    /// App settings make multi-line values awkward, so besides a real PEM this accepts one whose line
    /// breaks were written as a literal backslash-n, and one that is base64 of the whole PEM text.
    /// </summary>
    public static string Normalize(string value)
    {
        var text = value.Trim();
        if (!text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            try
            {
                text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text)).Trim();
            }
            catch (FormatException)
            {
                // Not base64 either: let the PEM parser reject it.
            }
        }

        return text.Replace("\\r\\n", "\n").Replace("\\n", "\n");
    }
}
