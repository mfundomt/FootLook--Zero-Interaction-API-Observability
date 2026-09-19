using FootLook.Core.Models;

namespace FootLook.Core.Security
{
    /// <summary>
    /// Where FootLook developer accounts live. The default (<see cref="JsonFileUserStore"/>)
    /// persists to a local JSON file; register your own implementation before calling
    /// AddFootLook to keep accounts somewhere else (the default is added with TryAdd).
    /// </summary>
    public interface IFootLookUserStore
    {
        FootLookUser? FindByEmail(string email);

        FootLookUser? FindById(string id);

        /// <summary>
        /// Creates the account, or returns null when the email is already registered. The
        /// very first account created on an instance is made an admin (the developer who set
        /// FootLook up) - that decision has to happen inside the store so two simultaneous
        /// first registrations can't both become admin.
        /// </summary>
        FootLookUser? TryCreate(string email, string displayName, string passwordHash);
    }
}
