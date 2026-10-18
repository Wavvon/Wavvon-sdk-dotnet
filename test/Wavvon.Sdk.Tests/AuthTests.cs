using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Wavvon.Sdk.Tests;

public sealed class AuthTests
{
    private const string Seed = "5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a";
    private static readonly byte[] Challenge32 = Enumerable.Repeat((byte)9, 32).ToArray();

    [Fact]
    public void TaggedV1PrefixesTheTag()
    {
        var bytes = WavvonAuth.ChallengeSigningBytes(Challenge32, "tagged-v1");
        Assert.Equal(Encoding.UTF8.GetBytes("wavvon/auth-challenge/v1\0"), bytes[..25]);
        Assert.Equal(Challenge32, bytes[25..]);
    }

    [Fact]
    public void RawChallengeIsSignedOnlyAtExactly32Bytes()
    {
        Assert.Equal(Challenge32, WavvonAuth.ChallengeSigningBytes(Challenge32, null));
        Assert.Throws<AuthSchemeException>(() => WavvonAuth.ChallengeSigningBytes(new byte[31], null));
        Assert.Throws<AuthSchemeException>(() => WavvonAuth.ChallengeSigningBytes(new byte[64], null));
    }

    [Fact]
    public void UnknownSchemeIsRefusedInsteadOfFallingBackToRaw()
    {
        Assert.Throws<AuthSchemeException>(() => WavvonAuth.ChallengeSigningBytes(Challenge32, "tagged-v2"));
        Assert.Throws<AuthSchemeException>(() => WavvonAuth.ChallengeSigningBytes(Challenge32, ""));
    }

    [Fact]
    public void SeedIsAcceptedAsHexOrBytesAndOtherLengthsAreRejected()
    {
        using var fromHex = Identity.FromSeed(Seed);
        using var fromBytes = Identity.FromSeed(Convert.FromHexString(Seed));
        Assert.Equal(fromHex.PublicKey, fromBytes.PublicKey);
        Assert.Throws<ArgumentException>(() => Identity.FromSeed("abcd"));
        Assert.Throws<ArgumentException>(() => Identity.FromSeed("not hex"));
    }

    /// <summary>Answers the challenge flow; the first <paramref name="failVerify"/> verifies get 503.</summary>
    private sealed class ChallengeHub(string? scheme = "tagged-v1", int failVerify = 0, HttpStatusCode? verifyStatus = null)
    {
        public List<string> Challenges { get; } = [];
        public List<JsonElement> Verifies { get; } = [];
        public List<string> Bodies { get; } = [];
        private int _failVerify = failVerify;

        public FakeHandler Handler => new((request, body) =>
        {
            Bodies.Add(body ?? "");
            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/challenge", StringComparison.Ordinal))
            {
                var challenge = Convert.ToHexString(Enumerable.Repeat((byte)(Challenges.Count + 1), 32).ToArray()).ToLowerInvariant();
                Challenges.Add(challenge);
                return scheme is null ? Http.Json(new { challenge }) : Http.Json(new { challenge, scheme });
            }
            var verify = JsonDocument.Parse(body!).RootElement.Clone();
            Verifies.Add(verify);
            if (_failVerify-- > 0)
            {
                return Http.Text(HttpStatusCode.ServiceUnavailable, "busy");
            }
            if (verifyStatus is { } status)
            {
                return Http.Text(status, "This hub requires an invite code");
            }
            var signed = WavvonAuth.ChallengeSigningBytes(Convert.FromHexString(verify.GetProperty("challenge").GetString()!), scheme);
            var ok = Identity.Verify(verify.GetProperty("public_key").GetString()!, signed, Convert.FromHexString(verify.GetProperty("signature").GetString()!));
            return ok
                ? Http.Json(new { token = "tok", scope = "member", canonical_pubkey = verify.GetProperty("public_key").GetString() })
                : Http.Text(HttpStatusCode.Unauthorized, "Invalid signature");
        });
    }

    private static readonly Uri Hub = new("http://hub/");

    [Fact]
    public async Task LogsInWithTaggedV1AndPassesTheInvite()
    {
        var hub = new ChallengeHub();
        using var http = new HttpClient(hub.Handler);
        using var identity = Identity.FromSeed(Seed);
        var session = await WavvonAuth.AuthenticateAsync(http, Hub, identity, "inv", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("tok", session.Token);
        Assert.Equal(identity.PublicKey, session.CanonicalPubkey);
        Assert.Equal("inv", hub.Verifies[0].GetProperty("invite_code").GetString());
    }

    [Fact]
    public async Task SignsTheRawChallengeForAnOlderHub()
    {
        var hub = new ChallengeHub(scheme: null);
        using var http = new HttpClient(hub.Handler);
        using var identity = Identity.FromSeed(Seed);
        var session = await WavvonAuth.AuthenticateAsync(http, Hub, identity, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("tok", session.Token);
        Assert.False(hub.Verifies[0].TryGetProperty("invite_code", out _));
    }

    [Fact]
    public async Task RestartsFromAFreshChallengeAfterATransientVerifyFailure()
    {
        var hub = new ChallengeHub(failVerify: 2);
        using var http = new HttpClient(hub.Handler);
        using var identity = Identity.FromSeed(Seed);
        await WavvonAuth.AuthenticateAsync(http, Hub, identity, retry: Fast.Retry, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, hub.Challenges.Count);
        Assert.Equal(3, hub.Verifies.Select(v => v.GetProperty("challenge").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task SurfacesADefinitiveRefusalAsHubException()
    {
        var hub = new ChallengeHub(verifyStatus: HttpStatusCode.Forbidden);
        using var http = new HttpClient(hub.Handler);
        using var identity = Identity.FromSeed(Seed);
        var e = await Assert.ThrowsAsync<HubException>(() =>
            WavvonAuth.AuthenticateAsync(http, Hub, identity, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Forbidden, e.Status);
        Assert.Single(hub.Verifies);
    }

    [Fact]
    public async Task NeverSendsTheSeed()
    {
        var hub = new ChallengeHub();
        using var http = new HttpClient(hub.Handler);
        using var identity = Identity.FromSeed(Seed);
        await WavvonAuth.AuthenticateAsync(http, Hub, identity, "x", cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(hub.Bodies, b => b.Contains(Seed, StringComparison.OrdinalIgnoreCase));
    }
}
