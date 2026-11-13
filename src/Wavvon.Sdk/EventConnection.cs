using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Wavvon.Sdk;

/// <summary>The state of an <see cref="EventConnection"/>.</summary>
public enum ConnectionState
{
    /// <summary>Opening the first connection.</summary>
    Connecting,

    /// <summary>The hub said hello; events flow.</summary>
    Open,

    /// <summary>The connection dropped; a new one is being opened.</summary>
    Reconnecting,

    /// <summary>Disposed. No more events.</summary>
    Closed,
}

/// <summary>Options for <see cref="WavvonClient.Connect"/>.</summary>
public sealed record EventConnectionOptions
{
    /// <summary>
    /// Replay hub events after this seq on the first connect (an app persisting its position across
    /// restarts). Without it the stream starts from now.
    /// </summary>
    public long? SinceSeq { get; init; }
}

/// <summary>
/// One hub WebSocket that reconnects on its own. It tracks the last <see cref="HubEvent"/> seq and
/// sends <c>resume</c> after every reconnect, so an app misses nothing inside the hub's 72-hour
/// window; <see cref="ReplayUnavailableEvent"/> is delivered like any other event when it falls
/// outside. The hub only honours <c>resume</c> (and only pushes hub events) for an identity whose
/// app profile existed when the socket connected: register the profile first.
/// </summary>
public sealed class EventConnection : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<WebSocket>> _connect;
    private readonly Func<CancellationToken, Task> _reauthenticate;
    private readonly RetryOptions _retry;
    // ponytail: unbounded buffer; a program that never reads grows it. Bound it if that bites.
    private readonly Channel<WsEvent> _events = Channel.CreateUnbounded<WsEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _stateLock = new();
    private readonly Task _loop;
    private WebSocket? _socket;
    private ConnectionState _state = ConnectionState.Connecting;
    private long? _lastSeq;

    internal EventConnection(
        Func<CancellationToken, Task<WebSocket>> connect,
        Func<CancellationToken, Task> reauthenticate,
        RetryOptions retry,
        EventConnectionOptions? options)
    {
        _connect = connect;
        _reauthenticate = reauthenticate;
        _retry = retry;
        _lastSeq = options?.SinceSeq;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Raised on every state change, from the stream's background task.</summary>
    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>The current state.</summary>
    public ConnectionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// The highest hub event seq delivered (or the hub's live seq at first connect). Persist it and
    /// pass it back as <see cref="EventConnectionOptions.SinceSeq"/> to resume across restarts.
    /// </summary>
    public long? LastSeq
    {
        get
        {
            lock (_stateLock)
            {
                return _lastSeq;
            }
        }
    }

    /// <summary>
    /// Every event in order, until the stream is disposed. Read it from one consumer only.
    /// Unknown frame types arrive as <see cref="UnknownEvent"/>.
    /// </summary>
    public IAsyncEnumerable<WsEvent> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _events.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Sends one frame.</summary>
    /// <exception cref="InvalidOperationException">The socket is not open.</exception>
    public async Task SendAsync(ClientMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var socket = State == ConnectionState.Open ? _socket : null;
        if (socket is null)
        {
            throw new InvalidOperationException($"socket is {State}, not open");
        }
        await SendOnAsync(socket, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Mini-app host: show a launch card in a channel.</summary>
    public Task AnnounceAppAsync(string channelId, string title, string description, CancellationToken cancellationToken = default) =>
        SendAsync(new AppAnnounceMessage(channelId, title, description), cancellationToken);

    /// <summary>Mini-app host: close the session; clients dismiss their webviews.</summary>
    public Task DismissAppAsync(string channelId, CancellationToken cancellationToken = default) =>
        SendAsync(new AppDismissMessage(channelId), cancellationToken);

    /// <summary>
    /// Mini-app host: relay an opaque payload to one joined player. Players' messages arrive as
    /// <see cref="MiniAppMessageEvent"/> with <see cref="MiniAppMessageEvent.FromPubkey"/>. The hub
    /// answers <c>app_join</c> itself.
    /// </summary>
    public Task SendMiniAppMessageAsync(string appId, string channelId, string toPubkey, string payload, CancellationToken cancellationToken = default) =>
        SendAsync(new MiniAppMessage(appId, channelId, payload, toPubkey), cancellationToken);

    /// <summary>Closes the socket, stops reconnecting and completes <see cref="ReadAllAsync"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The loop ends by cancellation.
        }
        _stop.Dispose();
        _sendLock.Dispose();
    }

    private async Task SendOnAsync(WebSocket socket, ClientMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), WavvonJson.Options);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void SetLastSeq(long seq)
    {
        lock (_stateLock)
        {
            _lastSeq = seq;
        }
    }

    private void SetState(ConnectionState state)
    {
        lock (_stateLock)
        {
            if (_state == state)
            {
                return;
            }
            _state = state;
        }
        StateChanged?.Invoke(this, state);
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var attempt = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var greeted = false;
                try
                {
                    using var socket = await _connect(stop).ConfigureAwait(false);
                    _socket = socket;
                    await foreach (var frame in ReceiveFramesAsync(socket, stop).ConfigureAwait(false))
                    {
                        var e = frame;
                        if (e is HelloEvent hello)
                        {
                            greeted = true;
                            attempt = 0;
                            SetState(ConnectionState.Open);
                            if (LastSeq is { } since)
                            {
                                await SendOnAsync(socket, new ResumeMessage(since), stop).ConfigureAwait(false);
                            }
                            else
                            {
                                SetLastSeq(hello.LiveSeq);
                            }
                        }
                        else if (e is HubEvent hubEvent)
                        {
                            // Live events that arrived during a replay are flushed after it and can
                            // repeat what the replay already carried.
                            if (LastSeq is { } last && hubEvent.Seq <= last)
                            {
                                continue;
                            }
                            SetLastSeq(hubEvent.Seq);
                        }
                        _events.Writer.TryWrite(e);
                    }
                }
                catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or WavvonException
                    || (e is OperationCanceledException && !stop.IsCancellationRequested))
                {
                    // A dropped or refused connection: reconnect below.
                }
                finally
                {
                    _socket = null;
                }
                if (stop.IsCancellationRequested)
                {
                    break;
                }
                SetState(ConnectionState.Reconnecting);
                attempt++;
                await Task.Delay(RetryPolicy.BackoffDelay(attempt, _retry), stop).ConfigureAwait(false);
                if (!greeted)
                {
                    // A refused upgrade looks like a dropped one; a fresh session rules out the token.
                    try
                    {
                        await _reauthenticate(stop).ConfigureAwait(false);
                    }
                    catch (WavvonException)
                    {
                        // The next connect attempt will fail and come back here.
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Disposed.
        }
        finally
        {
            SetState(ConnectionState.Closed);
            _events.Writer.TryComplete();
        }
    }

    private static async IAsyncEnumerable<WsEvent> ReceiveFramesAsync(
        WebSocket socket,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        while (socket.State == WebSocketState.Open)
        {
            buffer.Clear();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.GetMemory(4096), cancellationToken).ConfigureAwait(false);
                buffer.Advance(result.Count);
            }
            while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                yield break;
            }
            yield return result.MessageType == WebSocketMessageType.Text
                ? WsEvent.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan))
                : new UnknownEvent("binary", Convert.ToBase64String(buffer.WrittenSpan));
        }
    }
}
