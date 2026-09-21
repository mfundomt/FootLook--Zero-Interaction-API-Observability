namespace FootLook.Central.Data;

public static class ProjectRoles
{
    public const string Owner = "owner";
    public const string Member = "member";
}

public sealed record ProjectSummary(string Id, string Name, string Role, int MemberCount, DateTime CreatedAtUtc);

public sealed record ProjectDetail(
    string Id,
    string Name,
    string Role,
    IReadOnlyList<string> AllowedReturnUrls,
    DateTime CreatedAtUtc,
    int MemberCount);

public sealed record ProjectMember(string UserId, string Email, string DisplayName, string Role, DateTime AddedAtUtc);

public sealed record InviteInfo(long Id, DateTime ExpiresAtUtc, int MaxUses, int UsedCount, bool Revoked);

public enum CreateProjectStatus { Created, LimitReached, UnknownUser }

public sealed record CreateProjectResult(CreateProjectStatus Status, ProjectDetail? Project);

public enum CreateInviteStatus { Created, NotOwner, LimitReached, CodeCollision }

public sealed record CreateInviteResult(CreateInviteStatus Status, InviteInfo? Invite);

public enum RedeemStatus { Redeemed, AlreadyMember, InvalidInvite, ProjectFull, UnknownUser }

public sealed record RedeemResult(RedeemStatus Status, string? ProjectId, string? ProjectName);

/// <summary>Thrown by the "no database configured" store; the API answers 503 service_unavailable.</summary>
public sealed class CentralStoreUnavailableException : Exception
{
    public CentralStoreUnavailableException() : base("The central database is not configured.")
    {
    }
}

/// <summary>
/// Persistence for projects, members and invites (schema: FootLook.Data/Sql/002_projects.sql). User ids are
/// the dbo.Users ids. The owner-only operations re-check ownership themselves, so a stale role read by the
/// caller can never let a non-owner change something.
/// </summary>
public interface ICentralStore
{
    /// <summary>Creates the project with the caller as owner and member. LimitReached when they already own <paramref name="maxOwnedProjects"/>.</summary>
    Task<CreateProjectResult> CreateProjectAsync(Guid ownerId, string name, IReadOnlyList<string> returnUrls, int maxOwnedProjects, CancellationToken cancellationToken = default);

    /// <summary>Every project the user owns or belongs to, newest first.</summary>
    Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The user's role in the project, or null when they are not a member (or it does not exist).</summary>
    Task<string?> GetRoleAsync(string projectId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The project as this user sees it, or null when they are not a member.</summary>
    Task<ProjectDetail?> GetProjectAsync(string projectId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Changes the name and/or the return URL list (null leaves it alone). Null result: not the owner.</summary>
    Task<ProjectDetail?> UpdateProjectAsync(string projectId, Guid ownerId, string? name, IReadOnlyList<string>? returnUrls, CancellationToken cancellationToken = default);

    /// <summary>Deletes the project and, by cascade, its members, invites and return URLs. False: not the owner.</summary>
    Task<bool> DeleteProjectAsync(string projectId, Guid ownerId, CancellationToken cancellationToken = default);

    /// <summary>Owner first, then by when they joined.</summary>
    Task<IReadOnlyList<ProjectMember>> ListMembersAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Removes a non-owner member. False: there was no such member (the owner is never removed).</summary>
    Task<bool> RemoveMemberAsync(string projectId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Stores a new invite (by hash only). Live = not revoked, not expired, not used up.</summary>
    Task<CreateInviteResult> CreateInviteAsync(string projectId, Guid ownerId, string codeHash, DateTime expiresAtUtc, int maxUses, int maxLiveInvites, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Newest first. Never contains the code.</summary>
    Task<IReadOnlyList<InviteInfo>> ListInvitesAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Revokes an invite of this project. False: no such invite, or not the owner.</summary>
    Task<bool> RevokeInviteAsync(string projectId, Guid ownerId, long inviteId, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically consumes one use of the invite and adds the user as a member. Unknown, expired, revoked and
    /// used-up invites all give <see cref="RedeemStatus.InvalidInvite"/>. Already a member: nothing is consumed.
    /// </summary>
    Task<RedeemResult> RedeemInviteAsync(Guid userId, string codeHash, int maxMembers, DateTime nowUtc, CancellationToken cancellationToken = default);
}

/// <summary>The store used when no connection string is configured: every call reports the database as unavailable.</summary>
public sealed class UnavailableCentralStore : ICentralStore
{
    private static T Fail<T>() => throw new CentralStoreUnavailableException();

    public Task<CreateProjectResult> CreateProjectAsync(Guid ownerId, string name, IReadOnlyList<string> returnUrls, int maxOwnedProjects, CancellationToken cancellationToken = default) => Fail<Task<CreateProjectResult>>();
    public Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Guid userId, CancellationToken cancellationToken = default) => Fail<Task<IReadOnlyList<ProjectSummary>>>();
    public Task<string?> GetRoleAsync(string projectId, Guid userId, CancellationToken cancellationToken = default) => Fail<Task<string?>>();
    public Task<ProjectDetail?> GetProjectAsync(string projectId, Guid userId, CancellationToken cancellationToken = default) => Fail<Task<ProjectDetail?>>();
    public Task<ProjectDetail?> UpdateProjectAsync(string projectId, Guid ownerId, string? name, IReadOnlyList<string>? returnUrls, CancellationToken cancellationToken = default) => Fail<Task<ProjectDetail?>>();
    public Task<bool> DeleteProjectAsync(string projectId, Guid ownerId, CancellationToken cancellationToken = default) => Fail<Task<bool>>();
    public Task<IReadOnlyList<ProjectMember>> ListMembersAsync(string projectId, CancellationToken cancellationToken = default) => Fail<Task<IReadOnlyList<ProjectMember>>>();
    public Task<bool> RemoveMemberAsync(string projectId, Guid userId, CancellationToken cancellationToken = default) => Fail<Task<bool>>();
    public Task<CreateInviteResult> CreateInviteAsync(string projectId, Guid ownerId, string codeHash, DateTime expiresAtUtc, int maxUses, int maxLiveInvites, DateTime nowUtc, CancellationToken cancellationToken = default) => Fail<Task<CreateInviteResult>>();
    public Task<IReadOnlyList<InviteInfo>> ListInvitesAsync(string projectId, CancellationToken cancellationToken = default) => Fail<Task<IReadOnlyList<InviteInfo>>>();
    public Task<bool> RevokeInviteAsync(string projectId, Guid ownerId, long inviteId, DateTime nowUtc, CancellationToken cancellationToken = default) => Fail<Task<bool>>();
    public Task<RedeemResult> RedeemInviteAsync(Guid userId, string codeHash, int maxMembers, DateTime nowUtc, CancellationToken cancellationToken = default) => Fail<Task<RedeemResult>>();
}
