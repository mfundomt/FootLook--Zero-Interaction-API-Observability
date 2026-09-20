using FootLook.Core.Interfaces;
using FootLook.Core.Models;
using FootLook.Core.Options;
using FootLook.Core.Security;

namespace FootLook.Tests;

/// <summary>The in-memory capture store on its own: per-session observers and release when a session ends.</summary>
public class InMemorySinkTests
{
    private static DateTime Future => DateTime.UtcNow.AddHours(1);

    private static CapturedRequest Capture(string path, params string[] sessionIds) =>
        new() { Path = path, ObserverSessionIds = sessionIds.ToList() };

    [Fact]
    public async Task Clear_removes_only_that_session_and_deletes_captures_nobody_else_observes()
    {
        var sink = new InMemorySink(new FootLookOptions());
        await sink.WriteAsync(Capture("/solo-1", "s1"));
        await sink.WriteAsync(Capture("/shared", "s1", "s2"));
        await sink.WriteAsync(Capture("/solo-2", "s2"));

        sink.Clear("s1");

        Assert.Equal(new[] { "/shared", "/solo-2" }, sink.GetAll().Select(c => c.Path));
        Assert.Equal(new[] { "s2" }, sink.GetAll()[0].ObserverSessionIds);

        sink.Clear("s2");
        Assert.Empty(sink.GetAll());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Clear_with_a_missing_or_empty_session_id_clears_nothing(string? sessionId)
    {
        var sink = new InMemorySink(new FootLookOptions());
        await sink.WriteAsync(Capture("/a", "s1"));
        await sink.WriteAsync(Capture("/orphan")); // no observers at all

        sink.Clear(sessionId);

        Assert.Equal(2, sink.GetAll().Count);
    }

    [Fact]
    public async Task Clearing_keeps_capture_order_and_the_size_estimate_consistent()
    {
        // A tiny byte cap so the estimate matters: after clearing, the freed room is reusable.
        var options = new FootLookOptions { MaxInMemoryCaptureBytes = 1000 };
        var sink = new InMemorySink(options);
        await sink.WriteAsync(Capture("/a", "s1") with { RequestBody = new string('x', 100) }); // ~200 bytes each
        await sink.WriteAsync(Capture("/b", "s2") with { RequestBody = new string('x', 100) });
        await sink.WriteAsync(Capture("/c", "s1") with { RequestBody = new string('x', 100) });

        sink.Clear("s1");
        Assert.Equal(new[] { "/b" }, sink.GetAll().Select(c => c.Path));

        // 200 (b) + 4 x 200 = 1000 -> fits exactly, so nothing is evicted; a fifth evicts /b (the oldest).
        for (var i = 0; i < 4; i++)
        {
            await sink.WriteAsync(Capture($"/n{i}", "s2") with { RequestBody = new string('x', 100) });
        }

        Assert.Equal(5, sink.GetAll().Count);
        await sink.WriteAsync(Capture("/n4", "s2") with { RequestBody = new string('x', 100) });
        Assert.DoesNotContain(sink.GetAll(), c => c.Path == "/b");
        Assert.Equal("/n4", sink.GetAll().Last().Path);
    }

    [Fact]
    public async Task A_session_ending_releases_its_captures_when_the_store_is_wired_to_the_session_store()
    {
        var sessions = new ObservationSessionStore();
        var sink = new InMemorySink(new FootLookOptions(), sessions);
        var s1 = sessions.Start("user", Future);
        var s2 = sessions.Start("user", Future);
        await sink.WriteAsync(Capture("/both", s1.SessionId, s2.SessionId));
        await sink.WriteAsync(Capture("/only-1", s1.SessionId));

        sessions.End(s1.SessionId);
        Assert.Equal(new[] { "/both" }, sink.GetAll().Select(c => c.Path));
        Assert.Equal(new[] { s2.SessionId }, sink.GetAll()[0].ObserverSessionIds);

        sessions.End(s2.SessionId);
        Assert.Empty(sink.GetAll());
    }

    [Fact]
    public async Task An_expired_session_releases_its_captures_when_pruned()
    {
        var sessions = new ObservationSessionStore();
        var sink = new InMemorySink(new FootLookOptions(), sessions);
        var doomed = sessions.Start("user", DateTime.UtcNow.AddMilliseconds(150));
        var survivor = sessions.Start("user", Future);
        await sink.WriteAsync(Capture("/x", doomed.SessionId));
        await sink.WriteAsync(Capture("/y", doomed.SessionId, survivor.SessionId));

        await Task.Delay(300);
        sessions.PruneExpired();

        Assert.Equal(new[] { "/y" }, sink.GetAll().Select(c => c.Path));
    }

    [Fact]
    public async Task A_capture_written_after_its_session_already_ended_is_released_straight_away()
    {
        // ShadowMiddleware tags a capture, the queue holds it, the session ends, then it is written.
        var sessions = new ObservationSessionStore();
        var sink = new InMemorySink(new FootLookOptions(), sessions);
        var s1 = sessions.Start("user", Future);
        var s2 = sessions.Start("user", Future);
        var inFlight = Capture("/in-flight", s1.SessionId, s2.SessionId);

        sessions.End(s1.SessionId);
        await sink.WriteAsync(inFlight);

        var stored = Assert.Single(sink.GetAll());
        Assert.Equal(new[] { s2.SessionId }, stored.ObserverSessionIds);

        sessions.End(s2.SessionId);
        await sink.WriteAsync(Capture("/in-flight-2", s2.SessionId));
        Assert.Empty(sink.GetAll());
    }

    [Fact]
    public async Task Concurrent_writes_and_session_ends_do_not_deadlock_or_leak()
    {
        var sessions = new ObservationSessionStore();
        var sink = new InMemorySink(new FootLookOptions { MaxInMemoryCaptures = 100_000 }, sessions);
        var survivor = sessions.Start("user", Future);

        var work = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var session = sessions.Start("user", Future);
                await sink.WriteAsync(Capture($"/w{worker}-{i}", survivor.SessionId, session.SessionId));
                sink.GetAll();
                sessions.End(session.SessionId);
            }
        })).ToArray();

        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(30));

        // Every short-lived session ended; the long-lived one still sees all 1600, each observed by it alone.
        var all = sink.GetAll();
        Assert.Equal(1600, all.Count);
        Assert.All(all, c => Assert.Equal(new[] { survivor.SessionId }, c.ObserverSessionIds));

        sessions.End(survivor.SessionId);
        Assert.Empty(sink.GetAll());
    }
}
