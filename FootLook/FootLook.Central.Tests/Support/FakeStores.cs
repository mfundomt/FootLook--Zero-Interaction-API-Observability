using FootLook.Central.Data;
using FootLook.Core.Models;
using FootLook.Core.Security;

namespace FootLook.Central.Tests;

/// <summary>A clock a test can move. Starts at the real time so tokens it stamps are valid for the real JWT checks.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null) => _now = start ?? DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }
}

/// <summary>An in-memory account store with the same rules as the SQL one, plus helpers to create users directly.</summary>
public sealed class FakeAccountStore : IFootLookMicrosoftAccountStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, string Subject), FootLookUser> _users = new();

    public int SignInCalls { get; private set; }
    public Exception? ThrowOnSignIn { get; set; }
    public List<string> Outcomes { get; } = new();

    public IReadOnlyCollection<FootLookUser> Users
    {
        get { lock (_gate) { return _users.Values.ToList(); } }
    }

    /// <summary>Creates an account without going through Microsoft (tests mint a central token for it).</summary>
    public FootLookUser AddUser(string email, string displayName = "Test User", bool isAdmin = false)
    {
        lock (_gate)
        {
            var user = new FootLookUser(Guid.NewGuid().ToString("N"), email, displayName, string.Empty, isAdmin, DateTime.UtcNow);
            _users[("test-tenant", user.Id)] = user;
            return user;
        }
    }

    public void Add(FootLookUser user)
    {
        lock (_gate)
        {
            _users[("test-tenant", user.Id)] = user;
        }
    }

    public void Rename(string userId, string displayName)
    {
        lock (_gate)
        {
            var entry = _users.First(u => u.Value.Id == userId);
            _users[entry.Key] = entry.Value with { DisplayName = displayName };
        }
    }

    public void Remove(string userId)
    {
        lock (_gate)
        {
            var entry = _users.First(u => u.Value.Id == userId);
            _users.Remove(entry.Key);
        }
    }

    public Task<MicrosoftSignInResult> SignInAsync(
        MicrosoftIdentity identity, bool createIfMissing, bool acceptedTerms, string? ipAddress, string? userAgent,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            SignInCalls++;
            if (ThrowOnSignIn is not null)
            {
                throw ThrowOnSignIn;
            }

            var key = (identity.TenantId, identity.Subject);
            var existing = _users.TryGetValue(key, out var user);
            if (!existing)
            {
                if (!createIfMissing)
                {
                    return Task.FromResult(new MicrosoftSignInResult(MicrosoftSignInStatus.NotFound, null));
                }

                user = new FootLookUser(Guid.NewGuid().ToString("N"), identity.Email, identity.DisplayName, string.Empty,
                    IsAdmin: _users.Count == 0, DateTime.UtcNow);
            }
            else
            {
                user = user! with { Email = identity.Email, DisplayName = identity.DisplayName };
            }

            _users[key] = user!;
            Outcomes.Add(existing ? "login" : "register");
            return Task.FromResult(new MicrosoftSignInResult(existing ? MicrosoftSignInStatus.SignedIn : MicrosoftSignInStatus.Registered, user));
        }
    }

    public Task<FootLookUser?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!Guid.TryParse(id, out var wanted))
            {
                return Task.FromResult<FootLookUser?>(null);
            }

            return Task.FromResult(_users.Values.FirstOrDefault(u => Guid.Parse(u.Id) == wanted));
        }
    }
}

/// <summary>
/// The rules of <see cref="SqlCentralStore"/> in memory, so the HTTP tests need no database. The same contract
/// tests run against both, which keeps the two honest.
/// </summary>
public sealed class InMemoryCentralStore : ICentralStore
{
    private sealed class Invite
    {
        public long Id;
        public string Hash = "";
        public DateTime Expires;
        public int MaxUses;
        public int Used;
        public DateTime? Revoked;
    }

    private sealed class Project
    {
        public string Id = "";
        public string Name = "";
        public Guid Owner;
        public DateTime Created;
        public List<string> Urls = new();
        public List<(Guid User, string Role, DateTime Added)> Members = new();
        public List<Invite> Invites = new();
    }

    private readonly object _gate = new();
    private readonly FakeAccountStore _accounts;
    private readonly Dictionary<string, Project> _projects = new();
    private long _nextInvite = 1;

    public Exception? ThrowOnEveryCall { get; set; }

    public InMemoryCentralStore(FakeAccountStore accounts) => _accounts = accounts;

    /// <summary>Everything stored for invites: what a database dump would contain.</summary>
    public IReadOnlyList<string> AllStoredInviteHashes()
    {
        lock (_gate)
        {
            return _projects.Values.SelectMany(p => p.Invites).Select(i => i.Hash).ToList();
        }
    }

