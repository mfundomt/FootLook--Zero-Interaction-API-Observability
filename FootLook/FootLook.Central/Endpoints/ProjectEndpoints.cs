using FootLook.Central.Data;
using FootLook.Central.Options;
using FootLook.Central.Security;
using FootLook.Core.Security;

namespace FootLook.Central.Endpoints;

internal sealed record CreateProjectRequest(string? Name, string?[]? AllowedReturnUrls);

internal sealed record UpdateProjectRequest(string? Name, string?[]? AllowedReturnUrls);

internal sealed record CreateInviteRequest(int? MaxUses, int? ExpiresInHours);

internal sealed record RedeemInviteRequest(string? Code);

internal sealed record ConnectRequest(string? ReturnUrl);

/// <summary>
/// Projects, members, invites and the connect step that issues a pass. Every route needs a central session
/// token, and every route that names a project decides access itself, on the server, from the database:
/// somebody who is not a member of a project gets 404 project_not_found (never 403, so ids cannot be probed),
/// before anything else about the request - even its body - is looked at.
/// </summary>
internal static class ProjectEndpoints
{
    public const int MaxNameLength = 100;
    public const int MaxOwnedProjects = 10;
    public const int MaxLiveInvites = 20;
    public const int MaxMembers = 25;
    public const int DefaultInviteUses = 1;
    public const int DefaultInviteHours = 168;

    internal static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var projects = endpoints.MapGroup("/projects")
            .RequireCors(AuthEndpoints.SiteCorsPolicy)
            .RequireAuthorization();

        projects.MapPost("", CreateAsync);
        projects.MapGet("", ListAsync);
        projects.MapGet("/{id}", GetAsync);
        projects.MapPatch("/{id}", UpdateAsync);
        projects.MapDelete("/{id}", DeleteAsync);

        projects.MapGet("/{id}/members", ListMembersAsync);
        projects.MapDelete("/{id}/members/{userId}", RemoveMemberAsync);

        projects.MapPost("/{id}/invites", CreateInviteAsync);
        projects.MapGet("/{id}/invites", ListInvitesAsync);
        projects.MapDelete("/{id}/invites/{inviteId}", RevokeInviteAsync);

        projects.MapPost("/{id}/connect", ConnectAsync).RequireRateLimiting(RateLimitPolicies.Connect);

