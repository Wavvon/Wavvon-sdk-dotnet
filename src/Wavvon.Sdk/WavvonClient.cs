using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Serialization.Json;
using Wavvon.Sdk.Generated;
using Wavvon.Sdk.Generated.Me.App.Commands;
using Wavvon.Sdk.Generated.Me.App.Profile;
using Wavvon.Sdk.Generated.Me.App.Subscriptions;
using Wavvon.Sdk.Generated.Me.Events;
using Wavvon.Sdk.Generated.Models;

namespace Wavvon.Sdk;

/// <summary>Where the client's session stands.</summary>
public enum SessionState
{
    /// <summary>No session (before the first call, or after a login failed).</summary>
    SignedOut,

    /// <summary>Logging in. After a session died, this is the reconnecting state of REST.</summary>
    Authenticating,

    /// <summary>A session is held.</summary>
    Ready,
}

/// <summary>Options for <see cref="WavvonClient"/>.</summary>
public sealed record WavvonClientOptions
{
    /// <summary>The hub's base URL, for example <c>https://hub.example</c>.</summary>
    public required Uri HubUrl { get; init; }

    /// <summary>The program's keypair. The client signs with it and never disposes it.</summary>
    public required Identity Identity { get; init; }

    /// <summary>Admits the program on an <c>invite_only</c> hub. Only needed the first time.</summary>
    public string? InviteCode { get; init; }

    /// <summary>The retry budget for every request and for reconnects.</summary>
    public RetryOptions Retry { get; init; } = RetryOptions.Default;

    /// <summary>
    /// The HTTP transport, for proxies, custom TLS or tests. The client does not dispose a handler
    /// passed here. When <see langword="null"/>, the client owns a <see cref="SocketsHttpHandler"/>.
    /// </summary>
    public HttpMessageHandler? Handler { get; init; }
}

/// <summary>One row of <c>GET /me/events</c>, with the payload already parsed.</summary>
/// <param name="Id">Pass it to <see cref="WavvonClient.AckEventsAsync"/>.</param>
/// <param name="EventType">For example <c>message.created</c>.</param>
/// <param name="Payload">The event's JSON; a JSON string if the hub stored something that is not JSON.</param>
/// <param name="CreatedAt">When the event was queued, as the hub reports it.</param>
public sealed record PolledEvent(string Id, string EventType, JsonElement Payload, long CreatedAt);

/// <summary>
/// A program's connection to one hub: a session that renews itself, the generated REST client
/// (<see cref="Rest"/>), capability checks, app registration and the event connection.
/// </summary>
/// <remarks>
/// Every REST call through <see cref="Rest"/> or <see cref="SendAsync"/> carries the bearer,
/// retries 429, 5xx, timeouts and network errors, and logs in again once on a 401. A failure that
/// survives that throws <see cref="HubUnavailableException"/> (transient) or
/// <see cref="HubException"/> (a definitive answer); a transient failure never reads as an answer.
/// </remarks>
public sealed class WavvonClient : IDisposable
{
    private readonly WavvonClientOptions _options;
    private readonly Uri _hubUrl;
    private readonly HttpMessageInvoker _transport;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private volatile Session? _session;
    private Task<Session>? _loggingIn;
    private SessionState _state = SessionState.SignedOut;
    private HubInfo? _info;