    /// <summary>Adds a member straight to a project (test setup, e.g. to fill it up).</summary>
    public void AddMember(string projectId, Guid userId)
    {
        lock (_gate)
        {
            _projects[projectId].Members.Add((userId, ProjectRoles.Member, DateTime.UtcNow));
        }
    }

    public int ProjectCount
    {
        get { lock (_gate) { return _projects.Count; } }
    }

    private void Guard()
    {
        if (ThrowOnEveryCall is not null)
        {
            throw ThrowOnEveryCall;
        }
    }

    private bool UserExists(Guid id) => _accounts.FindByIdAsync(id.ToString("N")).GetAwaiter().GetResult() is not null;

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime Seconds(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static ProjectDetail Detail(Project p, string role) =>
        new(p.Id, p.Name, role, p.Urls.OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList(), p.Created, p.Members.Count);

    public Task<CreateProjectResult> CreateProjectAsync(Guid ownerId, string name, IReadOnlyList<string> returnUrls, int maxOwnedProjects, CancellationToken cancellationToken = default)
    {
        Guard();
        lock (_gate)
        {
            if (!UserExists(ownerId))
            {
                return Task.FromResult(new CreateProjectResult(CreateProjectStatus.UnknownUser, null));
            }

            if (_projects.Values.Count(p => p.Owner == ownerId) >= maxOwnedProjects)
            {
                return Task.FromResult(new CreateProjectResult(CreateProjectStatus.LimitReached, null));
            }

            var project = new Project { Id = ProjectIds.New(), Name = name, Owner = ownerId, Created = Seconds(DateTime.UtcNow), Urls = returnUrls.ToList() };
            project.Members.Add((ownerId, ProjectRoles.Owner, project.Created));
            _projects[project.Id] = project;
            return Task.FromResult(new CreateProjectResult(CreateProjectStatus.Created, Detail(project, ProjectRoles.Owner)));
        }
    }

    public Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        Guard();
        lock (_gate)
        {
            IReadOnlyList<ProjectSummary> list = _projects.Values
                .Where(p => p.Members.Any(m => m.User == userId))
                .OrderByDescending(p => p.Created).ThenBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => new ProjectSummary(p.Id, p.Name, p.Members.First(m => m.User == userId).Role, p.Members.Count, p.Created))
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<string?> GetRoleAsync(string projectId, Guid userId, CancellationToken cancellationToken = default)
    {
        Guard();
        lock (_gate)
        {
            return Task.FromResult(_projects.TryGetValue(projectId, out var p) && p.Members.Any(m => m.User == userId)
                ? p.Members.First(m => m.User == userId).Role
                : null);
        }
    }

    public Task<ProjectDetail?> GetProjectAsync(string projectId, Guid userId, CancellationToken cancellationToken = default)
    {
        Guard();
        lock (_gate)
        {
            return Task.FromResult(_projects.TryGetValue(projectId, out var p) && p.Members.Any(m => m.User == userId)
                ? Detail(p, p.Members.First(m => m.User == userId).Role)
                : null);
        }
    }

    public Task<ProjectDetail?> UpdateProjectAsync(string projectId, Guid ownerId, string? name, IReadOnlyList<string>? returnUrls, CancellationToken cancellationToken = default)
    {
        Guard();
        lock (_gate)
        {
            if (!_projects.TryGetValue(projectId, out var p) || p.Owner != ownerId)
            {
                return Task.FromResult<ProjectDetail?>(null);
            }

            if (name is not null)
            {
                p.Name = name;
            }

            if (returnUrls is not null)
            {
                p.Urls = returnUrls.ToList();
            }

            return Task.FromResult<ProjectDetail?>(Detail(p, ProjectRoles.Owner));
        }
    }

    public Task<bool> DeleteProjectAsync(string projectId, Guid ownerId, CancellationToken cancellationToken = default)
    {
        Guard();
        lock (_gate)
        {
            return Task.FromResult(_projects.TryGetValue(projectId, out var p) && p.Owner == ownerId && _projects.Remove(projectId));
        }
    }

    public Task<IReadOnlyList<ProjectMember>> ListMembersAsync(string projectId, CancellationToken cancellationToken = default)
    {
        Guard();
        lock (_gate)
        {
            if (!_projects.TryGetValue(projectId, out var p))
            {
                return Task.FromResult<IReadOnlyList<ProjectMember>>(Array.Empty<ProjectMember>());
            }

            IReadOnlyList<ProjectMember> list = p.Members
                .Select(m => (Member: m, User: _accounts.FindByIdAsync(m.User.ToString("N")).GetAwaiter().GetResult()!))
                .OrderBy(x => x.Member.Role == ProjectRoles.Owner ? 0 : 1).ThenBy(x => x.Member.Added).ThenBy(x => x.User.Email, StringComparer.OrdinalIgnoreCase)
                .Select(x => new ProjectMember(x.Member.User.ToString("N"), x.User.Email, x.User.DisplayName, x.Member.Role, Utc(x.Member.Added)))
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<bool> RemoveMemberAsync(string projectId, Guid userId, CancellationToken cancellationToken = default)
    {
        Guard();
        lock (_gate)
        {
            return Task.FromResult(_projects.TryGetValue(projectId, out var p) &&
                                   p.Members.RemoveAll(m => m.User == userId && m.Role != ProjectRoles.Owner) > 0);
        }
    }

    public Task<CreateInviteResult> CreateInviteAsync(string projectId, Guid ownerId, string codeHash, DateTime expiresAtUtc, int maxUses, int maxLiveInvites, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        Guard();
        lock (_gate)
        {
            if (!_projects.TryGetValue(projectId, out var p) || p.Owner != ownerId)
            {
                return Task.FromResult(new CreateInviteResult(CreateInviteStatus.NotOwner, null));
            }

            var live = p.Invites.Count(i => i.Revoked is null && i.Expires > nowUtc && i.Used < i.MaxUses);
            if (live >= maxLiveInvites)
            {
                return Task.FromResult(new CreateInviteResult(CreateInviteStatus.LimitReached, null));
            }

            if (_projects.Values.SelectMany(x => x.Invites).Any(i => i.Hash == codeHash))
            {
                return Task.FromResult(new CreateInviteResult(CreateInviteStatus.CodeCollision, null));
            }

            var invite = new Invite { Id = _nextInvite++, Hash = codeHash, Expires = Seconds(expiresAtUtc), MaxUses = maxUses };
            p.Invites.Add(invite);
            return Task.FromResult(new CreateInviteResult(CreateInviteStatus.Created, new InviteInfo(invite.Id, invite.Expires, maxUses, 0, false)));
        }
    }

    public Task<IReadOnlyList<InviteInfo>> ListInvitesAsync(string projectId, CancellationToken cancellationToken = default)
    {
        Guard();
        lock (_gate)
        {
            IReadOnlyList<InviteInfo> list = _projects.TryGetValue(projectId, out var p)
                ? p.Invites.OrderByDescending(i => i.Id).Select(i => new InviteInfo(i.Id, i.Expires, i.MaxUses, i.Used, i.Revoked is not null)).ToList()
                : Array.Empty<InviteInfo>();
            return Task.FromResult(list);
        }
    }

    public Task<bool> RevokeInviteAsync(string projectId, Guid ownerId, long inviteId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        Guard();
        lock (_gate)
        {
            if (!_projects.TryGetValue(projectId, out var p) || p.Owner != ownerId)
            {
                return Task.FromResult(false);
            }

            var invite = p.Invites.FirstOrDefault(i => i.Id == inviteId);
            if (invite is null)
            {
                return Task.FromResult(false);
            }

            invite.Revoked ??= nowUtc;
            return Task.FromResult(true);
        }
    }

    public Task<RedeemResult> RedeemInviteAsync(Guid userId, string codeHash, int maxMembers, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        Guard();
        lock (_gate)
        {
            var invalid = new RedeemResult(RedeemStatus.InvalidInvite, null, null);
            var match = _projects.Values
                .SelectMany(p => p.Invites.Select(i => (Project: p, Invite: i)))
                .FirstOrDefault(x => x.Invite.Hash == codeHash && x.Invite.Revoked is null && x.Invite.Expires > nowUtc);
            if (match.Invite is null)
            {
                return Task.FromResult(invalid);
            }

            var (project, invite) = match;
            if (project.Members.Any(m => m.User == userId))
            {
                return Task.FromResult(new RedeemResult(RedeemStatus.AlreadyMember, project.Id, project.Name));
            }

            if (invite.Used >= invite.MaxUses)
            {
                return Task.FromResult(invalid);
            }

            if (project.Members.Count >= maxMembers)
            {
                return Task.FromResult(new RedeemResult(RedeemStatus.ProjectFull, project.Id, project.Name));
            }

            if (!UserExists(userId))
            {
                return Task.FromResult(new RedeemResult(RedeemStatus.UnknownUser, null, null));
            }

            invite.Used++;
            project.Members.Add((userId, ProjectRoles.Member, Seconds(nowUtc)));
            return Task.FromResult(new RedeemResult(RedeemStatus.Redeemed, project.Id, project.Name));
        }
    }
}
