using System.Security.Cryptography;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace FootLook.Central.Security;

/// <summary>A Key Vault key's public part and the version-specific key id used to sign with it.</summary>
public sealed record KeyVaultPublicKey(string VersionedKeyId, byte[] Modulus, byte[] Exponent);

/// <summary>
/// The two Key Vault calls the provider needs. A seam so the provider can be tested with a fake: the real
/// vault is not reachable from tests.
/// </summary>
public interface IKeyVaultKeyOperations
{
    /// <summary>Reads the key's public part (the current version when the URI names none). Throws when it cannot.</summary>
    KeyVaultPublicKey GetPublicKey(Uri keyUri);

    /// <summary>Signs <paramref name="data"/> with RS256 inside the vault; the private key never leaves it.</summary>
    Task<byte[]> SignAsync(string versionedKeyId, byte[] data, CancellationToken cancellationToken);
}

/// <summary>
/// Signs with an Azure Key Vault RSA key (CryptographyClient), so the private key never reaches this
/// process. The public key published in the JWKS is read from the same key VERSION that signs, and one
/// test signature is verified against it at startup, so a wrong key, a missing "sign" permission or an
/// unreachable vault fails startup with a clear message instead of failing the first sign-in.
/// <para>
/// UNVERIFIED against a real vault: no vault existed when this was written. Only the logic around the
/// <see cref="IKeyVaultKeyOperations"/> seam is covered by tests.
/// </para>
/// </summary>
public sealed class KeyVaultSigningKeyProvider : ISigningKeyProvider
{
    private readonly IKeyVaultKeyOperations _vault;
    private readonly string _versionedKeyId;

    public string KeyId { get; }

    public IReadOnlyList<SigningPublicKey> PublicKeys { get; }

    public KeyVaultSigningKeyProvider(
        IKeyVaultKeyOperations vault,
        string keyUri,
        IEnumerable<string>? previousKeyUris = null,
        IEnumerable<SigningPublicKey>? previousPublicKeys = null)
    {
        _vault = vault;

        var current = Load(keyUri, "Central:KeyVaultKeyUri");
        _versionedKeyId = current.VersionedKeyId;
        var currentPublic = ToPublicKey(current);
        KeyId = currentPublic.KeyId;

        var keys = new List<SigningPublicKey> { currentPublic };
        foreach (var previousUri in (previousKeyUris ?? Array.Empty<string>()).Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            keys.Add(ToPublicKey(Load(previousUri, "Central:KeyVaultPreviousKeyUris")));
        }

        keys.AddRange(previousPublicKeys ?? Array.Empty<SigningPublicKey>());
        PublicKeys = keys.DistinctBy(k => k.KeyId).ToList();

        VerifySigningWorks(currentPublic);
    }

    /// <summary>The real thing: DefaultAzureCredential (the App Service managed identity when deployed).</summary>
    public static KeyVaultSigningKeyProvider Create(string keyUri, IEnumerable<string>? previousKeyUris, IEnumerable<SigningPublicKey>? previousPublicKeys, TokenCredential? credential = null) =>
        new(new AzureKeyVaultKeyOperations(credential ?? new DefaultAzureCredential()), keyUri, previousKeyUris, previousPublicKeys);

    public Task<byte[]> SignAsync(byte[] data, CancellationToken cancellationToken = default) =>
        _vault.SignAsync(_versionedKeyId, data, cancellationToken);

