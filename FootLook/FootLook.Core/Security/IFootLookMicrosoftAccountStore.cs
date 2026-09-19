using FootLook.Core.Models;

namespace FootLook.Core.Security
{
    /// <summary>
    /// Persistence for accounts that sign in with Microsoft (Entra ID). It sits beside
    /// <see cref="IFootLookUserStore"/> (email + password accounts) rather than replacing it:
    /// the two are keyed differently - a Microsoft account is (tenant id, subject), never
    /// an email - so they are separate contracts. FootLook.Data provides the SQL
    /// implementation (AddFootLookSqlAccounts). When no implementation is registered,
    /// POST {EndpointBasePath}/auth/microsoft answers 503 accounts_unavailable.
    /// </summary>
    public interface IFootLookMicrosoftAccountStore
    {
        /// <summary>
        /// Signs the identity in as one atomic step: finds the account by (TenantId, Subject),
        /// or - when <paramref name="createIfMissing"/> is true - creates it. Every sign-in
        /// bumps the login counters, refreshes the email and display name, and records a
        /// login event. The very first account ever created on the instance becomes its
        /// admin; that decision must be race-free.
        /// </summary>
        /// <param name="acceptedTerms">True when the caller registered with the terms accepted.</param>
        Task<MicrosoftSignInResult> SignInAsync(
            MicrosoftIdentity identity,
            bool createIfMissing,
            bool acceptedTerms,
            string? ipAddress,
            string? userAgent,
            CancellationToken cancellationToken = default);

        /// <summary>The account with this FootLook user id, or null.</summary>
        Task<FootLookUser?> FindByIdAsync(string id, CancellationToken cancellationToken = default);
    }
}
