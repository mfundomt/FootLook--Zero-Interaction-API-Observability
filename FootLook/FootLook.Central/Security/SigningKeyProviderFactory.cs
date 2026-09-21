using FootLook.Central.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FootLook.Central.Security;

/// <summary>Chooses the signing key from configuration; called once at startup so a bad setup stops the app.</summary>
public static class SigningKeyProviderFactory
{
    public static ISigningKeyProvider Create(CentralOptions options, IHostEnvironment environment, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.KeyVaultKeyUri))
        {
            if (!string.IsNullOrWhiteSpace(options.SigningKeyPem))
            {
                logger.LogWarning("Central:KeyVaultKeyUri and Central:SigningKeyPem are both set; the Key Vault key is used and the PEM is ignored. Remove the PEM setting.");
            }

            var previousPublic = LocalRsaSigningKeyProvider.ParsePublicKeys(options.PreviousSigningKeyPems, "Central:PreviousSigningKeyPems");
            return KeyVaultSigningKeyProvider.Create(options.KeyVaultKeyUri, options.KeyVaultPreviousKeyUris, previousPublic);
        }

        if (!string.IsNullOrWhiteSpace(options.SigningKeyPem))
        {
            return LocalRsaSigningKeyProvider.FromPem(options.SigningKeyPem, options.PreviousSigningKeyPems);
        }

        if (environment.IsDevelopment())
        {
            return LocalRsaSigningKeyProvider.CreateEphemeral(logger);
        }

        throw new SigningKeyConfigurationException(
            "No signing key is configured. Set Central:SigningKeyPem (a PKCS8 RSA private key in PEM form, as an application setting) " +
            "or Central:KeyVaultKeyUri (an Azure Key Vault RSA key). Only the Development environment may run without one.");
    }
}
