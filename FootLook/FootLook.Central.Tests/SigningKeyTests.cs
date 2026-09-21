using System.Security.Cryptography;
using System.Text;
using Azure;
using FootLook.Central.Options;
using FootLook.Central.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace FootLook.Central.Tests;

public class LocalSigningKeyTests
{
    private static string PkcsPem(RSA rsa) => rsa.ExportPkcs8PrivateKeyPem();

    private static bool Verifies(SigningPublicKey key, byte[] data, byte[] signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = key.Modulus, Exponent = key.Exponent });
        return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    [Fact]
    public async Task A_pkcs8_pem_gives_a_provider_whose_signatures_verify_with_the_published_key()
    {
        using var rsa = RSA.Create(2048);
        using var provider = LocalRsaSigningKeyProvider.FromPem(PkcsPem(rsa));

        var data = Encoding.UTF8.GetBytes("header.payload");
        var signature = await provider.SignAsync(data);

        var published = Assert.Single(provider.PublicKeys);
        Assert.Equal(provider.KeyId, published.KeyId);
        Assert.True(Verifies(published, data, signature));
        Assert.False(provider.IsEphemeral);
    }

    [Fact]
    public void The_key_id_is_stable_for_the_same_key_and_differs_between_keys()
    {
        using var rsa = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var pem = PkcsPem(rsa);

        using var first = LocalRsaSigningKeyProvider.FromPem(pem);
        using var second = LocalRsaSigningKeyProvider.FromPem(pem);
        using var different = LocalRsaSigningKeyProvider.FromPem(PkcsPem(other));

        Assert.Equal(first.KeyId, second.KeyId);
        Assert.NotEqual(first.KeyId, different.KeyId);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first.KeyId);
    }

    [Fact]
    public void Pkcs1_pem_the_escaped_newline_form_and_the_base64_form_all_load()
    {
        using var rsa = RSA.Create(2048);
        var pkcs8 = PkcsPem(rsa);
        var pkcs1 = rsa.ExportRSAPrivateKeyPem();

        using var a = LocalRsaSigningKeyProvider.FromPem(pkcs1);
        using var b = LocalRsaSigningKeyProvider.FromPem(pkcs8.Replace("\r\n", "\\n").Replace("\n", "\\n"));
        using var c = LocalRsaSigningKeyProvider.FromPem(Convert.ToBase64String(Encoding.UTF8.GetBytes(pkcs8)));

        Assert.Equal(a.KeyId, b.KeyId);
        Assert.Equal(a.KeyId, c.KeyId);
    }

    [Fact]
    public void A_public_only_pem_is_refused_because_it_cannot_sign()
    {
        using var rsa = RSA.Create(2048);
        var ex = Assert.Throws<SigningKeyConfigurationException>(() => LocalRsaSigningKeyProvider.FromPem(rsa.ExportSubjectPublicKeyInfoPem()));
        Assert.Contains("private key", ex.Message);
    }

    [Theory]
    [InlineData("not a pem at all, hunter2-secret-value")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----")]
    public void Garbage_is_refused_and_the_message_never_echoes_it(string garbage)
    {
        var ex = Assert.Throws<SigningKeyConfigurationException>(() => LocalRsaSigningKeyProvider.FromPem(garbage));
        Assert.DoesNotContain("hunter2", ex.Message);
        Assert.DoesNotContain("AAAA", ex.Message);
    }

    [Fact]
    public void A_key_under_2048_bits_is_refused()
    {
        using var small = RSA.Create(1024);
        var ex = Assert.Throws<SigningKeyConfigurationException>(() => LocalRsaSigningKeyProvider.FromPem(PkcsPem(small)));
        Assert.Contains("2048", ex.Message);
    }

    [Fact]
    public void Earlier_keys_are_published_after_the_current_one_and_a_private_or_public_pem_will_do()
    {
        using var current = RSA.Create(2048);
        using var old1 = RSA.Create(2048);
        using var old2 = RSA.Create(2048);

        using var provider = LocalRsaSigningKeyProvider.FromPem(
            PkcsPem(current), new[] { old1.ExportSubjectPublicKeyInfoPem(), PkcsPem(old2), "  " });

        Assert.Equal(3, provider.PublicKeys.Count);
        Assert.Equal(provider.KeyId, provider.PublicKeys[0].KeyId);
        Assert.Equal(3, provider.PublicKeys.Select(k => k.KeyId).Distinct().Count());
    }

    [Fact]
    public void An_invalid_earlier_key_stops_startup()
    {
        using var rsa = RSA.Create(2048);
        var ex = Assert.Throws<SigningKeyConfigurationException>(() => LocalRsaSigningKeyProvider.FromPem(PkcsPem(rsa), new[] { "nope" }));
        Assert.Contains("PreviousSigningKeyPems", ex.Message);
    }

    [Fact]
    public void The_same_key_listed_as_earlier_is_published_once()
    {
        using var rsa = RSA.Create(2048);
        using var provider = LocalRsaSigningKeyProvider.FromPem(PkcsPem(rsa), new[] { rsa.ExportSubjectPublicKeyInfoPem() });
        Assert.Single(provider.PublicKeys);
    }

    [Fact]
    public void Published_numbers_have_no_leading_zero_bytes_and_the_exponent_is_65537()
    {
        using var rsa = RSA.Create(2048);
        using var provider = LocalRsaSigningKeyProvider.FromPem(PkcsPem(rsa));
        var key = provider.PublicKeys[0];

        Assert.Equal(256, key.Modulus.Length);
        Assert.NotEqual(0, key.Modulus[0]);
        Assert.Equal(new byte[] { 1, 0, 1 }, key.Exponent);
    }
}

