namespace FootLook.Core.Models
{
    /// <summary>
    /// A developer account on a FootLook instance. Only accounts can log in, and logging in
    /// is what opens the observation session that turns capture on - see
    /// <see cref="Security.ObservationSessionStore"/>.
    /// </summary>
    public sealed record FootLookUser(
        string Id,
        string Email,
        string DisplayName,
        string PasswordHash,
        bool IsAdmin,
        DateTime CreatedAtUtc);

    /// <summary>The account fields that are safe to return to a client (never the password hash).</summary>
    public sealed record FootLookUserSummary(string Id, string Email, string DisplayName, bool IsAdmin, DateTime CreatedAtUtc)
    {
        public static FootLookUserSummary From(FootLookUser user) =>
            new(user.Id, user.Email, user.DisplayName, user.IsAdmin, user.CreatedAtUtc);
    }
}
