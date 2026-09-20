namespace FootLook.Tests;

/// <summary>
/// captures.jsonl cannot be purged when a session ends, so it is off by default: captures live
/// in memory only unless the host sets FootLookOptions.EnableFileSink.
/// </summary>
public class FileSinkOptInTests
{
    private static string CaptureFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures.jsonl");

    private static long CaptureFileLength() => File.Exists(CaptureFilePath) ? new FileInfo(CaptureFilePath).Length : 0;

    [Fact]
    public async Task By_default_nothing_is_written_to_the_capture_file()
    {
        await using var host = await FootLookTestHost.StartAsync();
        var before = CaptureFileLength();

        var token = await host.RegisterAndLoginAsync("dev@example.com");
        await host.GetAsync("/hello");
        await host.WaitForCaptureAsync(token, "/hello");
        await Task.Delay(500); // a file write, if there were one, would have landed by now

        Assert.False(host.Options.EnableFileSink);
        Assert.Equal(before, CaptureFileLength());
    }

    [Fact]
    public async Task When_enabled_captures_are_also_appended_to_the_capture_file()
    {
        await using var host = await FootLookTestHost.StartAsync(o => o.EnableFileSink = true);
        var before = CaptureFileLength();

        var token = await host.RegisterAndLoginAsync("dev@example.com");
        await host.GetAsync("/hello");
        await host.WaitForCaptureAsync(token, "/hello");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (CaptureFileLength() <= before && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(CaptureFileLength() > before);
    }
}