    /// <summary>Creates a client. Nothing is sent until the first call.</summary>
    public WavvonClient(WavvonClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        var url = options.HubUrl.AbsoluteUri;
        _hubUrl = new Uri(url.EndsWith('/') ? url : url + "/");
        _transport = options.Handler is null
            ? new HttpMessageInvoker(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true)
            : new HttpMessageInvoker(options.Handler, disposeHandler: false);
        // Per-attempt timeouts live in the retry policy; an overall one would cut a patient retry short.
        _http = new HttpClient(new SessionHandler(this)) { BaseAddress = _hubUrl, Timeout = Timeout.InfiniteTimeSpan };
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: _http)
        {
            BaseUrl = _hubUrl.AbsoluteUri.TrimEnd('/'),
        };
        Rest = new WavvonRestClient(adapter);
    }

    /// <summary>Every REST route, generated from <c>openapi.yaml</c> by Kiota. Bearer, retry and re-auth are built in.</summary>
    public WavvonRestClient Rest { get; }

    /// <summary>The program's identity.</summary>
    public Identity Identity => _options.Identity;

    /// <summary>The identity the hub attributes this program's actions to, once logged in.</summary>
    public string? CanonicalPubkey => _session?.CanonicalPubkey;

    /// <summary>Raised on every session state change.</summary>
    public event EventHandler<SessionState>? StateChanged;

    /// <summary>The current session state.</summary>
    public SessionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The current session, logging in if there is none.</summary>
    public Task<Session> LoginAsync(CancellationToken cancellationToken = default) =>
        _session is { } session ? Task.FromResult(session) : ReloginAsync(null, cancellationToken);

    /// <summary>
    /// Sends any request through the session: an escape hatch for a route the generated client
    /// models awkwardly. A relative URI is resolved against the hub URL. Non-success throws.
    /// </summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
        _http.SendAsync(request, cancellationToken);

    /// <summary><c>GET /info</c>, cached after the first call; <paramref name="refresh"/> reads it again. Needs no session.</summary>
    public async Task<HubInfo> InfoAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (_info is { } cached && !refresh)
        {
            return cached;
        }
        using var response = await RetryPolicy.SendAsync(
            ct => _transport.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(_hubUrl, "info")), ct),
            _options.Retry,
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HubException(response.StatusCode, body);
        }
        using var doc = JsonDocument.Parse(body);
        var info = new JsonParseNode(doc.RootElement).GetObjectValue(HubInfo.CreateFromDiscriminatorValue)
            ?? throw new HubException(response.StatusCode, "GET /info answered null");
        _info = info;
        return info;
    }

    /// <summary>Whether the hub advertises <paramref name="capability"/>. Capabilities, never versions.</summary>
    public async Task<bool> HasCapabilityAsync(string capability, CancellationToken cancellationToken = default) =>
        (await InfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Capabilities?.Contains(capability) == true;

    /// <summary>Throws <see cref="UnsupportedByHubException"/> unless the hub advertises <paramref name="capability"/>.</summary>
    public async Task RequireCapabilityAsync(string capability, CancellationToken cancellationToken = default)
    {
        if (!await HasCapabilityAsync(capability, cancellationToken).ConfigureAwait(false))
        {
            throw new UnsupportedByHubException(capability);
        }
    }

    /// <summary>
    /// Registers or replaces this identity's app profile (<c>apps.register</c>). Do it before
    /// <see cref="Connect"/>: the hub only pushes hub events to a socket opened by a registered app.
    /// </summary>
    public async Task SetAppProfileAsync(ProfilePutRequestBody profile, CancellationToken cancellationToken = default)
    {
        await RequireCapabilityAsync("apps.register", cancellationToken).ConfigureAwait(false);
        await Rest.Me.App.Profile.PutAsync(profile, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces this app's slash commands (<c>apps.register</c>).</summary>
    public async Task SetAppCommandsAsync(IEnumerable<CommandsPutRequestBody_commands> commands, CancellationToken cancellationToken = default)
    {
        await RequireCapabilityAsync("apps.register", cancellationToken).ConfigureAwait(false);
        var body = new CommandsPutRequestBody { Commands = [.. commands] };
        var stream = await Rest.Me.App.Commands.PutAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Replaces this app's event subscriptions (<c>apps.register</c>). A <c>message.*</c> subscription must name its channels.</summary>
    public async Task SetAppSubscriptionsAsync(IEnumerable<SubscriptionsPutRequestBody_subscriptions> subscriptions, CancellationToken cancellationToken = default)
    {
        await RequireCapabilityAsync("apps.register", cancellationToken).ConfigureAwait(false);
        var body = new SubscriptionsPutRequestBody { Subscriptions = [.. subscriptions] };
        await Rest.Me.App.Subscriptions.PutAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the hub WebSocket. It reconnects, re-authenticates and resumes on its own until disposed.
    /// Register the app profile first.
    /// </summary>
    public EventConnection Connect(EventConnectionOptions? options = null) =>
        new(ConnectSocketAsync, ct => ReloginAsync(_session?.Token, ct), _options.Retry, options);

    /// <summary><c>GET /me/events</c>: the polling transport, for a program that holds no socket.</summary>
    /// <param name="since">Only events queued after this <see cref="PolledEvent.CreatedAt"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<PolledEvent>> PollEventsAsync(long? since = null, CancellationToken cancellationToken = default)
    {
        var page = await Rest.Me.Events.GetAsync(r => r.QueryParameters.Since = since, cancellationToken).ConfigureAwait(false);
        var rows = page?.Events ?? throw new WavvonException("GET /me/events answered without an events array");
        return [.. rows.Select(r => new PolledEvent(r.Id ?? "", r.EventType ?? "", ParsePayload(r.Payload), r.CreatedAt ?? 0))];
    }

    /// <summary><c>DELETE /me/events</c>: acknowledges polled events so they are not returned again.</summary>
    public async Task AckEventsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var stream = await Rest.Me.Events.DeleteAsync(new EventsDeleteRequestBody { Ids = [.. ids] }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _http.Dispose();
        _transport.Dispose();
    }

    private static JsonElement ParsePayload(string? payload)
    {
        if (payload is null)
        {
            return default;
        }
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(payload);
        }
    }

    private async Task<WebSocket> ConnectSocketAsync(CancellationToken cancellationToken)
    {
        var session = await LoginAsync(cancellationToken).ConfigureAwait(false);
        var url = new UriBuilder(new Uri(_hubUrl, "ws"))
        {
            Scheme = _hubUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Query = "token=" + Uri.EscapeDataString(session.Token),
        };
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(url.Uri, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private Task<Session> ReloginAsync(string? staleToken, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Someone else already replaced the token this caller saw fail.
            if (_session is { } current && current.Token != staleToken)
            {
                return Task.FromResult(current);
            }
            _loggingIn ??= LoginOnceAsync();
            // One caller giving up must not cancel the login the others share.
            return _loggingIn.WaitAsync(cancellationToken);
        }
    }

    private async Task<Session> LoginOnceAsync()
    {
        await Task.Yield();
        SetState(SessionState.Authenticating);
        try
        {
            var session = await WavvonAuth.AuthenticateAsync(
                _transport, _hubUrl, _options.Identity, _options.InviteCode, _options.Retry, CancellationToken.None).ConfigureAwait(false);
            _session = session;
            SetState(SessionState.Ready);
            return session;
        }
        catch
        {
            _session = null;
            SetState(SessionState.SignedOut);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _loggingIn = null;
            }
        }
    }

    private void SetState(SessionState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }
            _state = state;
        }
        StateChanged?.Invoke(this, state);
    }

    private async Task<HttpResponseMessage> SendWithSessionAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        Task<HttpResponseMessage> SendAsync(string token) =>
            RetryPolicy.SendAsync(ct => _transport.SendAsync(Copy(request, content, token), ct), _options.Retry, cancellationToken);

        var session = await LoginAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendAsync(session.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            session = await ReloginAsync(session.Token, cancellationToken).ConfigureAwait(false);
            response = await SendAsync(session.Token).ConfigureAwait(false);
        }
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                throw new HubException(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }
        }
        return response;
    }

    /// <summary>A request can only be sent once, so every attempt sends a copy.</summary>
    private static HttpRequestMessage Copy(HttpRequestMessage request, byte[]? content, string token)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        copy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (content is not null)
        {
            copy.Content = new ByteArrayContent(content);
            foreach (var header in request.Content!.Headers)
            {
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        return copy;
    }

    /// <summary>Routes the HttpClient (and so the generated client) through the session.</summary>
    private sealed class SessionHandler(WavvonClient client) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            client.SendWithSessionAsync(request, cancellationToken);
    }
}
