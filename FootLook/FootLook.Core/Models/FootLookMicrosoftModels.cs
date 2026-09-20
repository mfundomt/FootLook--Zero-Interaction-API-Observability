namespace FootLook.Core.Models
{
    /// <summary>
    /// The identity proven by a validated Microsoft (Entra ID) ID token. The identity KEY is
    /// (<see cref="TenantId"/>, <see cref="Subject"/>) - never the email, which an Entra
    /// tenant admin (or a personal-account owner) can change or, in some tenants, set to an
    /// address they do not own.
    /// </summary>
    public sealed record MicrosoftIdentity(
        string TenantId,
        string Subject,
        string Email,
        string DisplayName,
        string AccountType);

    /// <summary>Request body for POST {EndpointBasePath}/auth/microsoft.</summary>
    public sealed record FootLookMicrosoftSignInRequest(string? IdToken, string? Mode, bool? AcceptedTerms);

    public enum MicrosoftSignInStatus
    {
        /// <summary>The account already existed; its login counters were updated.</summary>
        SignedIn,

        /// <summary>A new account was created and signed in.</summary>
        Registered,

        /// <summary>No account exists for the identity and creating one was not allowed.</summary>
        NotFound,
    }

    public sealed record MicrosoftSignInResult(MicrosoftSignInStatus Status, FootLookUser? User);
}
