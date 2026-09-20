using System.Text.Json;
using FootLook.Core.Models;
using FootLook.Core.Options;

namespace FootLook.Core.Security
{
    /// <summary>
    /// Default account store: an in-memory index backed by a JSON file so accounts survive a
    /// restart. Account counts on a FootLook instance are tiny (developers, not end users), so
    /// rewriting the whole file per registration is fine and keeps it dependency-free.
    /// The file contains password hashes - keep it out of source control and off any served path.
    /// </summary>
    public sealed class JsonFileUserStore : IFootLookUserStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly object _gate = new();
        private readonly string _path;
        private readonly Dictionary<string, FootLookUser> _byEmail = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FootLookUser> _byId = new(StringComparer.Ordinal);

        public JsonFileUserStore(FootLookOptions options)
        {
            _path = string.IsNullOrWhiteSpace(options.UserStorePath)
                ? Path.Combine(AppContext.BaseDirectory, "footlook-users.json")
                : options.UserStorePath;

            Load();
        }

        public FootLookUser? FindByEmail(string email)
        {
            lock (_gate)
            {
                return _byEmail.GetValueOrDefault(Normalize(email));
            }
        }

        public FootLookUser? FindById(string id)
        {
            lock (_gate)
            {
                return _byId.GetValueOrDefault(id);
            }
        }

        public FootLookUser? TryCreate(string email, string displayName, string passwordHash)
        {
            var normalized = Normalize(email);

            lock (_gate)
            {
                if (_byEmail.ContainsKey(normalized))
                {
                    return null;
                }

                var user = new FootLookUser(
                    Id: Guid.NewGuid().ToString("N"),
                    Email: normalized,
                    DisplayName: displayName,
                    PasswordHash: passwordHash,
                    IsAdmin: _byId.Count == 0,
                    CreatedAtUtc: DateTime.UtcNow);

                _byEmail[normalized] = user;
                _byId[user.Id] = user;

                try
                {
                    Save();
                }
                catch
                {
                    // Don't leave an account that exists in memory but not on disk - it would
                    // silently vanish on restart, and the caller would never know.
                    _byEmail.Remove(normalized);
                    _byId.Remove(user.Id);
                    throw;
                }

                return user;
            }
        }

        private static string Normalize(string email) => email.Trim().ToLowerInvariant();

        private void Load()
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var users = JsonSerializer.Deserialize<List<FootLookUser>>(File.ReadAllText(_path)) ?? new();
            foreach (var user in users)
            {
                _byEmail[Normalize(user.Email)] = user;
                _byId[user.Id] = user;
            }
        }

        private void Save()
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write-then-move so a crash mid-write can't leave a truncated accounts file.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_byId.Values.OrderBy(u => u.CreatedAtUtc), JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }
}