public class SigningKeyProviderFactoryTests
{
    private sealed class Env : IHostEnvironment
    {
        public Env(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void Development_without_a_key_gets_a_throwaway_key_and_a_loud_warning()
    {
        var logs = new CapturingLoggerProvider();
        var provider = SigningKeyProviderFactory.Create(new CentralOptions(), new Env("Development"), logs.CreateLogger("t"));

        var local = Assert.IsType<LocalRsaSigningKeyProvider>(provider);
        Assert.True(local.IsEphemeral);
        Assert.Contains(logs.Lines, l => l.StartsWith("Warning") && l.Contains("throwaway"));
        local.Dispose();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void Outside_development_a_missing_key_fails_startup_with_a_clear_message(string environment)
    {
        var ex = Assert.Throws<SigningKeyConfigurationException>(() =>
            SigningKeyProviderFactory.Create(new CentralOptions(), new Env(environment), NullLogger.Instance));

        Assert.Contains("Central:SigningKeyPem", ex.Message);
        Assert.Contains("Central:KeyVaultKeyUri", ex.Message);
    }

    [Fact]
    public void A_whitespace_only_key_counts_as_missing()
    {
        Assert.Throws<SigningKeyConfigurationException>(() =>
            SigningKeyProviderFactory.Create(new CentralOptions { SigningKeyPem = "   ", KeyVaultKeyUri = " " }, new Env("Production"), NullLogger.Instance));
    }

    [Fact]
    public void A_configured_pem_is_used_in_any_environment()
    {
        using var rsa = RSA.Create(2048);
        var provider = SigningKeyProviderFactory.Create(
            new CentralOptions { SigningKeyPem = rsa.ExportPkcs8PrivateKeyPem() }, new Env("Production"), NullLogger.Instance);

        var local = Assert.IsType<LocalRsaSigningKeyProvider>(provider);
        Assert.False(local.IsEphemeral);
        local.Dispose();
    }

    [Fact]
    public void A_broken_configured_pem_fails_even_in_development_instead_of_silently_using_a_throwaway_key()
    {
        Assert.Throws<SigningKeyConfigurationException>(() =>
            SigningKeyProviderFactory.Create(new CentralOptions { SigningKeyPem = "garbage" }, new Env("Development"), NullLogger.Instance));
    }
}

/// <summary>
/// The Key Vault provider against a fake vault. This covers the logic around the vault (kid, JWKS, rotation, the
/// startup self-check, error messages); the calls to the real Azure SDK cannot be tested without a vault and
/// are UNVERIFIED.
/// </summary>
public class KeyVaultSigningKeyProviderTests
{
    private const string KeyUri = "https://footlook-vault.vault.azure.net/keys/central/0123456789abcdef";

    private sealed class FakeVault : IKeyVaultKeyOperations, IDisposable
    {
        private readonly Dictionary<string, RSA> _keys = new();

        public Exception? ThrowOnGet { get; set; }
        public Exception? ThrowOnSign { get; set; }
        public RSA? SignWithDifferentKey { get; set; }
        public List<string> Signed { get; } = new();

        public RSA Add(string uri, int bits = 2048)
        {
            var rsa = RSA.Create(bits);
            _keys[uri] = rsa;
            return rsa;
        }

        public KeyVaultPublicKey GetPublicKey(Uri keyUri)
        {
            if (ThrowOnGet is not null)
            {
                throw ThrowOnGet;
            }

            if (!_keys.TryGetValue(keyUri.ToString(), out var rsa))
            {
                throw new RequestFailedException(404, "not found");
            }

            var p = rsa.ExportParameters(false);
            return new KeyVaultPublicKey(keyUri.ToString(), p.Modulus!, p.Exponent!);
        }

        public Task<byte[]> SignAsync(string versionedKeyId, byte[] data, CancellationToken cancellationToken)
        {
            if (ThrowOnSign is not null)
            {
                throw ThrowOnSign;
            }

            Signed.Add(versionedKeyId);
            var rsa = SignWithDifferentKey ?? _keys[versionedKeyId];
            return Task.FromResult(rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        public void Dispose()
        {
            foreach (var key in _keys.Values)
            {
                key.Dispose();
            }
        }
    }

    [Fact]
    public async Task Signs_inside_the_vault_and_publishes_the_public_part_of_the_same_key_version()
    {
        using var vault = new FakeVault();
        var rsa = vault.Add(KeyUri);
        var provider = new KeyVaultSigningKeyProvider(vault, KeyUri);

        var data = Encoding.UTF8.GetBytes("header.payload");
        var signature = await provider.SignAsync(data);

        var published = Assert.Single(provider.PublicKeys);
        Assert.Equal(provider.KeyId, published.KeyId);
        Assert.Equal(SigningPublicKey.From(rsa.ExportParameters(false)).KeyId, provider.KeyId);
        Assert.True(rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        // The signing call names the version the public key was read from, and the startup probe ran too.
        Assert.All(vault.Signed, id => Assert.Equal(KeyUri, id));
        Assert.True(vault.Signed.Count >= 2);
    }

    [Fact]
    public void Earlier_vault_keys_and_earlier_pem_keys_stay_published_behind_the_current_one()
    {
        using var vault = new FakeVault();
        vault.Add(KeyUri);
        const string oldUri = "https://footlook-vault.vault.azure.net/keys/central/fedcba9876543210";
        vault.Add(oldUri);
        using var pemKey = RSA.Create(2048);

        var provider = new KeyVaultSigningKeyProvider(
            vault, KeyUri, new[] { oldUri, " " }, new[] { SigningPublicKey.From(pemKey.ExportParameters(false)) });

        Assert.Equal(3, provider.PublicKeys.Count);
        Assert.Equal(provider.KeyId, provider.PublicKeys[0].KeyId);
    }

    [Theory]
    [InlineData("http://footlook-vault.vault.azure.net/keys/central")]
    [InlineData("footlook-vault/keys/central")]
    [InlineData("")]
    public void The_key_address_must_be_an_https_url(string uri)
    {
        using var vault = new FakeVault();
        var ex = Assert.Throws<SigningKeyConfigurationException>(() => new KeyVaultSigningKeyProvider(vault, uri));
        Assert.Contains("KeyVaultKeyUri", ex.Message);
    }

    [Fact]
    public void A_vault_that_refuses_to_hand_over_the_key_stops_startup_with_the_status_and_no_secret()
    {
        using var vault = new FakeVault();
        vault.Add(KeyUri);
        vault.ThrowOnGet = new RequestFailedException(403, "Caller is not authorized: client_secret=hunter2");

        var ex = Assert.Throws<SigningKeyConfigurationException>(() => new KeyVaultSigningKeyProvider(vault, KeyUri));

        Assert.Contains("HTTP 403", ex.Message);
        Assert.Contains("footlook-vault.vault.azure.net", ex.Message);
        Assert.DoesNotContain("hunter2", ex.Message);
    }

    [Fact]
    public void A_missing_key_stops_startup()
    {
        using var vault = new FakeVault();
        var ex = Assert.Throws<SigningKeyConfigurationException>(() => new KeyVaultSigningKeyProvider(vault, KeyUri));
        Assert.Contains("HTTP 404", ex.Message);
    }

    [Fact]
    public void Missing_sign_permission_is_caught_at_startup_not_at_the_first_sign_in()
    {
        using var vault = new FakeVault();
        vault.Add(KeyUri);
        vault.ThrowOnSign = new RequestFailedException(403, "Operation sign is not permitted on this key. secret=hunter2");

        var ex = Assert.Throws<SigningKeyConfigurationException>(() => new KeyVaultSigningKeyProvider(vault, KeyUri));

        Assert.Contains("'sign'", ex.Message);
        Assert.DoesNotContain("hunter2", ex.Message);
    }

    [Fact]
    public void A_signature_that_does_not_verify_against_the_published_key_stops_startup()
    {
        using var vault = new FakeVault();
        vault.Add(KeyUri);
        vault.SignWithDifferentKey = RSA.Create(2048);

        var ex = Assert.Throws<SigningKeyConfigurationException>(() => new KeyVaultSigningKeyProvider(vault, KeyUri));
        Assert.Contains("does not verify", ex.Message);
        vault.SignWithDifferentKey.Dispose();
    }

    [Fact]
    public void A_key_under_2048_bits_is_refused()
    {
        using var vault = new FakeVault();
        vault.Add(KeyUri, 1024);

        var ex = Assert.Throws<SigningKeyConfigurationException>(() => new KeyVaultSigningKeyProvider(vault, KeyUri));
        Assert.Contains("2048", ex.Message);
    }

    [Fact]
    public void The_factory_prefers_the_vault_over_a_pem_and_warns()
    {
        // The factory builds the real Azure client, so only the precedence is checked here: with both set and an
        // address that is not https the vault path is the one that complains.
        using var rsa = RSA.Create(2048);
        var logs = new CapturingLoggerProvider();

        var ex = Assert.Throws<SigningKeyConfigurationException>(() => SigningKeyProviderFactory.Create(
            new CentralOptions { KeyVaultKeyUri = "http://not-https/keys/k", SigningKeyPem = rsa.ExportPkcs8PrivateKeyPem() },
            new Env2(), logs.CreateLogger("t")));

        Assert.Contains("KeyVaultKeyUri", ex.Message);
        Assert.Contains(logs.Lines, l => l.Contains("PEM is ignored"));
    }

    private sealed class Env2 : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