    private KeyVaultPublicKey Load(string uriText, string settingName)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new SigningKeyConfigurationException($"{settingName} must be an https Key Vault key address (https://<vault>.vault.azure.net/keys/<name>).");
        }

        try
        {
            return _vault.GetPublicKey(uri);
        }
        catch (SigningKeyConfigurationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Type (and HTTP status when the vault answered) only: never the exception text or credentials.
            var status = ex is RequestFailedException failed ? $" (HTTP {failed.Status})" : string.Empty;
            throw new SigningKeyConfigurationException(
                $"Could not read the Key Vault key named by {settingName} ({uri.Host}{uri.AbsolutePath}): {ex.GetType().Name}{status}. " +
                "Check the address and that the app's identity may 'get' keys.");
        }
    }

    private static SigningPublicKey ToPublicKey(KeyVaultPublicKey key)
    {
        var modulus = SigningPublicKey.TrimLeadingZeros(key.Modulus);
        var exponent = SigningPublicKey.TrimLeadingZeros(key.Exponent);
        if (modulus.Length * 8 < LocalRsaSigningKeyProvider.MinimumKeyBits - 8)
        {
            throw new SigningKeyConfigurationException($"The Key Vault key must be RSA with at least {LocalRsaSigningKeyProvider.MinimumKeyBits} bits.");
        }

        return new SigningPublicKey(SigningPublicKey.ThumbprintOf(modulus, exponent), modulus, exponent);
    }

    /// <summary>Signs a probe with the vault and checks it against the published public key.</summary>
    private void VerifySigningWorks(SigningPublicKey current)
    {
        var probe = System.Text.Encoding.ASCII.GetBytes("footlook-central-startup-check");
        byte[] signature;
        try
        {
            signature = _vault.SignAsync(_versionedKeyId, probe, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var status = ex is RequestFailedException failed ? $" (HTTP {failed.Status})" : string.Empty;
            throw new SigningKeyConfigurationException(
                $"Key Vault refused to sign with the configured key: {ex.GetType().Name}{status}. Check that the app's identity has the 'sign' key permission.");
        }

        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = current.Modulus, Exponent = current.Exponent });
        if (!rsa.VerifyData(probe, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            throw new SigningKeyConfigurationException("Key Vault's signature does not verify against the key's public part; the key is not usable for RS256.");
        }
    }
}

/// <summary>Azure SDK implementation of <see cref="IKeyVaultKeyOperations"/>. Not testable without a vault.</summary>
internal sealed class AzureKeyVaultKeyOperations : IKeyVaultKeyOperations
{
    private readonly TokenCredential _credential;
    private readonly Dictionary<string, CryptographyClient> _crypto = new();

    public AzureKeyVaultKeyOperations(TokenCredential credential) => _credential = credential;

    public KeyVaultPublicKey GetPublicKey(Uri keyUri)
    {
        // https://{vault}.vault.azure.net/keys/{name}[/{version}]
        var segments = keyUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 2 or > 3 || !string.Equals(segments[0], "keys", StringComparison.OrdinalIgnoreCase))
        {
            throw new SigningKeyConfigurationException("The Key Vault key address must look like https://<vault>.vault.azure.net/keys/<name>[/<version>].");
        }

        var vaultUri = new Uri(keyUri.GetLeftPart(UriPartial.Authority));
        var version = segments.Length == 3 ? segments[2] : null;
        KeyVaultKey key = new KeyClient(vaultUri, _credential).GetKey(segments[1], version);

        if (key.KeyType != KeyType.Rsa && key.KeyType != KeyType.RsaHsm)
        {
            throw new SigningKeyConfigurationException("The Key Vault key must be an RSA key.");
        }

        return new KeyVaultPublicKey(key.Id.ToString(), key.Key.N, key.Key.E);
    }

    public async Task<byte[]> SignAsync(string versionedKeyId, byte[] data, CancellationToken cancellationToken)
    {
        CryptographyClient client;
        lock (_crypto)
        {
            if (!_crypto.TryGetValue(versionedKeyId, out client!))
            {
                client = new CryptographyClient(new Uri(versionedKeyId), _credential);
                _crypto[versionedKeyId] = client;
            }
        }

        // SignData hashes locally (SHA-256) and sends only the digest to the vault.
        var result = await client.SignDataAsync(SignatureAlgorithm.RS256, data, cancellationToken);
        return result.Signature;
    }
}
