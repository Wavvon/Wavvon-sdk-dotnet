using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;

namespace Wavvon.Sdk.Tests;

public sealed class ParseTests
{
    [Fact]
    public void TypesKnownFrames()
    {
        var e = Assert.IsType<ReplayUnavailableEvent>(WsEvent.Parse("""{"type":"replay_unavailable","earliest_seq":5,"earliest_at":1}"""));
        Assert.Equal(5, e.EarliestSeq);
        var typing = Assert.IsType<TypingEvent>(WsEvent.Parse("""{"type":"typing","channel_id":"c","public_key":"p","display_name":null,"typing":true}"""));
        Assert.True(typing.IsTyping);
    }

    [Fact]
    public void ParsesAMessageIntoTheGeneratedModel()
    {
        var e = Assert.IsType<MessageEvent>(WsEvent.Parse(
            """{"type":"message","channel_id":"c","message":{"id":"m","channel_id":"c","sender":"s","content":"hi","created_at":1760000000000,"embeds":[]}}"""));
        Assert.Equal("hi", e.Message.Content);
        Assert.Equal(1760000000000, e.Message.CreatedAt);
    }

    [Fact]
    public void DeliversUnknownTypesWithTheRawFrame()
    {
        const string raw = """{"type":"voice_zone_created","zone_id":"z"}""";
        Assert.Equal(new UnknownEvent("voice_zone_created", raw), WsEvent.Parse(raw));
    }

    [Fact]
    public void DeliversGarbageAndMisshapenFramesAsUnknownToo()
    {
        Assert.Equal("", Assert.IsType<UnknownEvent>(WsEvent.Parse("{nope")).RawType);
        Assert.Equal("hub_event", Assert.IsType<UnknownEvent>(WsEvent.Parse("""{"type":"hub_event","seq":"x"}""")).RawType);
    }

    [Fact]
    public void SerialisesClientMessagesWithTheirWireType()
    {
        var json = JsonSerializer.Serialize<object>(new MiniAppMessage("a", "c", "p", "k"), WavvonJson.Options);
        Assert.Contains("\"type\":\"mini_app_message\"", json, StringComparison.Ordinal);
        Assert.Contains("\"to_pubkey\":\"k\"", json, StringComparison.Ordinal);
    }
}

/// <summary>Real WebSockets over loopback TCP; each connect hands the hub side to the test.</summary>
internal sealed class LoopbackHub : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<IDisposable> _owned = [];
    public Channel<WebSocket> Accepted { get; } = Channel.CreateUnbounded<WebSocket>();
    public int Connects { get; private set; }

    public LoopbackHub() => _listener.Start();

    public async Task<WebSocket> ConnectAsync(CancellationToken cancellationToken)
    {
        Connects++;
        var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)_listener.LocalEndpoint, cancellationToken);
        var server = await _listener.AcceptTcpClientAsync(cancellationToken);
        lock (_owned)
        {
            _owned.Add(client);
            _owned.Add(server);
        }
        await Accepted.Writer.WriteAsync(WebSocket.CreateFromStream(server.GetStream(), isServer: true, subProtocol: null, Timeout.InfiniteTimeSpan), cancellationToken);
        return WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _listener.Stop();
        lock (_owned)
        {
            _owned.ForEach(o => o.Dispose());
        }
    }
}

internal static class HubSide
{
    public static Task SendAsync(this WebSocket socket, object frame) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);

    public static async Task<JsonElement> ReceiveJsonAsync(this WebSocket socket)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

    public static Task CloseAsync(this WebSocket socket) =>
        socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, TestContext.Current.CancellationToken);

    public static object HubEvent(long seq) => new { type = "hub_event", seq, @event = "member.joined", hub_url = "h", at = 1, payload = new { } };
}

public sealed class EventConnectionTests
{
    private static async Task<T> Next<T>(IAsyncEnumerator<WsEvent> events) where T : WsEvent
    {
        Assert.True(await events.MoveNextAsync());
        return Assert.IsType<T>(events.Current);
    }

    [Fact]
    public async Task ResumesFromTheLastSeqAfterAReconnectAndDropsReplayDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        using var hub = new LoopbackHub();
        var reauths = 0;
        await using var connection = new EventConnection(hub.ConnectAsync, _ => { reauths++; return Task.CompletedTask; }, Fast.Retry, null);
        var states = new List<ConnectionState>();
        connection.StateChanged += (_, s) => states.Add(s);
        var events = connection.ReadAllAsync(ct).GetAsyncEnumerator(ct);

