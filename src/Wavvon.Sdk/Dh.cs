using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Wavvon.Sdk;

/// <summary>
/// Where pinned peer DH keys live (trust on first use). The default keeps them in memory for the
/// life of the client; a program that wants pins across restarts passes its own.
/// </summary>
public interface IDhPinStore
{
    /// <summary>The value stored under <paramref name="key"/>, or <see langword="null"/>.</summary>
    ValueTask<string?> GetAsync(string key);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>, replacing any.</summary>
    ValueTask SetAsync(string key, string value);
}

internal sealed class MemoryDhPinStore : IDhPinStore
{
    private readonly ConcurrentDictionary<string, string> _pins = new();

    public ValueTask<string?> GetAsync(string key) => ValueTask.FromResult(_pins.TryGetValue(key, out var v) ? v : null);

    public ValueTask SetAsync(string key, string value)
    {
        _pins[key] = value;
        return ValueTask.CompletedTask;
    }
}

/// <summary>The DH key record (wire-format.md, DhKeyRecord): publish, fetch, verify, pin.</summary>
internal static class DhKeys
{
    private static byte[] LenPrefixed(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        var o = new byte[4 + b.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(o, (uint)b.Length);
        b.CopyTo(o, 4);
        return o;
    }

    /// <summary>What <paramref name="pubkey"/> signs to publish its DH key.</summary>
    public static byte[] SigningBytes(string pubkey, string dhPubkeyHex) =>
        [.. "wavvon/dh-key/v1\0"u8, .. LenPrefixed(pubkey), .. LenPrefixed(dhPubkeyHex)];

    public static bool VerifyRecord(string pubkey, string dhPubkeyHex, string signatureHex) =>
        Hex.TryDecode(signatureHex) is { } sig && Identity.Verify(pubkey, SigningBytes(pubkey, dhPubkeyHex), sig);

    /// <summary>
    /// The DH key <paramref name="pubkey"/> published, verified; <see langword="null"/> on a 404 or a
    /// record without a key. A record the peer did not sign throws: the hub is not trusted with it.
    /// </summary>
    public static async Task<string?> FetchAsync(WavvonClient client, string pubkey, CancellationToken ct)
    {
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"identity/{Uri.EscapeDataString(pubkey)}/dh-key");
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HubException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var dh = root.TryGetProperty("dh_pubkey_hex", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        if (string.IsNullOrEmpty(dh))
        {
            return null;
        }
        var sig = root.TryGetProperty("signature_hex", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (sig is null || !VerifyRecord(pubkey, dh, sig))
        {
            throw new DhKeyUnsignedException(pubkey);
        }
        return dh;
    }

    public static async Task<bool> PublishAsync(WavvonClient client, CancellationToken ct)
    {
        var own = client.Identity.PublicKey;
        var dh = client.Identity.DhPublicKey;
        try
        {
            if (await FetchAsync(client, own, ct).ConfigureAwait(false) == dh)
            {
                return false;
            }
        }
        catch (DhKeyUnsignedException)
        {
            // Replaced below with a record this identity signs.
        }
        var signature = Hex.Encode(client.Identity.Sign(SigningBytes(own, dh)));
        using var request = new HttpRequestMessage(HttpMethod.Put, $"identity/{own}/dh-key")
        {
            Content = WavvonJson.Content(new { dh_pubkey_hex = dh, signature_hex = signature }),
        };
        using var _ = await client.SendAsync(request, ct).ConfigureAwait(false);
        return true;
    }

    public static async Task<byte[]> PeerAsync(WavvonClient client, IDhPinStore pins, string pubkey, CancellationToken ct)
    {
        var dh = await FetchAsync(client, pubkey, ct).ConfigureAwait(false) ?? throw new RecipientHasNoKeyException(pubkey);
        var raw = Hex.TryDecode(dh);
        if (raw is not { Length: 32 })
        {
            throw new DhKeyUnsignedException(pubkey);
        }
        var key = $"dh-pin/{pubkey}";
        var pinned = await pins.GetAsync(key).ConfigureAwait(false);
        if (pinned is null)
        {
            await pins.SetAsync(key, dh).ConfigureAwait(false);
        }
        else if (pinned != dh)
        {
            throw new DhKeyChangedException(pubkey, pinned, dh);
        }
        return raw;
    }

    public static async Task AcceptAsync(WavvonClient client, IDhPinStore pins, string pubkey, CancellationToken ct)
    {
        var dh = await FetchAsync(client, pubkey, ct).ConfigureAwait(false) ?? throw new RecipientHasNoKeyException(pubkey);
        await pins.SetAsync($"dh-pin/{pubkey}", dh).ConfigureAwait(false);
    }
}
