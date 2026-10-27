using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Wavvon.Sdk.Tests;

/// <summary>
/// Asserts the tier-1 vectors from Wavvon-server's wire-vectors.json, the one authority for every
/// signed byte sequence (sdk.md §4). The file is read, never copied in: point WAVVON_WIRE_VECTORS
/// at a pinned copy, or keep the default sibling checkout. Missing: a loud skip locally, a failure
/// when CI is set.
/// </summary>
public sealed class ConformanceTests
{
    private static readonly Lazy<(string Path, JsonObject? File)> Vectors = new(Load);

    private static (string, JsonObject?) Load()
    {
        var root = RepoRoot();
        var configured = Environment.GetEnvironmentVariable("WAVVON_WIRE_VECTORS");
        var path = Path.GetFullPath(configured ?? "../../server/crates/identity/wire-vectors.json", root);
        return (path, File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : null);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wavvon.Sdk.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("cannot find the repo root (Wavvon.Sdk.slnx) above " + AppContext.BaseDirectory);
    }

    /// <summary>The named vector with every "@name" string replaced by fixtures[name].</summary>
    private static JsonNode Vector(string name)
    {
        var (path, file) = Vectors.Value;
        if (file is null)
        {
            var message = $"CONFORMANCE VECTORS NOT FOUND at {path}: the tier-1 wire vectors were NOT checked. Set WAVVON_WIRE_VECTORS.";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
            {
                Assert.Fail(message);
            }
            Assert.Skip(message);
        }
        var fixtures = file["fixtures"]!.AsObject();
        var vector = file["vectors"]![name] ?? throw new InvalidOperationException($"vector {name} missing from {path}");
        return Resolve(vector.DeepClone(), fixtures);
    }

    private static JsonNode Resolve(JsonNode node, JsonObject fixtures)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith('@'):
                return fixtures[s[1..]]?.DeepClone() ?? throw new InvalidOperationException($"unknown fixture {s}");
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    o[key] = Resolve(o[key]!.DeepClone(), fixtures);
                }
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    a[i] = Resolve(a[i]!.DeepClone(), fixtures);
                }
                return a;
            default:
                return node;
        }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string S(JsonNode node, string path) =>
        path.Split('.').Aggregate(node, (n, key) => n[key]!).GetValue<string>();

    private static Func<string, string?> HeadersOf(JsonNode v, params (string Header, string Value)[] values) =>
        Http.Headers(values.ToDictionary(x => S(v, "headers." + x.Header), x => x.Value));

    [Fact]
    public void SpeaksFormat1()
    {
        Vector("auth_challenge");
        Assert.Equal(1, Vectors.Value.File!["format"]!.GetValue<int>());
    }

    [Fact]
    public void AuthChallengeTaggedV1SigningBytesAndSignature()
    {
        var v = Vector("auth_challenge");
        var bytes = WavvonAuth.ChallengeSigningBytes(Convert.FromHexString(S(v, "inputs.challenge")), "tagged-v1");
        Assert.Equal(S(v, "signing_bytes"), Hex(bytes));
        using var signer = Identity.FromSeed(S(v, "inputs.signer_seed"));
        Assert.Equal(S(v, "signature"), Hex(signer.Sign(bytes)));
    }

    [Fact]
    public void WebhookDeliveryHubEd25519DispatchSignatureVerifies()
    {
        var v = Vector("webhook_delivery_hub");
        var timestamp = v["inputs"]!["timestamp"]!.GetValue<long>();
        var body = Encoding.UTF8.GetBytes(S(v, "inputs.body"));
        var bytes = WebhookVerifier.DeliverySigningBytes((ulong)timestamp, body);
        Assert.Equal(S(v, "signing_bytes"), Hex(bytes));

        using var hub = Identity.FromSeed(S(v, "inputs.hub_seed"));
        Assert.Equal(S(v, "hub_pubkey"), hub.PublicKey);
        Assert.Equal(S(v, "signature_v1"), Hex(hub.Sign(bytes)));

        var headers = HeadersOf(v,
            ("hub_pubkey", S(v, "hub_pubkey")),
            ("signature_v1", S(v, "signature_v1")),
            ("timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var dispatch = WebhookVerifier.VerifyDispatch(headers, body, S(v, "hub_pubkey"), new VerifyOptions { Now = DateTimeOffset.FromUnixTimeSeconds(timestamp) });
        Assert.IsType<SlashInvocation>(dispatch);
    }

    [Fact]
    public void WebhookDeliveryOutgoingHkdfKeyAndHmacSignatures()
    {
        var v = Vector("webhook_delivery_outgoing");
        Assert.Equal("wavvon-webhook-signing", S(v, "inputs.hkdf_salt"));
        Assert.Equal("", S(v, "inputs.hkdf_info"));
        var key = WebhookVerifier.OutgoingSigningKey(S(v, "inputs.secret_as_shown"));
        Assert.Equal(S(v, "signing_key"), Hex(key));
        Assert.Equal(S(v, "signing_key"), Hex(WebhookVerifier.OutgoingSigningKey(Convert.FromHexString(S(v, "inputs.raw_secret")))));

        var timestamp = v["inputs"]!["timestamp"]!.GetValue<long>();
        var body = Encoding.UTF8.GetBytes(S(v, "inputs.body"));
        var bytes = WebhookVerifier.DeliverySigningBytes((ulong)timestamp, body);
        Assert.Equal(S(v, "signing_bytes"), Hex(bytes));
        Assert.Equal(S(v, "signature_v1"), Hex(HMACSHA256.HashData(key, bytes)));

        var headers = HeadersOf(v,
            ("signature_v1", S(v, "signature_v1")),
            ("timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("webhook_id", "wh_test"));
        var options = new VerifyOptions { Now = DateTimeOffset.FromUnixTimeSeconds(timestamp) };
        Assert.NotNull(WebhookVerifier.VerifyOutgoingWebhook(headers, body, S(v, "inputs.secret_as_shown"), options));
    }
}
