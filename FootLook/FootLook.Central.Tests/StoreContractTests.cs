using FootLook.Central.Data;

namespace FootLook.Central.Tests;

/// <summary>
/// The rules every <see cref="ICentralStore"/> must keep. The in-memory store runs them as ordinary tests; the SQL
/// store runs the very same methods against a real database (see SqlCentralStoreTests), so the fake the HTTP tests
/// lean on cannot drift from the SQL that ships.
/// </summary>
public abstract class CentralStoreContractTests : IAsyncLifetime
{
    protected abstract ICentralStore Store { get; }

    /// <summary>Creates a user the store will accept as an owner or member.</summary>
    protected abstract Task<Guid> NewUserAsync(string label);

    public virtual Task InitializeAsync() => Task.CompletedTask;

    public virtual Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime Now = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private async Task<string> NewProjectAsync(Guid owner, string name = "P", params string[] urls)
    {
        var result = await Store.CreateProjectAsync(owner, name, urls, 10);
        Assert.Equal(CreateProjectStatus.Created, result.Status);
        return result.Project!.Id;
    }

    private async Task<string> NewInviteAsync(string project, Guid owner, int maxUses = 1, int hours = 24, string? hash = null)
    {
        hash ??= InviteHash();
        var result = await Store.CreateInviteAsync(project, owner, hash, Now.AddHours(hours), maxUses, 20, Now);
        Assert.Equal(CreateInviteStatus.Created, result.Status);
        return hash;
    }

    private static string InviteHash() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    // ---- projects ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_created_project_has_the_owner_as_its_only_member()
    {
        var owner = await NewUserAsync("owner");

        var result = await Store.CreateProjectAsync(owner, "Billing", new[] { "https://b.example/x", "http://localhost:5103/f" }, 10);

        Assert.Equal(CreateProjectStatus.Created, result.Status);
        var project = result.Project!;
        Assert.Matches("^prj_[a-z2-7]{16}$", project.Id);
        Assert.Equal("owner", project.Role);
        Assert.Equal(1, project.MemberCount);
        Assert.Equal(DateTimeKind.Utc, project.CreatedAtUtc.Kind);
        Assert.InRange((DateTime.UtcNow - project.CreatedAtUtc).TotalMinutes, -5, 5);

        var read = await Store.GetProjectAsync(project.Id, owner);
        Assert.Equal("Billing", read!.Name);
        Assert.Equal(new[] { "http://localhost:5103/f", "https://b.example/x" }, read.AllowedReturnUrls.OrderBy(u => u, StringComparer.Ordinal).ToArray());
        Assert.Equal("owner", await Store.GetRoleAsync(project.Id, owner));
        var member = Assert.Single(await Store.ListMembersAsync(project.Id));
        Assert.Equal(("owner", owner.ToString("N")), (member.Role, member.UserId));
    }

    [Fact]
    public async Task The_owned_project_limit_is_enforced_per_owner()
    {
        var owner = await NewUserAsync("owner");
        var other = await NewUserAsync("other");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(CreateProjectStatus.Created, (await Store.CreateProjectAsync(owner, $"p{i}", Array.Empty<string>(), 3)).Status);
        }

        var over = await Store.CreateProjectAsync(owner, "over", Array.Empty<string>(), 3);

