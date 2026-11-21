// Smoke test of voice against a real hub: the owner and a second identity share a room, and every
// assertion needs a decrypted frame whose bytes match what the other side sent, never a roster
// row. Nothing is played. Without an Opus encoder the frames are fixed 20 ms TOC bytes with a
// numbered payload; the hub relays them sealed and never decodes them.
//
//   WAVVON_SMOKE_HUB=http://localhost:3400 [WAVVON_SMOKE_SEED=<64 hex>] dotnet run --project samples/VoiceSmoke
//
// The seed identity (default 5a repeated) must be a hub owner, so it can create the channel and
// mint the invite the second identity joins with.

using System.Buffers.Binary;
using System.Security.Cryptography;
using Wavvon.Sdk;
using Wavvon.Sdk.Generated.Channels;
using Wavvon.Sdk.Generated.Invites;

var hub = Environment.GetEnvironmentVariable("WAVVON_SMOKE_HUB");
if (string.IsNullOrEmpty(hub))
{
    Console.Error.WriteLine("set WAVVON_SMOKE_HUB");
    return 2;
}
var hubUrl = new Uri(hub);

using var aId = Identity.FromSeed(Environment.GetEnvironmentVariable("WAVVON_SMOKE_SEED") ?? string.Concat(Enumerable.Repeat("5a", 32)));
using var a = new WavvonClient(new WavvonClientOptions { HubUrl = hubUrl, Identity = aId });
await a.LoginAsync();
using var bId = Identity.FromSeed(RandomNumberGenerator.GetBytes(32));
var invite = await a.Rest.Invites.PostAsync(new InvitesPostRequestBody { BoundPubkey = bId.PublicKey });
using var b = new WavvonClient(new WavvonClientOptions { HubUrl = hubUrl, Identity = bId, InviteCode = invite!.Code });
await b.LoginAsync();
var aKey = aId.PublicKey;
var bKey = bId.PublicKey;

var channel = await a.Rest.Channels.PostAsync(new ChannelsPostRequestBody { Name = $"voice-smoke-{bKey[..6]}", ChannelType = "voice" });
var channelId = channel!.Id!;

var va = await a.JoinVoiceAsync(channelId);
var vb = await b.JoinVoiceAsync(channelId);
Say($"both joined: A is sender {va.SenderId}, B is {vb.SenderId}");
var seenA = Seen.Watch(va);
var seenB = Seen.Watch(vb);

// A speaks; B must open what it hears, byte for byte.
var sending = Speak(va, 0xa1, 200);
await Until("B to open 100 frames from A", () => seenB.Heard(aKey, 0xa1) >= 100);
await sending;
if (seenB.Frames().Any(f => f.From == aKey && !Clip.SameAs(f, 0xa1)))
{
    throw new InvalidOperationException("B opened a frame that is not A's");
}
Say($"B opened {seenB.Heard(aKey, 0xa1)} of A's 200 frames, each byte-identical");

sending = Speak(vb, 0xb2, 200);
await Until("A to open 100 frames from B", () => seenA.Heard(bKey, 0xb2) >= 100);
await sending;
Say($"A opened {seenA.Heard(bKey, 0xb2)} of B's 200 frames, each byte-identical");

// A frame that is not 20 ms is refused before anything is sent.
try
{
    await va.SendOpusAsync(new byte[] { 0xf9, 0, 0 });
    throw new InvalidOperationException("a 40 ms packet was accepted");
}
catch (VoiceException e) when (e.Kind == VoiceErrorKind.BadFrame)
{
    Say($"a 40 ms packet is refused ({e.Message})");
}

// Rotation: B leaves, so A's key changes to 2; B rejoins and must open it.
await vb.LeaveAsync();
var vb2 = await b.JoinVoiceAsync(channelId);
var seenB2 = Seen.Watch(vb2);
sending = Speak(va, 0xa3, 200);
await Until("B's second session to open key_id >= 2", () => seenB2.Heard(aKey, 0xa3, 2) >= 50);
await sending;
Say($"after B left, A rotated to key_id {seenB2.Frames().Max(f => f.KeyId)} and B opened {seenB2.Heard(aKey, 0xa3, 2)} frames under it");

