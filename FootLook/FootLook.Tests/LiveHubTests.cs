using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FootLook.Tests;

/// <summary>
/// Drives the SignalR hub with the raw JSON hub protocol over TestServer's WebSocket client,
/// so no SignalR client package is needed.
/// </summary>
public class LiveHubTests : IAsyncLifetime
{
    private const char RecordSeparator = '\u001e';

    private FootLookTestHost _host = null!;

    public async Task InitializeAsync() => _host = await FootLookTestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Hub_negotiate_requires_a_valid_live_token()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");

        var anonymous = await _host.PostAsync("/footlook/live/negotiate?negotiateVersion=1");
        var garbage = await _host.PostAsync("/footlook/live/negotiate?negotiateVersion=1&footlook_token=garbage");
        var viaQuery = await _host.PostAsync($"/footlook/live/negotiate?negotiateVersion=1&footlook_token={token}");
        var viaHeader = await _host.PostAsync("/footlook/live/negotiate?negotiateVersion=1", token);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viaQuery.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viaHeader.StatusCode);
    }

    [Fact]
    public async Task Hub_rejects_a_token_after_logout()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");
        await _host.PostAsync("/footlook/auth/logout", token);

        var response = await _host.PostAsync($"/footlook/live/negotiate?negotiateVersion=1&footlook_token={token}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Query_string_token_is_not_accepted_on_rest_routes()
    {
        var token = await _host.RegisterAndLoginAsync("dev@example.com");

        var response = await _host.GetAsync($"/footlook/captures?footlook_token={token}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Live_feed_delivers_only_the_connected_accounts_captures_without_observer_ids()
    {
        await _host.RegisterAsync("a@example.com");
        await _host.RegisterAsync("b@example.com");
        var tokenA = await _host.LoginTokenAsync("a@example.com");

        // Only A is logged in when /hello happens; B logs in afterwards and connects to the hub.
        // (Connect A first so its group membership exists before the traffic arrives.)
        using var socketA = await ConnectAsync(tokenA);
        await _host.GetAsync("/hello");
        var messageA = await ReceiveCaptureAsync(socketA);
        Assert.Equal("/hello", messageA.GetProperty("path").GetString());
        Assert.False(messageA.TryGetProperty("observerIds", out _));

        var tokenB = await _host.LoginTokenAsync("b@example.com");
        using var socketB = await ConnectAsync(tokenB);
        await _host.GetAsync("/other");

        // Both are observing /other.
        Assert.Equal("/other", (await ReceiveCaptureAsync(socketA)).GetProperty("path").GetString());
        Assert.Equal("/other", (await ReceiveCaptureAsync(socketB)).GetProperty("path").GetString());
    }

    [Fact]
    public async Task Live_feed_does_not_send_an_account_traffic_it_did_not_observe()
    {
        await _host.RegisterAsync("a@example.com");
        await _host.RegisterAsync("b@example.com");
        var tokenA = await _host.LoginTokenAsync("a@example.com");
        var tokenB = await _host.LoginTokenAsync("b@example.com");

        using var socketB = await ConnectAsync(tokenB);
        await _host.PostAsync("/footlook/auth/logout", tokenB); // B's session ends; its hub connection lingers
        await _host.GetAsync("/hello"); // only A is observing now
        await _host.WaitForCaptureAsync(tokenA, "/hello");

        // Nothing should arrive for B within a short window.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        var received = await TryReceiveCaptureAsync(socketB, cts.Token);
        Assert.Null(received);
    }

    // ---- raw SignalR JSON protocol -------------------------------------------------

    private async Task<WebSocket> ConnectAsync(string token)
    {
        var client = _host.Server.CreateWebSocketClient();
        var socket = await client.ConnectAsync(
            new Uri($"ws://localhost/footlook/live?footlook_token={token}"), CancellationToken.None);

        await SendFrameAsync(socket, "{\"protocol\":\"json\",\"version\":1}");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var handshake = await ReadFrameAsync(socket, cts.Token);
        Assert.Equal("{}", handshake);
        return socket;
    }

    private static async Task SendFrameAsync(WebSocket socket, string json) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(json + RecordSeparator), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string?> ReadFrameAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var sb = new StringBuilder();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return sb.ToString().TrimEnd(RecordSeparator);
            }
        }
    }

    private static async Task<JsonElement?> TryReceiveCaptureAsync(WebSocket socket, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var frame = await ReadFrameAsync(socket, ct);
                if (frame is null)
                {
                    return null;
                }

                foreach (var part in frame.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    using var doc = JsonDocument.Parse(part);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("target", out var target) && target.GetString() == "captureReceived")
                    {
                        return root.GetProperty("arguments")[0].Clone();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    private static async Task<JsonElement> ReceiveCaptureAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var capture = await TryReceiveCaptureAsync(socket, cts.Token);
        Assert.True(capture.HasValue, "Expected a captureReceived message on the live feed within 5s.");
        return capture!.Value;
    }
}