        Assert.Equal(CreateProjectStatus.LimitReached, over.Status);
        Assert.Null(over.Project);
        Assert.Equal(CreateProjectStatus.Created, (await Store.CreateProjectAsync(other, "fine", Array.Empty<string>(), 3)).Status);
        Assert.Equal(3, (await Store.ListProjectsAsync(owner)).Count);
    }

    [Fact]
    public async Task Parallel_creations_cannot_slip_past_the_owned_project_limit()
    {
        var owner = await NewUserAsync("owner");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Store.CreateProjectAsync(owner, $"p{i}", Array.Empty<string>(), 3)));

        Assert.Equal(3, results.Count(r => r.Status == CreateProjectStatus.Created));
        Assert.Equal(5, results.Count(r => r.Status == CreateProjectStatus.LimitReached));
        Assert.Equal(3, (await Store.ListProjectsAsync(owner)).Count);
    }

    [Fact]
    public async Task A_user_that_does_not_exist_cannot_own_a_project()
    {
        var result = await Store.CreateProjectAsync(Guid.NewGuid(), "ghost", Array.Empty<string>(), 10);

        Assert.Equal(CreateProjectStatus.UnknownUser, result.Status);
    }

    [Fact]
    public async Task Text_that_looks_like_sql_is_stored_as_plain_text()
    {
        var owner = await NewUserAsync("owner");
        const string name = "Robert'); DROP TABLE dbo.Projects;--";

        var id = await NewProjectAsync(owner, name, "https://a.example/x'--");

        Assert.Equal(name, (await Store.GetProjectAsync(id, owner))!.Name);
        Assert.Single(await Store.ListProjectsAsync(owner));
    }

    [Fact]
    public async Task Names_can_hold_unicode()
    {
        var owner = await NewUserAsync("owner");
        var id = await NewProjectAsync(owner, "Ｆｏｏｔ Look ☃ Über 日本");

        Assert.Equal("Ｆｏｏｔ Look ☃ Über 日本", (await Store.GetProjectAsync(id, owner))!.Name);
    }

    [Fact]
    public async Task Listing_covers_owned_and_joined_projects_with_the_callers_role_and_counts()
    {
        var owner = await NewUserAsync("owner");
        var friend = await NewUserAsync("friend");
        var mine = await NewProjectAsync(owner, "Mine");
        var theirs = await NewProjectAsync(friend, "Theirs");
        await NewProjectAsync(await NewUserAsync("stranger"), "Secret");
        var hash = await NewInviteAsync(theirs, friend);
        Assert.Equal(RedeemStatus.Redeemed, (await Store.RedeemInviteAsync(owner, hash, 25, Now)).Status);

        var list = await Store.ListProjectsAsync(owner);

        Assert.Equal(2, list.Count);
        var mineRow = list.Single(p => p.Id == mine);
        var theirsRow = list.Single(p => p.Id == theirs);
        Assert.Equal(("owner", 1), (mineRow.Role, mineRow.MemberCount));
        Assert.Equal(("member", 2), (theirsRow.Role, theirsRow.MemberCount));
    }

    [Fact]
    public async Task Non_members_see_nothing()
    {
        var owner = await NewUserAsync("owner");
        var stranger = await NewUserAsync("stranger");
        var id = await NewProjectAsync(owner);

        Assert.Null(await Store.GetProjectAsync(id, stranger));
        Assert.Null(await Store.GetRoleAsync(id, stranger));
        Assert.Null(await Store.GetRoleAsync("prj_zzzzzzzzzzzzzzzz", owner));
        Assert.Empty(await Store.ListProjectsAsync(stranger));
    }

    [Fact]
    public async Task Only_the_owner_can_update_and_a_partial_update_keeps_the_rest()
    {
        var owner = await NewUserAsync("owner");
        var member = await NewUserAsync("member");
        var id = await NewProjectAsync(owner, "Old", "https://old.example/x");
        Assert.Equal(RedeemStatus.Redeemed, (await Store.RedeemInviteAsync(member, await NewInviteAsync(id, owner), 25, Now)).Status);

        Assert.Null(await Store.UpdateProjectAsync(id, member, "Hacked", null));
        Assert.Null(await Store.UpdateProjectAsync(id, await NewUserAsync("stranger"), "Hacked", null));
        Assert.Equal("Old", (await Store.GetProjectAsync(id, owner))!.Name);

        var renamed = await Store.UpdateProjectAsync(id, owner, "New", null);
        Assert.Equal(("New", 1), (renamed!.Name, renamed.AllowedReturnUrls.Count));

        var relisted = await Store.UpdateProjectAsync(id, owner, null, new[] { "https://a.example/1", "https://a.example/2" });
        Assert.Equal("New", relisted!.Name);
        Assert.Equal(2, relisted.AllowedReturnUrls.Count);
        Assert.Equal(2, relisted.MemberCount);

        var cleared = await Store.UpdateProjectAsync(id, owner, null, Array.Empty<string>());
        Assert.Empty(cleared!.AllowedReturnUrls);
    }

    [Fact]
    public async Task Only_the_owner_can_delete_and_deleting_cascades_to_members_and_invites()
    {
        var owner = await NewUserAsync("owner");
        var member = await NewUserAsync("member");
        var id = await NewProjectAsync(owner, "Doomed", "https://a.example/x");
        var hash = await NewInviteAsync(id, owner, maxUses: 5);
        await Store.RedeemInviteAsync(member, hash, 25, Now);

        Assert.False(await Store.DeleteProjectAsync(id, member));
        Assert.False(await Store.DeleteProjectAsync(id, await NewUserAsync("stranger")));
        Assert.NotNull(await Store.GetProjectAsync(id, owner));

        Assert.True(await Store.DeleteProjectAsync(id, owner));

        Assert.Null(await Store.GetProjectAsync(id, owner));
        Assert.Null(await Store.GetRoleAsync(id, member));
        Assert.Empty(await Store.ListMembersAsync(id));
        Assert.Empty(await Store.ListInvitesAsync(id));
        Assert.Equal(RedeemStatus.InvalidInvite, (await Store.RedeemInviteAsync(await NewUserAsync("late"), hash, 25, Now)).Status);
        Assert.False(await Store.DeleteProjectAsync(id, owner));
        Assert.Empty(await Store.ListProjectsAsync(member));
    }

    // ---- members ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Members_are_listed_owner_first_with_their_account_details()
    {
        var owner = await NewUserAsync("owner");
        var a = await NewUserAsync("aaa");
        var b = await NewUserAsync("bbb");
        var id = await NewProjectAsync(owner);
        var hash = await NewInviteAsync(id, owner, maxUses: 5);
        await Store.RedeemInviteAsync(a, hash, 25, Now);
        await Store.RedeemInviteAsync(b, hash, 25, Now.AddMinutes(1));

        var members = await Store.ListMembersAsync(id);

        Assert.Equal(new[] { "owner", "member", "member" }, members.Select(m => m.Role).ToArray());
        Assert.Equal(owner.ToString("N"), members[0].UserId);
        Assert.Equal(new[] { a.ToString("N"), b.ToString("N") }, members.Skip(1).Select(m => m.UserId).ToArray());
        Assert.All(members, m => Assert.EndsWith("@example.invalid", m.Email));
        Assert.All(members, m => Assert.False(string.IsNullOrEmpty(m.DisplayName)));
        Assert.All(members, m => Assert.Equal(DateTimeKind.Utc, m.AddedAtUtc.Kind));
    }

    [Fact]
    public async Task Removing_a_member_works_and_the_owner_can_never_be_removed()
    {
        var owner = await NewUserAsync("owner");
        var member = await NewUserAsync("member");
        var id = await NewProjectAsync(owner);
        await Store.RedeemInviteAsync(member, await NewInviteAsync(id, owner), 25, Now);

        Assert.False(await Store.RemoveMemberAsync(id, owner));
        Assert.False(await Store.RemoveMemberAsync(id, await NewUserAsync("stranger")));
        Assert.Equal(2, (await Store.ListMembersAsync(id)).Count);

        Assert.True(await Store.RemoveMemberAsync(id, member));
        Assert.False(await Store.RemoveMemberAsync(id, member));
        Assert.Null(await Store.GetRoleAsync(id, member));
        Assert.Single(await Store.ListMembersAsync(id));
    }

    // ---- invites: create / list / revoke -------------------------------------------------------------------------------

    [Fact]
    public async Task An_invite_is_stored_by_hash_and_listed_without_the_code()
    {
        var owner = await NewUserAsync("owner");
        var id = await NewProjectAsync(owner);

        var created = await Store.CreateInviteAsync(id, owner, InviteHash(), Now.AddHours(48), 3, 20, Now);

        Assert.Equal(CreateInviteStatus.Created, created.Status);
        var invite = Assert.Single(await Store.ListInvitesAsync(id));
        Assert.Equal(created.Invite!.Id, invite.Id);
        Assert.Equal((3, 0, false), (invite.MaxUses, invite.UsedCount, invite.Revoked));
        Assert.Equal(Now.AddHours(48), invite.ExpiresAtUtc);
        Assert.Equal(DateTimeKind.Utc, invite.ExpiresAtUtc.Kind);
    }

    [Fact]
    public async Task Only_the_owner_can_create_or_revoke_invites()
    {
        var owner = await NewUserAsync("owner");
        var member = await NewUserAsync("member");
        var stranger = await NewUserAsync("stranger");
        var id = await NewProjectAsync(owner);
        var hash = await NewInviteAsync(id, owner, maxUses: 3);
        await Store.RedeemInviteAsync(member, hash, 25, Now);
        var inviteId = (await Store.ListInvitesAsync(id)).Single().Id;

        Assert.Equal(CreateInviteStatus.NotOwner, (await Store.CreateInviteAsync(id, member, InviteHash(), Now.AddHours(1), 1, 20, Now)).Status);
        Assert.Equal(CreateInviteStatus.NotOwner, (await Store.CreateInviteAsync(id, stranger, InviteHash(), Now.AddHours(1), 1, 20, Now)).Status);
        Assert.Equal(CreateInviteStatus.NotOwner, (await Store.CreateInviteAsync("prj_zzzzzzzzzzzzzzzz", owner, InviteHash(), Now.AddHours(1), 1, 20, Now)).Status);
        Assert.False(await Store.RevokeInviteAsync(id, member, inviteId, Now));
        Assert.False(await Store.RevokeInviteAsync(id, stranger, inviteId, Now));
        Assert.False((await Store.ListInvitesAsync(id)).Single().Revoked);
    }

    [Fact]
    public async Task The_live_invite_limit_counts_only_live_invites()
    {
        var owner = await NewUserAsync("owner");
        var id = await NewProjectAsync(owner);
        var hashes = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            hashes.Add(await NewInviteAsync(id, owner));
        }

        var over = await Store.CreateInviteAsync(id, owner, InviteHash(), Now.AddHours(1), 1, 3, Now);
        Assert.Equal(CreateInviteStatus.LimitReached, over.Status);

        // Used up: frees a slot.
        await Store.RedeemInviteAsync(await NewUserAsync("u1"), hashes[0], 25, Now);
        Assert.Equal(CreateInviteStatus.Created, (await Store.CreateInviteAsync(id, owner, InviteHash(), Now.AddHours(1), 1, 3, Now)).Status);

        // Revoked: frees a slot.
        var revoke = (await Store.ListInvitesAsync(id)).First().Id;
        Assert.True(await Store.RevokeInviteAsync(id, owner, revoke, Now));
        Assert.Equal(CreateInviteStatus.Created, (await Store.CreateInviteAsync(id, owner, InviteHash(), Now.AddHours(1), 1, 3, Now)).Status);
        Assert.Equal(CreateInviteStatus.LimitReached, (await Store.CreateInviteAsync(id, owner, InviteHash(), Now.AddHours(1), 1, 3, Now)).Status);

        // Expired: frees slots.
        Assert.Equal(CreateInviteStatus.Created, (await Store.CreateInviteAsync(id, owner, InviteHash(), Now.AddHours(30), 1, 3, Now.AddHours(25))).Status);
    }

    [Fact]
    public async Task Two_invites_with_the_same_hash_collide_instead_of_overwriting()
    {
        var owner = await NewUserAsync("owner");
        var id = await NewProjectAsync(owner);
        var hash = await NewInviteAsync(id, owner);

        var again = await Store.CreateInviteAsync(id, owner, hash, Now.AddHours(1), 1, 20, Now);

        Assert.Equal(CreateInviteStatus.CodeCollision, again.Status);
        Assert.Single(await Store.ListInvitesAsync(id));
    }

    [Fact]
    public async Task Revoking_is_idempotent_and_needs_the_right_project()
    {
        var owner = await NewUserAsync("owner");
        var id = await NewProjectAsync(owner);
        var other = await NewProjectAsync(owner);
        await NewInviteAsync(id, owner);
        var inviteId = (await Store.ListInvitesAsync(id)).Single().Id;

        Assert.False(await Store.RevokeInviteAsync(other, owner, inviteId, Now));
        Assert.True(await Store.RevokeInviteAsync(id, owner, inviteId, Now));
        Assert.True(await Store.RevokeInviteAsync(id, owner, inviteId, Now.AddHours(1)));
        Assert.False(await Store.RevokeInviteAsync(id, owner, inviteId + 1000, Now));
        Assert.True((await Store.ListInvitesAsync(id)).Single().Revoked);
    }

    // ---- invites: redeeming ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Redeeming_adds_a_member_and_consumes_one_use()
    {
        var owner = await NewUserAsync("owner");
        var tester = await NewUserAsync("tester");
        var id = await NewProjectAsync(owner, "Named");
        var hash = await NewInviteAsync(id, owner, maxUses: 2);

        var result = await Store.RedeemInviteAsync(tester, hash, 25, Now);

        Assert.Equal(new RedeemResult(RedeemStatus.Redeemed, id, "Named"), result);
        Assert.Equal("member", await Store.GetRoleAsync(id, tester));
        Assert.Equal(1, (await Store.ListInvitesAsync(id)).Single().UsedCount);
    }

    [Fact]
    public async Task Unknown_expired_revoked_and_used_up_invites_are_all_simply_invalid()
    {
        var owner = await NewUserAsync("owner");
        var id = await NewProjectAsync(owner);
        var expired = await NewInviteAsync(id, owner, hours: 1);
        var revoked = await NewInviteAsync(id, owner, hours: 24);
        var revokedId = (await Store.ListInvitesAsync(id)).First().Id;
        Assert.True(await Store.RevokeInviteAsync(id, owner, revokedId, Now));
        var usedUp = await NewInviteAsync(id, owner, hours: 24);
        Assert.Equal(RedeemStatus.Redeemed, (await Store.RedeemInviteAsync(await NewUserAsync("first"), usedUp, 25, Now)).Status);

        var late = Now.AddHours(2);
        foreach (var hash in new[] { InviteHash(), expired, revoked, usedUp })
        {
            var user = await NewUserAsync("late");
            var result = await Store.RedeemInviteAsync(user, hash, 25, late);
            Assert.Equal(new RedeemResult(RedeemStatus.InvalidInvite, null, null), result);
            Assert.Null(await Store.GetRoleAsync(id, user));
        }
    }

    [Fact]
    public async Task An_invite_is_valid_until_the_moment_it_expires()
    {
        var owner = await NewUserAsync("owner");
        var id = await NewProjectAsync(owner);
        var hash = await NewInviteAsync(id, owner, maxUses: 3, hours: 2);

        Assert.Equal(RedeemStatus.Redeemed, (await Store.RedeemInviteAsync(await NewUserAsync("a"), hash, 25, Now.AddMinutes(119))).Status);
        Assert.Equal(RedeemStatus.InvalidInvite, (await Store.RedeemInviteAsync(await NewUserAsync("b"), hash, 25, Now.AddHours(2))).Status);
        Assert.Equal(RedeemStatus.InvalidInvite, (await Store.RedeemInviteAsync(await NewUserAsync("c"), hash, 25, Now.AddDays(30))).Status);
    }

    [Fact]
    public async Task An_existing_member_gets_AlreadyMember_and_nothing_is_consumed_even_for_a_used_up_code()
    {
        var owner = await NewUserAsync("owner");
        var member = await NewUserAsync("member");
        var id = await NewProjectAsync(owner, "Named");
        var hash = await NewInviteAsync(id, owner, maxUses: 1);
        await Store.RedeemInviteAsync(member, hash, 25, Now);

        var again = await Store.RedeemInviteAsync(member, hash, 25, Now);
        var ownerAgain = await Store.RedeemInviteAsync(owner, hash, 25, Now);

        Assert.Equal(new RedeemResult(RedeemStatus.AlreadyMember, id, "Named"), again);
        Assert.Equal(RedeemStatus.AlreadyMember, ownerAgain.Status);
        Assert.Equal(1, (await Store.ListInvitesAsync(id)).Single().UsedCount);
        Assert.Equal(2, (await Store.ListMembersAsync(id)).Count);
    }

    [Fact]
    public async Task A_revoked_or_expired_code_does_not_reveal_membership()
    {
        var owner = await NewUserAsync("owner");
        var member = await NewUserAsync("member");
        var id = await NewProjectAsync(owner);
        var hash = await NewInviteAsync(id, owner, maxUses: 5, hours: 1);
        await Store.RedeemInviteAsync(member, hash, 25, Now);

        // After expiry even a member gets the same "invalid" as everyone else.
        Assert.Equal(RedeemStatus.InvalidInvite, (await Store.RedeemInviteAsync(member, hash, 25, Now.AddHours(2))).Status);
    }

    [Fact]
    public async Task A_full_project_refuses_without_consuming_and_frees_up_when_someone_leaves()
    {
        var owner = await NewUserAsync("owner");
        var id = await NewProjectAsync(owner);
        var hash = await NewInviteAsync(id, owner, maxUses: 5);
        var first = await NewUserAsync("first");
        Assert.Equal(RedeemStatus.Redeemed, (await Store.RedeemInviteAsync(first, hash, 2, Now)).Status);

        var second = await NewUserAsync("second");
        var full = await Store.RedeemInviteAsync(second, hash, 2, Now);

        Assert.Equal(RedeemStatus.ProjectFull, full.Status);
        Assert.Equal(1, (await Store.ListInvitesAsync(id)).Single().UsedCount);
        Assert.Equal(2, (await Store.ListMembersAsync(id)).Count);

        await Store.RemoveMemberAsync(id, first);
        Assert.Equal(RedeemStatus.Redeemed, (await Store.RedeemInviteAsync(second, hash, 2, Now)).Status);
    }

    [Fact]
    public async Task A_user_that_does_not_exist_cannot_redeem_and_consumes_nothing()
    {
        var owner = await NewUserAsync("owner");
        var id = await NewProjectAsync(owner);
        var hash = await NewInviteAsync(id, owner);

        var result = await Store.RedeemInviteAsync(Guid.NewGuid(), hash, 25, Now);

        Assert.Equal(RedeemStatus.UnknownUser, result.Status);
        Assert.Equal(0, (await Store.ListInvitesAsync(id)).Single().UsedCount);
        Assert.Single(await Store.ListMembersAsync(id));
    }

    [Fact]
    public async Task A_code_with_N_uses_admits_exactly_N_people_however_many_race_for_it()
    {
        var owner = await NewUserAsync("owner");
        var id = await NewProjectAsync(owner);
        var hash = await NewInviteAsync(id, owner, maxUses: 3);
        var contenders = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            contenders.Add(await NewUserAsync("racer" + i));
        }

        var results = await Task.WhenAll(contenders.Select(u => Store.RedeemInviteAsync(u, hash, 25, Now)));

        Assert.Equal(3, results.Count(r => r.Status == RedeemStatus.Redeemed));
        Assert.Equal(7, results.Count(r => r.Status == RedeemStatus.InvalidInvite));
        Assert.Equal(3, (await Store.ListInvitesAsync(id)).Single().UsedCount);
        Assert.Equal(4, (await Store.ListMembersAsync(id)).Count);
    }

    [Fact]
    public async Task The_same_person_racing_with_themself_is_admitted_once_and_uses_one_slot()
    {
        var owner = await NewUserAsync("owner");
        var id = await NewProjectAsync(owner);
        var hash = await NewInviteAsync(id, owner, maxUses: 5);
        var user = await NewUserAsync("twice");

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Store.RedeemInviteAsync(user, hash, 25, Now)));

        Assert.Equal(1, results.Count(r => r.Status == RedeemStatus.Redeemed));
        Assert.Equal(5, results.Count(r => r.Status == RedeemStatus.AlreadyMember));
        Assert.Equal(1, (await Store.ListInvitesAsync(id)).Single().UsedCount);
        Assert.Equal(2, (await Store.ListMembersAsync(id)).Count);
    }

    [Fact]
    public async Task Racing_redemptions_through_different_codes_cannot_overfill_a_project()
    {
        var owner = await NewUserAsync("owner");
        var id = await NewProjectAsync(owner);
        var codes = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            codes.Add(await NewInviteAsync(id, owner, maxUses: 5));
        }

        var users = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            users.Add(await NewUserAsync("filler" + i));
        }

        // Room for 4 in all (owner + 3): eight people, four different codes, all at once.
        var results = await Task.WhenAll(users.Select((u, i) => Store.RedeemInviteAsync(u, codes[i % codes.Count], 4, Now)));

        Assert.Equal(3, results.Count(r => r.Status == RedeemStatus.Redeemed));
        Assert.Equal(5, results.Count(r => r.Status == RedeemStatus.ProjectFull));
        Assert.Equal(4, (await Store.ListMembersAsync(id)).Count);
    }
}

public class InMemoryCentralStoreContractTests : CentralStoreContractTests
{
    private readonly FakeAccountStore _accounts = new();
    private readonly InMemoryCentralStore _store;

    public InMemoryCentralStoreContractTests() => _store = new InMemoryCentralStore(_accounts);

    protected override ICentralStore Store => _store;

    protected override Task<Guid> NewUserAsync(string label) =>
        Task.FromResult(Guid.Parse(_accounts.AddUser($"{label}-{Guid.NewGuid():N}@example.invalid", label).Id));
}