        var first = await hub.Accepted.Reader.ReadAsync(ct);
        await first.SendAsync(new { type = "hello", live_seq = 10 });
        Assert.Equal(10, (await Next<HelloEvent>(events)).LiveSeq);
        await first.SendAsync(HubSide.HubEvent(11));
        await first.SendAsync(HubSide.HubEvent(12));
        Assert.Equal(11, (await Next<HubEvent>(events)).Seq);
        Assert.Equal(12, (await Next<HubEvent>(events)).Seq);
        await first.CloseAsync();

        var second = await hub.Accepted.Reader.ReadAsync(ct);
        await second.SendAsync(new { type = "hello", live_seq = 20 });
        var resume = await second.ReceiveJsonAsync();
        Assert.Equal("resume", resume.GetProperty("type").GetString());
        Assert.Equal(12, resume.GetProperty("since_seq").GetInt64());
        await second.SendAsync(HubSide.HubEvent(12));
        await second.SendAsync(HubSide.HubEvent(13));
        await second.SendAsync(new { type = "replay_unavailable", earliest_seq = 1, earliest_at = 2 });

        await Next<HelloEvent>(events);
        Assert.Equal(13, (await Next<HubEvent>(events)).Seq);
        await Next<ReplayUnavailableEvent>(events);
        Assert.Equal(13, connection.LastSeq);
        Assert.Equal([ConnectionState.Open, ConnectionState.Reconnecting, ConnectionState.Open], states);
        Assert.Equal(0, reauths);
    }

    [Fact]
    public async Task ReauthenticatesWhenAConnectionDiesBeforeHello()
    {
        var ct = TestContext.Current.CancellationToken;
        using var hub = new LoopbackHub();
        var reauths = 0;
        var connection = new EventConnection(hub.ConnectAsync, _ => { reauths++; return Task.CompletedTask; }, Fast.Retry, new EventConnectionOptions { SinceSeq = 4 });

        await (await hub.Accepted.Reader.ReadAsync(ct)).CloseAsync();
        var second = await hub.Accepted.Reader.ReadAsync(ct);
        Assert.Equal(1, reauths);
        await second.SendAsync(new { type = "hello", live_seq = 9 });
        Assert.Equal(4, (await second.ReceiveJsonAsync()).GetProperty("since_seq").GetInt64());

        await connection.DisposeAsync();
        Assert.Equal(ConnectionState.Closed, connection.State);
        await foreach (var _ in connection.ReadAllAsync(ct))
        {
            // Drains what was buffered; completes because the connection is closed.
        }
    }

    [Fact]
    public async Task DeliversBinaryFramesAsUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        using var hub = new LoopbackHub();
        await using var connection = new EventConnection(hub.ConnectAsync, _ => Task.CompletedTask, Fast.Retry, null);
        var socket = await hub.Accepted.Reader.ReadAsync(ct);
        await socket.SendAsync(new byte[] { 1, 2 }, WebSocketMessageType.Binary, true, ct);
        var events = connection.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.Equal(new UnknownEvent("binary", "AQI="), await Next<UnknownEvent>(events));
    }

    [Fact]
    public async Task SendingOnAClosedSocketThrows()
    {
        using var hub = new LoopbackHub();
        await using var connection = new EventConnection(hub.ConnectAsync, _ => Task.CompletedTask, Fast.Retry, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.SendAsync(new SubscribeMessage("c"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReassemblesFramesSplitAcrossReads()
    {
        var ct = TestContext.Current.CancellationToken;
        using var hub = new LoopbackHub();
        await using var connection = new EventConnection(hub.ConnectAsync, _ => Task.CompletedTask, Fast.Retry, null);
        var socket = await hub.Accepted.Reader.ReadAsync(ct);
        var payload = new string('x', 10_000);
        var frame = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "error", context = "c", message = payload }));
        await socket.SendAsync(frame.AsMemory(0, 100), WebSocketMessageType.Text, false, ct);
        await socket.SendAsync(frame.AsMemory(100), WebSocketMessageType.Text, true, ct);
        var events = connection.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.Equal(payload, (await Next<HubErrorEvent>(events)).Message);
    }
}