        endpoints.MapPost("/invites/redeem", RedeemAsync)
            .RequireCors(AuthEndpoints.SiteCorsPolicy)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Redeem);

        return endpoints;
    }

    // ---- projects -------------------------------------------------------------------------------

    private static async Task<IResult> CreateAsync(HttpContext http, ICentralStore store)
    {
        if (!Api.TryGetUserId(http.User, out var userId))
        {
            return Unauthorized();
        }

        var body = await Api.ReadBodyAsync<CreateProjectRequest>(http.Request, Api.MaxSmallBodyBytes, http.RequestAborted);
        if (body.Value is null ||
            !TryValidateName(body.Value.Name, out var name) ||
            !TryValidateReturnUrls(body.Value.AllowedReturnUrls, out var urls))
        {
            return Api.InvalidRequest();
        }

        var result = await store.CreateProjectAsync(userId, name, urls, MaxOwnedProjects, http.RequestAborted);
        return result.Status switch
        {
            CreateProjectStatus.LimitReached => Api.Error(StatusCodes.Status409Conflict, "project_limit"),
            CreateProjectStatus.UnknownUser => Unauthorized(),
            _ => Results.Json(
                new
                {
                    id = result.Project!.Id,
                    name = result.Project.Name,
                    role = ProjectRoles.Owner,
                    allowedReturnUrls = result.Project.AllowedReturnUrls,
                    createdAtUtc = result.Project.CreatedAtUtc,
                },
                statusCode: StatusCodes.Status201Created),
        };
    }

    private static async Task<IResult> ListAsync(HttpContext http, ICentralStore store)
    {
        if (!Api.TryGetUserId(http.User, out var userId))
        {
            return Unauthorized();
        }

        return Results.Ok(await store.ListProjectsAsync(userId, http.RequestAborted));
    }

    private static async Task<IResult> GetAsync(string id, HttpContext http, ICentralStore store)
    {
        if (!Api.TryGetUserId(http.User, out var userId))
        {
            return Unauthorized();
        }

        var project = ProjectIds.IsWellFormed(id) ? await store.GetProjectAsync(id, userId, http.RequestAborted) : null;
        return project is null ? Api.ProjectNotFound() : Results.Ok(project);
    }

    private static async Task<IResult> UpdateAsync(string id, HttpContext http, ICentralStore store)
    {
        var access = await RequireRoleAsync(id, http, store, ownerOnly: true);
        if (access.Failure is not null)
        {
            return access.Failure;
        }

        var body = await Api.ReadBodyAsync<UpdateProjectRequest>(http.Request, Api.MaxSmallBodyBytes, http.RequestAborted);
        if (body.Value is null || (body.Value.Name is null && body.Value.AllowedReturnUrls is null))
        {
            return Api.InvalidRequest();
        }

        string? name = null;
        if (body.Value.Name is not null && !TryValidateName(body.Value.Name, out name))
        {
            return Api.InvalidRequest();
        }

        IReadOnlyList<string>? urls = null;
        if (body.Value.AllowedReturnUrls is not null)
        {
            if (!TryValidateReturnUrls(body.Value.AllowedReturnUrls, out var validated))
            {
                return Api.InvalidRequest();
            }

            urls = validated;
        }

        var updated = await store.UpdateProjectAsync(id, access.UserId, name, urls, http.RequestAborted);
        return updated is null ? Api.ProjectNotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> DeleteAsync(string id, HttpContext http, ICentralStore store)
    {
        var access = await RequireRoleAsync(id, http, store, ownerOnly: true);
        if (access.Failure is not null)
        {
            return access.Failure;
        }

        return await store.DeleteProjectAsync(id, access.UserId, http.RequestAborted)
            ? Results.NoContent()
            : Api.ProjectNotFound();
    }

    // ---- members --------------------------------------------------------------------------------

    private static async Task<IResult> ListMembersAsync(string id, HttpContext http, ICentralStore store)
    {
        var access = await RequireRoleAsync(id, http, store, ownerOnly: false);
        if (access.Failure is not null)
        {
            return access.Failure;
        }

        return Results.Ok(await store.ListMembersAsync(id, http.RequestAborted));
    }

    private static async Task<IResult> RemoveMemberAsync(string id, string userId, HttpContext http, ICentralStore store)
    {
        var access = await RequireRoleAsync(id, http, store, ownerOnly: false);
        if (access.Failure is not null)
        {
            return access.Failure;
        }

        if (!Guid.TryParse(userId, out var target))
        {
            return Api.Error(StatusCodes.Status404NotFound, "member_not_found");
        }

        var isSelf = target == access.UserId;
        if (access.Role == ProjectRoles.Owner)
        {
            // The owner can remove anyone except the owner - and the owner is the only one who could be themselves here.
            if (isSelf)
            {
                return Api.Error(StatusCodes.Status400BadRequest, "cannot_remove_owner");
            }
        }
        else if (!isSelf)
        {
            // A plain member may only remove themselves.
            return Api.Error(StatusCodes.Status403Forbidden, "forbidden");
        }

        return await store.RemoveMemberAsync(id, target, http.RequestAborted)
            ? Results.NoContent()
            : Api.Error(StatusCodes.Status404NotFound, "member_not_found");
    }

    // ---- invites --------------------------------------------------------------------------------

    private static async Task<IResult> CreateInviteAsync(string id, HttpContext http, ICentralStore store, TimeProvider time)
    {
        var access = await RequireRoleAsync(id, http, store, ownerOnly: true);
        if (access.Failure is not null)
        {
            return access.Failure;
        }

        var body = await Api.ReadBodyAsync<CreateInviteRequest>(http.Request, Api.MaxSmallBodyBytes, http.RequestAborted);
        if (body.Kind == Api.BodyKind.Invalid)
        {
            return Api.InvalidRequest();
        }

        // An empty body means "the defaults".
        var maxUses = body.Value?.MaxUses ?? DefaultInviteUses;
        var hours = body.Value?.ExpiresInHours ?? DefaultInviteHours;
        if (maxUses is < 1 or > 25 || hours is < 1 or > 720)
        {
            return Api.InvalidRequest();
        }

        var now = Api.TruncateToSeconds(time.GetUtcNow().UtcDateTime);
        var expiresAtUtc = now.AddHours(hours);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var code = InviteCodes.Generate();
            var result = await store.CreateInviteAsync(
                id, access.UserId, InviteCodes.Hash(code), expiresAtUtc, maxUses, MaxLiveInvites, now, http.RequestAborted);

            switch (result.Status)
            {
                case CreateInviteStatus.Created:
                    // The one and only time the code exists outside a hash.
                    return Results.Json(
                        new { id = result.Invite!.Id, code, expiresAtUtc = result.Invite.ExpiresAtUtc, maxUses = result.Invite.MaxUses },
                        statusCode: StatusCodes.Status201Created);
                case CreateInviteStatus.LimitReached:
                    return Api.Error(StatusCodes.Status409Conflict, "invite_limit");
                case CreateInviteStatus.NotOwner:
                    return Api.ProjectNotFound();
            }

            // CodeCollision (2^-50): draw another code.
        }

        return Api.Error(StatusCodes.Status503ServiceUnavailable, "service_unavailable");
    }

    private static async Task<IResult> ListInvitesAsync(string id, HttpContext http, ICentralStore store)
    {
        var access = await RequireRoleAsync(id, http, store, ownerOnly: true);
        if (access.Failure is not null)
        {
            return access.Failure;
        }

        return Results.Ok(await store.ListInvitesAsync(id, http.RequestAborted));
    }

    private static async Task<IResult> RevokeInviteAsync(string id, string inviteId, HttpContext http, ICentralStore store, TimeProvider time)
    {
        var access = await RequireRoleAsync(id, http, store, ownerOnly: true);
        if (access.Failure is not null)
        {
            return access.Failure;
        }

        if (!long.TryParse(inviteId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var invite))
        {
            return Api.Error(StatusCodes.Status404NotFound, "invite_not_found");
        }

        var now = Api.TruncateToSeconds(time.GetUtcNow().UtcDateTime);
        return await store.RevokeInviteAsync(id, access.UserId, invite, now, http.RequestAborted)
            ? Results.NoContent()
            : Api.Error(StatusCodes.Status404NotFound, "invite_not_found");
    }

    private static async Task<IResult> RedeemAsync(HttpContext http, ICentralStore store, RedeemThrottle throttle, TimeProvider time)
    {
        if (!Api.TryGetUserId(http.User, out var userId))
        {
            return Unauthorized();
        }

        // Too many recent failures: refuse before the code is even looked at.
        if (throttle.IsBlocked(userId))
        {
            return Api.Error(StatusCodes.Status429TooManyRequests, "too_many_attempts");
        }

        var body = await Api.ReadBodyAsync<RedeemInviteRequest>(http.Request, Api.MaxSmallBodyBytes, http.RequestAborted);
        if (body.Value is null || string.IsNullOrWhiteSpace(body.Value.Code))
        {
            return Api.InvalidRequest();
        }

        // Unknown, malformed, expired, revoked and used-up all look exactly the same, and all count as a failure.
        var invalid = Api.Error(StatusCodes.Status404NotFound, "invalid_invite");
        if (!InviteCodes.TryNormalize(body.Value.Code, out var code))
        {
            throttle.RecordFailure(userId);
            return invalid;
        }

        var now = Api.TruncateToSeconds(time.GetUtcNow().UtcDateTime);
        var result = await store.RedeemInviteAsync(userId, InviteCodes.Hash(code), MaxMembers, now, http.RequestAborted);
        switch (result.Status)
        {
            case RedeemStatus.Redeemed:
                return Results.Ok(new { projectId = result.ProjectId, name = result.ProjectName, role = ProjectRoles.Member });
            case RedeemStatus.AlreadyMember:
                // Unchanged. (The owner redeeming their own code is told they are the owner.)
                var role = await store.GetRoleAsync(result.ProjectId!, userId, http.RequestAborted) ?? ProjectRoles.Member;
                return Results.Ok(new { projectId = result.ProjectId, name = result.ProjectName, role });
            case RedeemStatus.ProjectFull:
                return Api.Error(StatusCodes.Status409Conflict, "project_full");
            case RedeemStatus.UnknownUser:
                return Unauthorized();
            default:
                throttle.RecordFailure(userId);
                return invalid;
        }
    }

    // ---- connect --------------------------------------------------------------------------------

    private static async Task<IResult> ConnectAsync(
        string id, HttpContext http, ICentralStore store, CentralTokenService tokens, ILoggerFactory loggerFactory)
    {
        if (!Api.TryGetUserId(http.User, out var userId))
        {
            return Unauthorized();
        }

        // The authorisation decision: is the caller the owner or a member of this project? Nothing else is
        // looked at first, so a stranger learns nothing about the project, its return URLs or the body rules.
        var project = ProjectIds.IsWellFormed(id) ? await store.GetProjectAsync(id, userId, http.RequestAborted) : null;
        if (project is null)
        {
            return Api.ProjectNotFound();
        }

        var body = await Api.ReadBodyAsync<ConnectRequest>(http.Request, Api.MaxSmallBodyBytes, http.RequestAborted);
        if (body.Value is null || body.Value.ReturnUrl is null)
        {
            return Api.InvalidRequest();
        }

        if (!ReturnUrlPolicy.TryMatch(body.Value.ReturnUrl, project.AllowedReturnUrls, out var returnUrl))
        {
            return Api.Error(StatusCodes.Status400BadRequest, "invalid_return_url");
        }

        var accounts = http.RequestServices.GetService<IFootLookMicrosoftAccountStore>();
        if (accounts is null)
        {
            return Api.Error(StatusCodes.Status503ServiceUnavailable, "accounts_unavailable");
        }

        // The pass carries the user's current email and name, so read them, not the (older) session token.
        var user = await accounts.FindByIdAsync(userId.ToString("N"), http.RequestAborted);
        if (user is null)
        {
            return Unauthorized();
        }

        var pass = await tokens.IssuePassAsync(user, project.Id, project.Role, http.RequestAborted);
        return Results.Ok(new { pass = pass.Token, expiresAtUtc = pass.ExpiresAtUtc, returnUrl });
    }

    // ---- helpers --------------------------------------------------------------------------------

    private readonly record struct Access(Guid UserId, string Role, IResult? Failure);

    /// <summary>
    /// The server-side authorisation for a project route: not a member (or not a valid id) is 404
    /// project_not_found; a member where the owner is required is 403 forbidden.
    /// </summary>
    private static async Task<Access> RequireRoleAsync(string projectId, HttpContext http, ICentralStore store, bool ownerOnly)
    {
        if (!Api.TryGetUserId(http.User, out var userId))
        {
            return new Access(Guid.Empty, string.Empty, Unauthorized());
        }

        var role = ProjectIds.IsWellFormed(projectId) ? await store.GetRoleAsync(projectId, userId, http.RequestAborted) : null;
        if (role is null)
        {
            return new Access(userId, string.Empty, Api.ProjectNotFound());
        }

        if (ownerOnly && role != ProjectRoles.Owner)
        {
            return new Access(userId, role, Api.Error(StatusCodes.Status403Forbidden, "forbidden"));
        }

        return new Access(userId, role, null);
    }

    private static IResult Unauthorized() => Api.Error(StatusCodes.Status401Unauthorized, "unauthorized");

    private static bool TryValidateName(string? value, out string name)
    {
        name = value?.Trim() ?? string.Empty;
        return name.Length is >= 1 and <= MaxNameLength && !name.Any(char.IsControl);
    }

    private static bool TryValidateReturnUrls(string?[]? values, out List<string> urls)
    {
        urls = new List<string>();
        if (values is null)
        {
            return true;
        }

        if (values.Length > ReturnUrlPolicy.MaxAllowedUrls)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (!ReturnUrlPolicy.TryNormalizeAllowed(value, out var normalized))
            {
                return false;
            }

            if (!urls.Contains(normalized, StringComparer.Ordinal))
            {
                urls.Add(normalized);
            }
        }

        return true;
    }
}
