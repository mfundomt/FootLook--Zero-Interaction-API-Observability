using FootLook.Core.Security;

namespace FootLook.Tests;

public class ObservationSessionStoreTests
{
    private static DateTime Future => DateTime.UtcNow.AddHours(1);

    [Fact]
    public void Start_then_Get_returns_the_session()
    {
        var store = new ObservationSessionStore();

        var session = store.Start("user-a", Future);

        var fetched = store.Get(session.SessionId);
        Assert.NotNull(fetched);
        Assert.Equal("user-a", fetched!.UserId);
        Assert.Equal(session.SessionId, fetched.SessionId);
        Assert.True(store.IsActive(session.SessionId));
    }

    [Fact]
    public void Each_start_gets_a_unique_session_id()
    {
        var store = new ObservationSessionStore();

        var a = store.Start("user-a", Future);
        var b = store.Start("user-a", Future);

        Assert.NotEqual(a.SessionId, b.SessionId);
    }

    [Fact]
    public void End_removes_the_session_and_reports_whether_it_existed()
    {
        var store = new ObservationSessionStore();
        var session = store.Start("user-a", Future);

        Assert.True(store.End(session.SessionId));
        Assert.Null(store.Get(session.SessionId));
        Assert.False(store.IsActive(session.SessionId));
        Assert.False(store.End(session.SessionId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("does-not-exist")]
    public void Unknown_or_empty_session_ids_are_inactive(string? id)
    {
        var store = new ObservationSessionStore();

        Assert.Null(store.Get(id));
        Assert.False(store.IsActive(id));
        Assert.False(store.End(id));
    }

    [Fact]
    public void Expired_sessions_are_not_returned_and_do_not_count_as_active()
    {
        var store = new ObservationSessionStore();
        var expired = store.Start("user-a", DateTime.UtcNow.AddSeconds(-1));

        Assert.Null(store.Get(expired.SessionId));
        Assert.False(store.IsActive(expired.SessionId));
        Assert.Empty(store.GetActiveSessionIds());
    }

    [Fact]
    public void Expired_sessions_are_excluded_from_active_sessions_but_live_ones_remain()
    {
        var store = new ObservationSessionStore();
        store.Start("expired-user", DateTime.UtcNow.AddSeconds(-1));
        var live = store.Start("live-user", Future);

        Assert.Equal(new[] { live.SessionId }, store.GetActiveSessionIds());
    }

    [Fact]
    public void GetActiveSessionIds_is_empty_when_there_are_no_sessions()
    {
        Assert.Empty(new ObservationSessionStore().GetActiveSessionIds());
    }

    [Fact]
    public void GetActiveSessionIds_returns_every_live_session_even_when_several_belong_to_one_user()
    {
        var store = new ObservationSessionStore();
        var a1 = store.Start("user-a", Future);
        var a2 = store.Start("user-a", Future);
        var b = store.Start("user-b", Future);

        var ids = store.GetActiveSessionIds();

        Assert.Equal(3, ids.Count);
        Assert.Contains(a1.SessionId, ids);
        Assert.Contains(a2.SessionId, ids);
        Assert.Contains(b.SessionId, ids);
    }

    [Fact]
    public void Ending_one_of_a_users_sessions_leaves_the_others_live_until_each_ends()
    {
        var store = new ObservationSessionStore();
        var first = store.Start("user-a", Future);
        var second = store.Start("user-a", Future);

        store.End(first.SessionId);
        Assert.Equal(new[] { second.SessionId }, store.GetActiveSessionIds());

        store.End(second.SessionId);
        Assert.Empty(store.GetActiveSessionIds());
    }

    [Fact]
    public void SessionEnded_fires_once_when_a_session_is_ended_by_logout()
    {
        var store = new ObservationSessionStore();
        var ended = new List<string>();
        store.SessionEnded += s => ended.Add(s.SessionId);
        var session = store.Start("user-a", Future);

        store.End(session.SessionId);
        store.End(session.SessionId); // already gone

        Assert.Equal(new[] { session.SessionId }, ended);
    }

    [Fact]
    public void SessionEnded_fires_once_however_an_expired_session_is_noticed()
    {
        var store = new ObservationSessionStore();
        var ended = new List<string>();
        store.SessionEnded += s => ended.Add(s.SessionId);

        var viaGet = store.Start("u", DateTime.UtcNow.AddMilliseconds(100));
        var viaActive = store.Start("u", DateTime.UtcNow.AddMilliseconds(100));
        var viaPrune = store.Start("u", DateTime.UtcNow.AddMilliseconds(100));
        var live = store.Start("u", Future);
        System.Threading.Thread.Sleep(250);

        Assert.Null(store.Get(viaGet.SessionId));
        Assert.Equal(new[] { live.SessionId }, store.GetActiveSessionIds()); // also removes the other expired ones
        store.PruneExpired();
        store.End(viaPrune.SessionId);

        Assert.Equal(3, ended.Count);
        Assert.Equal(new[] { viaGet.SessionId, viaActive.SessionId, viaPrune.SessionId }.OrderBy(x => x), ended.OrderBy(x => x));
    }

    [Fact]
    public void A_throwing_SessionEnded_handler_does_not_break_ending_the_session()
    {
        var store = new ObservationSessionStore();
        store.SessionEnded += _ => throw new InvalidOperationException("boom");
        var session = store.Start("user-a", Future);

        Assert.True(store.End(session.SessionId));
        Assert.False(store.IsActive(session.SessionId));
    }

    [Fact]
    public void SessionEnded_can_take_locks_and_call_back_into_the_store_without_deadlocking()
    {
        var store = new ObservationSessionStore();
        var other = store.Start("user-b", Future);
        store.SessionEnded += _ => store.GetActiveSessionIds(); // re-enters the store
        var session = store.Start("user-a", Future);

        store.End(session.SessionId);

        Assert.True(store.IsActive(other.SessionId));
    }
}
