using System.Net;
using Wavvon.Sdk.Generated.Me.App.Commands;
using Xunit;

namespace Wavvon.Sdk.Tests;

public sealed class ClientTests
{
    /// <summary>A hub with a token that can be revoked mid-run and a flaky <c>/channels</c>.</summary>
    private sealed class TokenHub(params string[] capabilities)
    {
        private int _tokens;
        private string _valid = "";
        public int Flaky { get; set; }
        public List<string> Log { get; } = [];
        public void Revoke() => _valid = "revoked";

        public FakeHandler Handler => new((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Log)
            {
                Log.Add($"{request.Method} {path}");
            }
            switch (path)
            {
                case "/info":
                    return Http.Json(new { public_key = "aa", capabilities });
                case "/auth/challenge":
                    return Http.Json(new { challenge = new string('0', 64), scheme = "tagged-v1" });
                case "/auth/verify":
                    _valid = $"t{++_tokens}";
                    return Http.Json(new { token = _valid, scope = "member", canonical_pubkey = "me" });
            }
            if (request.Headers.Authorization?.ToString() != $"Bearer {_valid}")
            {
                return Http.Text(HttpStatusCode.Unauthorized, "expired");
            }
            if (path == "/channels" && Flaky-- > 0)
            {
                return Http.Text(HttpStatusCode.TooManyRequests);
            }
            if (path == "/missing")
            {
                return Http.Text(HttpStatusCode.NotFound, "no such thing");
            }
            if (path == "/me/app/commands")
            {
                return Http.Text(HttpStatusCode.OK);
            }
            return Http.Json(new[] { new { id = "c1", name = "general" } });
        });

        public WavvonClient Client(RetryOptions? retry = null) => new(new WavvonClientOptions
        {
            HubUrl = new Uri("http://hub"),
            Identity = Identity.FromSeed(new byte[32]),
            Retry = retry ?? Fast.Retry,
            Handler = Handler,
        });
    }

    [Fact]
    public async Task AddsTheBearerReauthenticatesOnceOn401AndRetriesTheCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new TokenHub();
        using var client = hub.Client();
        var states = new List<SessionState>();
        client.StateChanged += (_, s) => states.Add(s);

        Assert.Single((await client.Rest.Channels.GetAsync(cancellationToken: ct))!);
        hub.Revoke();
        Assert.Equal("general", (await client.Rest.Channels.GetAsync(cancellationToken: ct))![0].Name);

        Assert.Equal(2, hub.Log.Count(l => l == "POST /auth/verify"));
        Assert.Equal([SessionState.Authenticating, SessionState.Ready, SessionState.Authenticating, SessionState.Ready], states);
        Assert.Equal("me", client.CanonicalPubkey);
    }

    [Fact]
    public async Task ConcurrentCallsShareOneLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new TokenHub();
        using var client = hub.Client();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.Rest.Channels.GetAsync(cancellationToken: ct)));
        Assert.Equal(1, hub.Log.Count(l => l == "POST /auth/verify"));
    }

    [Fact]
    public async Task Retries429AndFailsLoudlyOnceTheBudgetIsSpent()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new TokenHub();
        using var client = hub.Client(Fast.Retry with { MaxAttempts = 3 });
        hub.Flaky = 2;
        Assert.NotNull(await client.Rest.Channels.GetAsync(cancellationToken: ct));
        hub.Flaky = 10;
        var e = await Assert.ThrowsAsync<HubUnavailableException>(() => client.Rest.Channels.GetAsync(cancellationToken: ct));
        Assert.Equal(HttpStatusCode.TooManyRequests, e.LastStatus);
    }

    [Fact]
    public async Task ADefinitiveRefusalThrowsHubExceptionWithTheHubsWords()
    {
        var hub = new TokenHub();
        using var client = hub.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "missing");
        var e = await Assert.ThrowsAsync<HubException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, e.Status);
        Assert.Equal("no such thing", e.Body);
    }

    [Fact]
    public async Task GatesAppRegistrationOnTheAppsRegisterCapability()
    {
        var ct = TestContext.Current.CancellationToken;
        using var without = new TokenHub().Client();
        var e = await Assert.ThrowsAsync<UnsupportedByHubException>(() => without.SetAppCommandsAsync([], ct));
        Assert.Equal("apps.register", e.Capability);

        var hub = new TokenHub("apps.register");
        using var with = hub.Client();
        await with.SetAppCommandsAsync([new CommandsPutRequestBody_commands { Name = "echo", Description = "Echo" }], ct);
        Assert.Contains("PUT /me/app/commands", hub.Log);
    }
}
