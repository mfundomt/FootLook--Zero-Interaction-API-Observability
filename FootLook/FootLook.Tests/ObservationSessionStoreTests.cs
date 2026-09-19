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
    public void Expired_sessions_are_not_returned_and_do_not_count_as_active_users()
    {
        var store = new ObservationSessionStore();
        var expired = store.Start("user-a", DateTime.UtcNow.AddSeconds(-1));

        Assert.Null(store.Get(expired.SessionId));
        Assert.False(store.IsActive(expired.SessionId));
        Assert.Empty(store.GetActiveUserIds());
    }

    [Fact]
    public void Expired_sessions_are_excluded_from_active_users_but_live_ones_remain()
    {
        var store = new ObservationSessionStore();
        store.Start("expired-user", DateTime.UtcNow.AddSeconds(-1));
        store.Start("live-user", Future);

        Assert.Equal(new[] { "live-user" }, store.GetActiveUserIds());
    }

    [Fact]
    public void GetActiveUserIds_is_empty_when_there_are_no_sessions()
    {
        Assert.Empty(new ObservationSessionStore().GetActiveUserIds());
    }

    [Fact]
    public void GetActiveUserIds_returns_distinct_users_even_with_several_sessions_each()
    {
        var store = new ObservationSessionStore();
        store.Start("user-a", Future);
        store.Start("user-a", Future);
        store.Start("user-b", Future);

        var ids = store.GetActiveUserIds();

        Assert.Equal(2, ids.Count);
        Assert.Contains("user-a", ids);
        Assert.Contains("user-b", ids);
    }

    [Fact]
    public void Ending_one_of_a_users_sessions_keeps_the_user_active_until_the_last_ends()
    {
        var store = new ObservationSessionStore();
        var first = store.Start("user-a", Future);
        var second = store.Start("user-a", Future);

        store.End(first.SessionId);
        Assert.Equal(new[] { "user-a" }, store.GetActiveUserIds());

        store.End(second.SessionId);
        Assert.Empty(store.GetActiveUserIds());
    }
}