// Proximity: ten positions in 100 ms end at the last one.
await va.CreateZoneAsync("smoke-zone", "smoke", "2d");
await Until("both to see the zone", () => seenB2.HasZone("smoke-zone") && va.Zones().Any(z => z.ZoneId == "smoke-zone"));
var t0 = System.Diagnostics.Stopwatch.StartNew();
for (var i = 0; i < 10; i++)
{
    va.SetPosition("smoke-zone", [10 + i, 50]);
    await Task.Delay(10);
}
// One update per 50 ms window the loop spanned, plus the first and the final
// flush; timer slop stretches the loop, and nine keeps it a coalescing test.
var limit = Math.Min(9, (int)Math.Ceiling(t0.Elapsed.TotalMilliseconds / 50) + 2);
await Until("B to see the last position", () => seenB2.Positions() is [.., [19, 50]]);
var n = seenB2.Positions().Count;
if (n > limit)
{
    throw new InvalidOperationException($"coalescing let {n} of 10 positions through (limit {limit})");
}
Say($"ten positions in 100 ms coalesced to {n}, ending at the last");

// A position in someone else's creator-only zone is refused, as an event.
await va.CreateZoneAsync("own-zone", "own", "2d", creatorOnly: true);
await Until("B to see the creator-only zone", () => vb2.Zones().Any(z => z.ZoneId == "own-zone"));
vb2.SetPosition("own-zone", [1, 1]);
await Until("B to be refused", () => !seenB2.Refused.IsEmpty);
Say($"B's position was refused: {seenB2.Refused.First()}");

if (!seenA.Skipped.IsEmpty)
{
    throw new InvalidOperationException($"A skipped {string.Join(", ", seenA.Skipped)}");
}
if (seenA.Rejoining > 0)
{
    throw new InvalidOperationException("A rejoined");
}
await va.LeaveAsync();
await vb2.LeaveAsync();
Say("VOICE SMOKE OK");
return 0;

static void Say(string s) => Console.WriteLine($"voice smoke: {s}");

static async Task Until(string what, Func<bool> cond)
{
    for (var i = 0; i < 600; i++)
    {
        if (cond())
        {
            return;
        }
        await Task.Delay(50);
    }
    throw new TimeoutException($"timed out waiting for {what}");
}

static async Task Speak(Voice voice, byte tag, uint n)
{
    for (uint i = 0; i < n; i++)
    {
        await voice.SendOpusAsync(Clip.Frame(tag, i));
    }
}

sealed class Seen
{
    private readonly object _gate = new();
    private readonly List<VoiceAudioEvent> _frames = [];
    private readonly List<IReadOnlyList<double>> _positions = [];
    private IReadOnlyList<string> _zones = [];

    public System.Collections.Concurrent.ConcurrentQueue<string> Refused { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<string> Skipped { get; } = new();
    public int Rejoining { get; private set; }

    public static Seen Watch(Voice voice)
    {
        var seen = new Seen();
        _ = Task.Run(async () =>
        {
            await foreach (var ev in voice.ReadAllAsync())
            {
                lock (seen._gate)
                {
                    switch (ev)
                    {
                        case VoiceAudioEvent a: seen._frames.Add(a); break;
                        case VoiceZonesEvent z: seen._zones = [.. z.Zones.Select(x => x.ZoneId)]; break;
                        case VoicePositionEvent p: seen._positions.Add(p.Position); break;
                        case VoicePositionRefusedEvent r: seen.Refused.Enqueue(r.Reason); break;
                        case VoiceRejoiningEvent: seen.Rejoining++; break;
                        case VoiceKeySkippedEvent k: seen.Skipped.Enqueue(k.PublicKey); break;
                        case VoiceErrorEvent e: Console.Error.WriteLine($"voice error: {e.Error.Message}"); break;
                        default: break;
                    }
                }
            }
        });
        return seen;
    }

    public List<VoiceAudioEvent> Frames()
    {
        lock (_gate)
        {
            return [.. _frames];
        }
    }

    public List<IReadOnlyList<double>> Positions()
    {
        lock (_gate)
        {
            return [.. _positions];
        }
    }

    public bool HasZone(string id)
    {
        lock (_gate)
        {
            return _zones.Contains(id);
        }
    }

    public int Heard(string from, byte tag, uint minKey = 1) =>
        Frames().Count(f => f.From == from && f.KeyId >= minKey && Clip.SameAs(f, tag));
}

static class Clip
{
    // CELT fullband, one 20 ms frame; the payload carries a tag and the frame number.
    public static byte[] Frame(byte tag, uint i)
    {
        var f = new byte[80];
        f[0] = 0xf8;
        f[1] = tag;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(2), i);
        for (var n = 6; n < f.Length; n++)
        {
            f[n] = (byte)((n * 7) + i);
        }
        return f;
    }

    public static bool SameAs(VoiceAudioEvent a, byte tag) =>
        a.Opus.Length >= 6 && a.Opus.AsSpan().SequenceEqual(Frame(tag, BinaryPrimitives.ReadUInt32BigEndian(a.Opus.AsSpan(2))));
}
