using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Wavvon.Sdk.Tests;

public sealed class WebhookTests
{
    private const long Now = 1_700_000_000;
    private static readonly VerifyOptions AtNow = new() { Now = DateTimeOffset.FromUnixTimeSeconds(Now) };
    private static readonly Identity HubKey = Identity.FromSeed(Enumerable.Repeat((byte)5, 32).ToArray());

    private const string Slash =
        """{"type":"slash_command","hub_url":"https://h","channel_id":"c","message_id_hint":"m","author":{"pubkey":"p"},"command":"echo","args_raw":"hi there","args_tokens":["hi","there"]}""";

    private static Dictionary<string, string> SignedDispatch(string body, long ts = Now) => new()
    {
        ["X-Wavvon-Hub-Pubkey"] = HubKey.PublicKey,
        ["X-Wavvon-Signature-V1"] = Convert.ToHexString(HubKey.Sign(WebhookVerifier.DeliverySigningBytes((ulong)ts, Encoding.UTF8.GetBytes(body)))),
        ["X-Wavvon-Timestamp"] = ts.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static Dispatch Verify(Dictionary<string, string> headers, string body, VerifyOptions? options = null, string? hubKey = null) =>
        WebhookVerifier.VerifyDispatch(Http.Headers(headers), Encoding.UTF8.GetBytes(body), hubKey ?? HubKey.PublicKey, options ?? AtNow);

    [Fact]
    public void AcceptsACorrectlySignedInvocationAndDecodesIt()
    {
        var inv = Assert.IsType<SlashInvocation>(Verify(SignedDispatch(Slash), Slash));
        Assert.Equal("echo", inv.Command);
        Assert.Equal(["hi", "there"], inv.ArgsTokens);
        Assert.Equal("p", inv.Author.Pubkey);
    }

    [Fact]
    public void RefusesTamperingStalenessAMissingV1HeaderAndAForeignHub()
    {
        var h = SignedDispatch(Slash);
        Assert.Throws<WebhookVerificationException>(() => Verify(h, Slash.Replace("echo", "ban", StringComparison.Ordinal)));
        Assert.Throws<WebhookVerificationException>(() => Verify(h, Slash, AtNow with { Now = DateTimeOffset.FromUnixTimeSeconds(Now + 301) }));
        Assert.IsType<SlashInvocation>(Verify(h, Slash, AtNow with { Now = DateTimeOffset.FromUnixTimeSeconds(Now + 300) }));

        var legacyOnly = new Dictionary<string, string>(h);
        legacyOnly.Remove("X-Wavvon-Signature-V1");
        Assert.Throws<WebhookVerificationException>(() => Verify(legacyOnly, Slash));

        using var other = Identity.FromSeed(Enumerable.Repeat((byte)6, 32).ToArray());
        Assert.Throws<WebhookVerificationException>(() => Verify(h, Slash, hubKey: other.PublicKey));
        var noPubkeyHeader = new Dictionary<string, string>(h);
        noPubkeyHeader.Remove("X-Wavvon-Hub-Pubkey");
        Assert.Throws<WebhookVerificationException>(() => Verify(noPubkeyHeader, Slash, hubKey: other.PublicKey));
    }

    [Fact]
    public void DeliversAnUnknownDispatchTypeInsteadOfDroppingIt()
    {
        const string body = """{"type":"modal_submit","x":1}""";
        var unknown = Assert.IsType<UnknownDispatch>(Verify(SignedDispatch(body), body));
        Assert.Equal("modal_submit", unknown.RawType);
        Assert.Equal(1, unknown.Raw.GetProperty("x").GetInt32());
    }

    private static readonly DispatchHandler Handler = new(new DispatchHandlerOptions
    {
        HubPublicKey = HubKey.PublicKey,
        Verify = AtNow,
        OnSlashCommand = (inv, _) => Task.FromResult(new AppResponse { Reply = new AppReply(inv.ArgsRaw), Ephemeral = true }),
    });

    [Fact]
    public async Task HandlerAnswersAVerifiedInvocationWithTheReply()
    {
        var result = await Handler.HandleAsync(Http.Headers(SignedDispatch(Slash)), Encoding.UTF8.GetBytes(Slash), TestContext.Current.CancellationToken);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("""{"reply":{"body":"hi there"},"ephemeral":true}""", result.Body);
    }

    [Fact]
    public async Task HandlerAnswers401ToAnUnverifiedOne()
    {
        var result = await Handler.HandleAsync(_ => null, Encoding.UTF8.GetBytes(Slash), TestContext.Current.CancellationToken);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public void ReplyTypesSerialiseToTheHubsShape()
    {
        var response = new AppResponse
        {
            Reply = new AppReply("b") { Components = [new ComponentRow([new AppComponent("button", "ok") { Label = "OK" }])] },
            Game = new GameLaunchCard("https://g", "G"),
        };
        Assert.Equal(
            """{"reply":{"body":"b","components":[{"components":[{"type":"button","custom_id":"ok","label":"OK"}],"type":"row"}]},"game":{"entry_url":"https://g","name":"G"}}""",
            JsonSerializer.Serialize(response, WavvonJson.Options));
    }

    private const string Secret = "BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc";
    private const string HubEventBody = """{"type":"hub_event","seq":3,"event":"member.joined","hub_url":"h","at":1700000000,"payload":{},"webhook_id":"w"}""";

    private static Dictionary<string, string> SignedOutgoing() => new()
    {
        ["x-wavvon-signature-v1"] = Convert.ToHexString(HMACSHA256.HashData(
            WebhookVerifier.OutgoingSigningKey(Secret), WebhookVerifier.DeliverySigningBytes(Now, Encoding.UTF8.GetBytes(HubEventBody)))),
        ["x-wavvon-timestamp"] = Now.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    [Fact]
    public void OutgoingAcceptsTheSecretAsShownAndAsDecodedBytes()
    {
        var body = Encoding.UTF8.GetBytes(HubEventBody);
        var e = WebhookVerifier.VerifyOutgoingWebhook(Http.Headers(SignedOutgoing()), body, Secret, AtNow);
        Assert.Equal(3, e.Seq);
        Assert.Equal("w", e.WebhookId);
        Assert.Equal(3, WebhookVerifier.VerifyOutgoingWebhook(Http.Headers(SignedOutgoing()), body, Enumerable.Repeat((byte)7, 32).ToArray(), AtNow).Seq);
    }

    [Fact]
    public void OutgoingRefusesTheWrongSecretOrAReplayOutsideTheWindow()
    {
        var body = Encoding.UTF8.GetBytes(HubEventBody);
        Assert.Throws<WebhookVerificationException>(() => WebhookVerifier.VerifyOutgoingWebhook(Http.Headers(SignedOutgoing()), body, "AAAA", AtNow));
        Assert.Throws<WebhookVerificationException>(() =>
            WebhookVerifier.VerifyOutgoingWebhook(Http.Headers(SignedOutgoing()), body, Secret, AtNow with { Now = DateTimeOffset.FromUnixTimeSeconds(Now - 400) }));
    }
}
