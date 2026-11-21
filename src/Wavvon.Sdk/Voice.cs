using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Wavvon.Sdk;

// A program in a voice channel (sdk-voice.md). It does what the desktop and web clients do:
// signalling rides a WebSocket of the room's own, the audio a WebRTC data channel, and every
// packet is sealed with a per-sender key wrapped to each listener's DH key.
//
// The SDK owns the mechanisms (key distribution and rotation, 20 ms pacing, the replay watermark,
// the proximity rate limit). It decides nothing for the program: mute, deafen, AFK moves and a
// changed DH key are events and calls.

/// <summary>Voice crypto (wire-format.md, "Voice key wrap" and "Voice packet seal").</summary>
internal static class VoiceCrypto
{
    private const int HeaderLength = 16;
    private const int TagLength = 16;
    private static readonly byte[] VoiceKeyInfo = "wavvon/voice-key/v1"u8.ToArray();

    private static byte[] WrapKey(Identity own, ReadOnlySpan<byte> peerDh, string channelId) =>
        own.DhDerive(peerDh, Encoding.UTF8.GetBytes(channelId), VoiceKeyInfo, 32);

    private static byte[] Seal(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        var o = new byte[plaintext.Length + TagLength];
        using var gcm = new AesGcm(key, TagLength);
        gcm.Encrypt(nonce, plaintext, o.AsSpan(0, plaintext.Length), o.AsSpan(plaintext.Length), aad);
        return o;
    }

    private static byte[] Open(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> sealedBytes, ReadOnlySpan<byte> aad)
    {
        if (sealedBytes.Length < TagLength)
        {
            throw new CryptographicException("ciphertext shorter than its tag");
        }
        var o = new byte[sealedBytes.Length - TagLength];
        using var gcm = new AesGcm(key, TagLength);
        gcm.Decrypt(nonce, sealedBytes[..o.Length], sealedBytes[o.Length..], o, aad);
        return o;
    }

    public static byte[] KeyWrap(Identity sender, ReadOnlySpan<byte> recipientDh, string channelId, byte[] senderKey, byte[] nonceSalt, uint keyId, byte[] nonce)
    {
        var plain = new byte[40];
        senderKey.CopyTo(plain, 0);
        nonceSalt.CopyTo(plain, 32);
        BinaryPrimitives.WriteUInt32BigEndian(plain.AsSpan(36), keyId);
        return Seal(WrapKey(sender, recipientDh, channelId), nonce, plain, default);
    }

    public static (byte[] SenderKey, byte[] NonceSalt, uint KeyId) KeyUnwrap(Identity recipient, ReadOnlySpan<byte> senderDh, string channelId, byte[] ciphertext, byte[] nonce)
    {
        var p = Open(WrapKey(recipient, senderDh, channelId), nonce, ciphertext, default);
        if (p.Length != 40)
        {
            throw new CryptographicException($"wrapped voice key is {p.Length} bytes, not 40");
        }
        return (p[..32], p[32..36], BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(36)));
    }

    private static byte[] PacketNonce(byte[] salt, ReadOnlySpan<byte> ctrBe)
    {
        var n = new byte[12];
        salt.CopyTo(n, 0);
        ctrBe.CopyTo(n.AsSpan(4));
        return n;
    }

    /// <summary><c>key_id[4] || ctr[8] || ts[4]</c> in clear (and as AAD), then AES-256-GCM(opus).</summary>
    public static byte[] PacketSeal(byte[] senderKey, byte[] nonceSalt, uint keyId, ulong ctr, uint ts, ReadOnlySpan<byte> opus)
    {
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, keyId);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(4), ctr);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), ts);
        return [.. header, .. Seal(senderKey, PacketNonce(nonceSalt, header.AsSpan(4, 8)), opus, header)];
    }

    public static (uint KeyId, ulong Ctr, uint Ts, byte[] Opus) PacketOpen(byte[] senderKey, byte[] nonceSalt, ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderLength + TagLength)
        {
            throw new CryptographicException($"voice packet too short: {packet.Length} bytes");
        }
        var header = packet[..HeaderLength];
        var opus = Open(senderKey, PacketNonce(nonceSalt, header.Slice(4, 8)), packet[HeaderLength..], header);
        return (BinaryPrimitives.ReadUInt32BigEndian(header), BinaryPrimitives.ReadUInt64BigEndian(header[4..]), BinaryPrimitives.ReadUInt32BigEndian(header[12..]), opus);
    }

    /// <summary>
    /// Duration of an Opus packet in microseconds, from its TOC byte (RFC 6716 3.1). The SDK sends
    /// only 20 ms packets: the desktop decoder's output buffer holds 20 ms, so a 40 or 60 ms packet
    /// fails to decode on every desktop listener.
    /// </summary>
    /// <exception cref="FormatException">The packet is empty or declares no frames.</exception>
    public static int OpusDurationUs(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty)
        {
            throw new FormatException("empty packet");
        }
        var toc = packet[0];
        var config = toc >> 3;
        int[] table = config <= 11 ? [10_000, 20_000, 40_000, 60_000] : config <= 15 ? [10_000, 20_000] : [2_500, 5_000, 10_000, 20_000];
        var frameUs = table[config % table.Length];
        int frames;
        if ((toc & 3) == 0)
        {
            frames = 1;
        }
        else if ((toc & 3) != 3)
        {
            frames = 2;
        }
        else
        {
            frames = packet.Length > 1 ? packet[1] & 0x3f : throw new FormatException("code-3 packet without a frame count");
        }
        return frames == 0 ? throw new FormatException("packet declares zero frames") : frameUs * frames;
    }
}

/// <summary>One participant of a voice room.</summary>
/// <param name="PublicKey">Their identity.</param>
/// <param name="SenderId">Their relay id in this room.</param>
/// <param name="DisplayName">Their display name, when the hub sent one.</param>
public sealed record VoiceParticipant(string PublicKey, ushort SenderId, string? DisplayName = null);

/// <summary>A proximity zone of the room.</summary>
/// <param name="ZoneId">The zone's id.</param>
/// <param name="Name">Its name.</param>
/// <param name="CoordinateSystem"><c>2d</c> or <c>3d</c>.</param>
/// <param name="Attenuation">The hub's attenuation object, as sent.</param>
/// <param name="Positions">The last known position of each participant in it, by public key.</param>
public sealed record VoiceZone(
    string ZoneId,
    string Name,
    string CoordinateSystem,
    JsonElement Attenuation,
    IReadOnlyDictionary<string, IReadOnlyList<double>> Positions);

/// <summary>Something that happened in a voice room. Switch on the concrete type.</summary>
public abstract record VoiceEvent
{
    private protected VoiceEvent() { }
}

/// <summary>One decrypted 20 ms packet from one speaker. Jitter buffering is the program's: order by <see cref="Ts"/> and <see cref="Ctr"/>.</summary>
/// <param name="From">The speaker's public key.</param>
/// <param name="SenderId">The speaker's relay id.</param>
/// <param name="KeyId">The sender key generation it was sealed under.</param>
/// <param name="Ts">48 kHz sample clock, +960 per frame.</param>
/// <param name="Ctr">The packet counter under <see cref="KeyId"/>.</param>
/// <param name="Whisper">Whether it was whispered to this program.</param>
/// <param name="Opus">The Opus packet.</param>
public sealed record VoiceAudioEvent(string From, ushort SenderId, uint KeyId, uint Ts, ulong Ctr, bool Whisper, byte[] Opus) : VoiceEvent;

/// <summary>Who is in the room, after any change.</summary>
/// <param name="Participants">Everyone, by sender id.</param>
public sealed record VoiceRosterEvent(IReadOnlyList<VoiceParticipant> Participants) : VoiceEvent;

/// <summary>A participant started or stopped speaking.</summary>
/// <param name="PublicKey">Who.</param>
/// <param name="Speaking">Started (<see langword="true"/>) or stopped.</param>
public sealed record VoiceSpeakingEvent(string PublicKey, bool Speaking) : VoiceEvent;

/// <summary>The room's zones, after any change.</summary>
/// <param name="Zones">Every zone.</param>
public sealed record VoiceZonesEvent(IReadOnlyList<VoiceZone> Zones) : VoiceEvent;

/// <summary>A participant moved in a zone.</summary>
/// <param name="ZoneId">The zone.</param>
/// <param name="PublicKey">Who.</param>
/// <param name="Position">Where, 2 or 3 coordinates.</param>
public sealed record VoicePositionEvent(string ZoneId, string PublicKey, IReadOnlyList<double> Position) : VoiceEvent;

/// <summary>The hub refused a position (unknown zone, or someone else's creator-only zone).</summary>
/// <param name="Reason">The hub's explanation.</param>
public sealed record VoicePositionRefusedEvent(string Reason) : VoiceEvent;

/// <summary>A moderator gave this program the floor; <see cref="Voice.MaySpeak"/> is now true.</summary>
public sealed record VoiceTalkGrantedEvent : VoiceEvent;

/// <summary>A moderator, or the AFK sweep (<see cref="Auto"/>), asks the program to move. The hub moves no one.</summary>
/// <param name="TargetChannelId">Where to.</param>
/// <param name="TargetChannelName">Its name.</param>
/// <param name="SourceChannelId">Where from, when sent.</param>
/// <param name="EventId">The event that caused it, when sent.</param>
/// <param name="Auto">Whether the AFK sweep asked.</param>
public sealed record VoiceMoveRequestedEvent(string TargetChannelId, string TargetChannelName, string? SourceChannelId, string? EventId, bool Auto) : VoiceEvent;

/// <summary>The socket dropped, which ended the room on the hub; the SDK is joining again with a fresh key.</summary>
public sealed record VoiceRejoiningEvent : VoiceEvent;

/// <summary>A participant could not be given this program's key (no DH key, or a changed one), so it cannot hear it.</summary>
/// <param name="PublicKey">The participant.</param>
/// <param name="Reason">Why.</param>
public sealed record VoiceKeySkippedEvent(string PublicKey, string Reason) : VoiceEvent;

/// <summary>A <c>voice_*</c> message this SDK does not model.</summary>
/// <param name="RawType">Its <c>type</c>.</param>
/// <param name="Raw">The frame as received.</param>
public sealed record VoiceUnknownEvent(string RawType, string Raw) : VoiceEvent;

/// <summary>Something failed in the background; the room carries on.</summary>
/// <param name="Error">What failed.</param>
public sealed record VoiceErrorEvent(Exception Error) : VoiceEvent;

internal sealed record VoiceJoinMessage(string ChannelId) : ClientMessage("voice_join");

internal sealed record VoiceLeaveMessage(string ChannelId) : ClientMessage("voice_leave");

internal sealed record VoiceRtcOfferMessage(string Token, string Sdp) : ClientMessage("voice_rtc_offer");

internal sealed record VoiceSpeakingMessage(string ChannelId, bool Speaking) : ClientMessage("voice_speaking");

internal sealed record VoiceKeyBundle(string RecipientPubkey, string CiphertextHex, string NonceHex);

internal sealed record VoiceKeyOfferMessage(string ChannelId, IReadOnlyList<VoiceKeyBundle> Bundles) : ClientMessage("voice_key_offer");

internal sealed record VoicePositionUpdateMessage(string ZoneId, IReadOnlyList<double> Position) : ClientMessage("voice_position_update");

internal sealed record VoiceZoneCreateMessage(string ZoneId, string Name, string CoordinateSystem, object Attenuation, string AuthMode) : ClientMessage("voice_zone_create");

internal sealed record VoiceZoneDestroyMessage(string ZoneId) : ClientMessage("voice_zone_destroy");

/// <summary>
/// A joined voice room (<see cref="WavvonClient.JoinVoiceAsync"/>). Read events with
/// <see cref="ReadAllAsync"/> from one consumer; send from anywhere.
/// </summary>
public sealed class Voice : IAsyncDisposable
{
    private const int FrameMs = 20;
    private const uint FrameSamples = 960;
    // A program this far behind is re-anchored instead of bursting to catch up.
    private const int MaxLagFrames = 3;
    // More than one position every 50 ms risks the hub's silent 30 ms rate drop.
    private const int PositionEveryMs = 50;
    private const int SpeakingIdleMs = 200;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    private readonly WavvonClient _client;
    private readonly EventConnection _conn;
    private readonly IDhPinStore _pins;
    private readonly string _me;
    private readonly Channel<VoiceEvent> _out = Channel.CreateUnbounded<VoiceEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<ClientMessage> _outbox = Channel.CreateUnbounded<ClientMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, byte[]> _dhCache = [];
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private string _channelId;
    private ushort _senderId;
    private bool _inRoom;
    private bool _maySpeak = true;
    private bool _muted;
    private bool _deafened;
    private Dictionary<ushort, VoiceParticipant> _roster = [];
    private OwnKey _own = new(new byte[32], new byte[4], 0);
    private readonly Dictionary<(string, uint), (byte[] Key, byte[] Salt)> _remote = [];
    private readonly Dictionary<(ushort, uint), ulong> _marks = [];
    private List<VoiceZone> _zones = [];
    private VoiceLink? _link;
    private double? _pacingNext;
    private uint _pacingTs;
    private bool _speaking;
    private double _lastSend;
    private (string ZoneId, double[] Position)? _position;
    private double _lastPosition = double.NegativeInfinity;
    private Timer? _tick;
    private Task? _loop;
    private Task? _sender;
    private bool _stopped;

    private sealed class OwnKey(byte[] key, byte[] salt, uint keyId)
    {
        public byte[] Key { get; } = key;
        public byte[] Salt { get; } = salt;
        public uint KeyId { get; } = keyId;
        public ulong Ctr { get; set; }

        public static OwnKey Next(OwnKey previous) =>
            new(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(4), previous.KeyId + 1);
    }

    internal Voice(WavvonClient client, EventConnection conn, string channelId, IDhPinStore pins)
    {
        _client = client;
        _conn = conn;
        _pins = pins;
        _me = client.Identity.PublicKey;
        _channelId = channelId;
    }

    /// <summary>The room's channel. For a spawner room this is the spawned channel, not the one joined.</summary>
    public string ChannelId
    {
        get
        {
            lock (_gate)
            {
                return _channelId;
            }
        }
    }

    /// <summary>This program's relay id in the room.</summary>
    public ushort SenderId
    {
        get
        {
            lock (_gate)
            {
                return _senderId;
            }
        }
    }

    /// <summary>False means the relay drops what this program sends; <see cref="SendOpusAsync"/> then throws <see cref="VoiceErrorKind.MayNotSpeak"/>.</summary>
    public bool MaySpeak
    {
        get
        {
            lock (_gate)
            {
                return _maySpeak;
            }
        }
    }

    /// <summary>Who is in the room, by sender id.</summary>
    public IReadOnlyList<VoiceParticipant> Roster()
    {
        lock (_gate)
        {
            return RosterLocked();
        }
    }

    /// <summary>The room's zones.</summary>
    public IReadOnlyList<VoiceZone> Zones()
    {
        lock (_gate)
        {
            return [.. _zones];
        }
    }

    /// <summary>Every event until the room is left. Read it from one consumer only.</summary>
    public IAsyncEnumerable<VoiceEvent> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _out.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Local: the hub has no self-mute. Stops sending (calls still wait their slot) and ends the speaking indication.</summary>
    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            _muted = muted;
            if (muted && _speaking)
            {
                _speaking = false;
                Post(new VoiceSpeakingMessage(_channelId, false));
            }
        }
    }

    /// <summary>Local: stops delivering audio. Key distribution keeps running, so undeafening is instant.</summary>
    public void SetDeafened(bool deafened)
    {
        lock (_gate)
        {
            _deafened = deafened;
        }
    }

    /// <summary>
    /// Sends one 20 ms Opus packet, held until its slot on a monotonic clock. A source faster than
    /// real time is slowed to it; one more than three frames behind is re-anchored, <c>ts</c>
    /// jumping by the slots it missed.
    /// </summary>
    /// <exception cref="VoiceException">The packet is not 20 ms, too large, or the program cannot send now.</exception>
    public async Task SendOpusAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        int us;
        try
        {
            us = VoiceCrypto.OpusDurationUs(packet.Span);
        }
        catch (FormatException e)
        {
            throw new VoiceException(VoiceErrorKind.BadFrame, e.Message, e);
        }
        if (us != 20_000)
        {
            throw new VoiceException(VoiceErrorKind.BadFrame, $"{us / 1000.0} ms, not 20 ms");
        }
        // The hub adds the 3-byte relay prefix on the way out.
        if (16 + packet.Length + 16 + 3 > VoiceLink.MaxPacket)
        {
            throw new VoiceException(VoiceErrorKind.PacketTooLarge, $"a {packet.Length}-byte packet does not fit one SCTP chunk");
        }
        double slot;
        uint ts;
        lock (_gate)
        {
            if (!_inRoom)
            {
                throw new VoiceException(VoiceErrorKind.NotInRoom, "not in a voice room");
            }
            if (!_maySpeak)
            {
                throw new VoiceException(VoiceErrorKind.MayNotSpeak, "this program may not speak in this room");
            }
            var now = Now;
            var next = _pacingNext ?? now;
            if (now > next + (FrameMs * MaxLagFrames))
            {
                var missed = (uint)Math.Floor((now - next) / FrameMs);
                _pacingTs = unchecked(_pacingTs + (FrameSamples * missed));
                next = now;
            }
            slot = next;
            ts = _pacingTs;
            _pacingNext = next + FrameMs;
            _pacingTs = unchecked(_pacingTs + FrameSamples);
        }
        var wait = slot - Now;
        if (wait > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken).ConfigureAwait(false);
        }

        VoiceLink link;
        byte[] sealedPacket;
        lock (_gate)
        {
            if (!_inRoom || _link is null)
            {
                throw new VoiceException(VoiceErrorKind.NotInRoom, "not in a voice room");
            }
            if (_muted)
            {
                return;
            }
            link = _link;
            var ctr = _own.Ctr++;
            sealedPacket = VoiceCrypto.PacketSeal(_own.Key, _own.Salt, _own.KeyId, ctr, ts, packet.Span);
            _lastSend = Now;
            if (!_speaking)
            {
                _speaking = true;
                Post(new VoiceSpeakingMessage(_channelId, true));
            }
        }
        link.Send(sealedPacket);
    }

    /// <summary>
    /// The latest position in a zone of this room. At most one reaches the hub every 50 ms (it
    /// silently drops one under 30 ms after the last); the newest is always the one sent. Checked
    /// here, because the hub drops a bad one silently.
    /// </summary>
    /// <exception cref="VoiceException">No such zone, or the wrong number of coordinates.</exception>
    public void SetPosition(string zoneId, IReadOnlyList<double> position)
    {
        ArgumentNullException.ThrowIfNull(position);
        lock (_gate)
        {
            var z = _zones.Find(x => x.ZoneId == zoneId) ?? throw new VoiceException(VoiceErrorKind.UnknownZone, $"no zone {zoneId} in this room");
            var want = z.CoordinateSystem switch
            {
                "2d" => 2,
                "3d" => 3,
                _ => Math.Clamp(position.Count, 1, 3),
            };
            if (position.Count != want)
            {
                throw new VoiceException(VoiceErrorKind.BadPosition, $"{position.Count} coordinates, the zone wants {want}");
            }
            _position = (zoneId, [.. position]);
        }
    }

    /// <summary>
    /// Creates a proximity zone; needs <c>channels.manage</c> on the channel. <paramref name="attenuation"/>
    /// is the hub's <c>{model, max_radius, ref_dist, rolloff}</c> object; <see langword="null"/> takes the defaults.
    /// </summary>
    /// <param name="zoneId">The new zone's id.</param>
    /// <param name="name">Its name.</param>
    /// <param name="coordinateSystem"><c>2d</c> or <c>3d</c>.</param>
    /// <param name="attenuation">The attenuation object, or <see langword="null"/>.</param>
    /// <param name="creatorOnly">Whether only this program may stand in it.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public Task CreateZoneAsync(
        string zoneId,
        string name,
        string coordinateSystem,
        IReadOnlyDictionary<string, object>? attenuation = null,
        bool creatorOnly = false,
        CancellationToken cancellationToken = default) =>
        _conn.SendAsync(
            new VoiceZoneCreateMessage(zoneId, name, coordinateSystem, attenuation ?? new Dictionary<string, object>(), creatorOnly ? "creator_only" : "any_channel_member"),
            cancellationToken);

    /// <summary>Destroys a zone of this room.</summary>
    public Task DestroyZoneAsync(string zoneId, CancellationToken cancellationToken = default) =>
        _conn.SendAsync(new VoiceZoneDestroyMessage(zoneId), cancellationToken);

    /// <summary><c>voice_leave</c>, then the audio session and the room's socket close. Closing the session alone would not leave the roster.</summary>
    public async Task LeaveAsync()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }
            _inRoom = false;
            if (_speaking)
            {
                Post(new VoiceSpeakingMessage(_channelId, false));
            }
            Post(new VoiceLeaveMessage(_channelId));
            _link?.Dispose();
            _link = null;
        }
        _outbox.Writer.TryComplete();
        if (_sender is { } sender)
        {
            await sender.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        // Lets the hub read voice_leave before the socket closes.
        await Task.Delay(100).ConfigureAwait(false);
        await ShutdownAsync().ConfigureAwait(false);
    }

    /// <summary>Same as <see cref="LeaveAsync"/>.</summary>
    public async ValueTask DisposeAsync() => await LeaveAsync().ConfigureAwait(false);

    // ---- internals ----

    private double Now => _clock.Elapsed.TotalMilliseconds;

    private IReadOnlyList<VoiceParticipant> RosterLocked() => [.. _roster.Values.OrderBy(p => p.SenderId)];

    private void Emit(VoiceEvent e) => _out.Writer.TryWrite(e);

    private void Fail(Exception e) => Emit(new VoiceErrorEvent(e));

    private void Post(ClientMessage m) => _outbox.Writer.TryWrite(m);

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        _sender = Task.Run(SendLoopAsync, CancellationToken.None);
        while (await NextAsync(cancellationToken).ConfigureAwait(false) is not HelloEvent)
        {
        }
        await EstablishAsync(cancellationToken).ConfigureAwait(false);
        _tick = new Timer(_ => Housekeeping(), null, 25, 25);
        _loop = Task.Run(LoopAsync, CancellationToken.None);
    }

    internal async Task ShutdownAsync()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
            _inRoom = false;
            _link?.Dispose();
            _link = null;
        }
        if (_tick is { } tick)
        {
            await tick.DisposeAsync().ConfigureAwait(false);
        }
        _outbox.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _conn.DisposeAsync().ConfigureAwait(false);
        _out.Writer.TryComplete();
    }

    private async Task<WsEvent> NextAsync(CancellationToken cancellationToken, bool timeout = true)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        if (timeout)
        {
            cts.CancelAfter(StepTimeout);
        }
        try
        {
            return await _conn.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (ChannelClosedException e)
        {
            throw new VoiceException(VoiceErrorKind.Transport, "socket closed", e);
        }
        catch (OperationCanceledException e) when (timeout && !cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
        {
            throw new VoiceException(VoiceErrorKind.Transport, "timed out waiting for the hub", e);
        }
    }

    private async Task SendLoopAsync()
    {
        await foreach (var m in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _conn.SendAsync(m).ConfigureAwait(false);
            }
            catch (Exception e) when (e is InvalidOperationException or System.Net.WebSockets.WebSocketException or IOException or ObjectDisposedException)
            {
                Fail(e);
            }
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (true)
            {
                var ev = await NextAsync(CancellationToken.None, timeout: false).ConfigureAwait(false);
                // A second hello: the socket dropped and came back, and the hub already ran
                // leave_voice. The old session is dead; join again.
                if (ev is HelloEvent)
                {
                    await RejoinAsync().ConfigureAwait(false);
                }
                else
                {
                    HandleEvent(ev);
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
            // Left.
        }
        catch (VoiceException e)
        {
            Fail(e);
        }
    }

    private async Task RejoinAsync()
    {
        lock (_gate)
        {
            _inRoom = false;
            _link?.Dispose();
            _link = null;
        }
        Emit(new VoiceRejoiningEvent());
        try
        {
            await EstablishAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WavvonException or InvalidOperationException or System.Net.WebSockets.WebSocketException)
        {
            Fail(e);
        }
    }

    /// <summary><c>voice_join</c> on an open socket: joined, audio connected, key offered.</summary>
    private async Task EstablishAsync(CancellationToken cancellationToken)
    {
        var requested = ChannelId;
        await _conn.SendAsync(new VoiceJoinMessage(requested), cancellationToken).ConfigureAwait(false);
        JsonElement joined;
        while (true)
        {
            var ev = await NextAsync(cancellationToken).ConfigureAwait(false);
            if (ev is UnknownEvent { RawType: "voice_joined" } u)
            {
                joined = Parse(u.Raw);
                break;
            }
            if (ev is HubErrorEvent { Context: "voice_join" } refused)
            {
                throw new VoiceException(VoiceErrorKind.Refused, refused.Message);
            }
        }

        var link = await VoiceLink.OfferAsync().ConfigureAwait(false);
        // What arrives while the answer is awaited is kept, a key bundle above all: dropping it
        // would leave this room unable to open that speaker.
        var deferred = new List<WsEvent>();
        string answer;
        try
        {
            await _conn.SendAsync(new VoiceRtcOfferMessage(Str(joined, "voice_token"), link.OfferSdp), cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var ev = await NextAsync(cancellationToken).ConfigureAwait(false);
                if (ev is UnknownEvent { RawType: "voice_rtc_answer" } a)
                {
                    answer = Str(Parse(a.Raw), "sdp");
                    break;
                }
                if (ev is HubErrorEvent { Context: "voice_rtc_offer" } refused)
                {
                    throw new VoiceException(VoiceErrorKind.Refused, refused.Message);
                }
                deferred.Add(ev);
            }
            await link.AcceptAsync(answer, OnDatagram, () =>
            {
                bool ours;
                lock (_gate)
                {
                    ours = _link == link;
                    if (ours)
                    {
                        _link = null;
                    }
                    // After leave or during a rejoin the close is ours.
                    ours &= _inRoom;
                }
                if (ours)
                {
                    Fail(new VoiceException(VoiceErrorKind.Transport, "voice data channel closed"));
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            link.Dispose();
            throw;
        }

        var senderId = U16(joined, "sender_id");
        if (senderId is null)
        {
            link.Dispose();
            throw new VoiceException(VoiceErrorKind.Transport, "voice_joined carries no valid sender_id");
        }
        var participants = Participants(joined, "participants");
        List<string> peers;
        lock (_gate)
        {
            var channel = Str(joined, "channel_id");
            _channelId = channel.Length > 0 ? channel : requested;
            _senderId = senderId.Value;
            // The key id never goes back: a rejoin must not reuse a (sender, key_id) pair whose
            // replay watermark listeners still hold.
            _own = OwnKey.Next(_own);
            // A hub without voice.talk omits the field; absence is not "muted".
            _maySpeak = !(joined.TryGetProperty("may_speak", out var ms) && ms.ValueKind == JsonValueKind.False);
            _roster = participants.ToDictionary(p => p.SenderId);
            _remote.Clear();
            _marks.Clear();
            _pacingNext = null;
            _speaking = false;
            _link = link;
            _inRoom = true;
            peers = [.. participants.Select(p => p.PublicKey).Where(k => k != _me)];
        }
        _ = OfferToAsync(peers);
        foreach (var ev in deferred)
        {
            HandleEvent(ev);
        }
    }

    /// <summary>A peer's DH key, looked up once per room: it is pinned, so a second answer would be the same or a refusal.</summary>
    private async Task<byte[]> DhForAsync(string pubkey)
    {
        lock (_gate)
        {
            if (_dhCache.TryGetValue(pubkey, out var cached))
            {
                return cached;
            }
        }
        var k = await DhKeys.PeerAsync(_client, _pins, pubkey, _stop.Token).ConfigureAwait(false);
        lock (_gate)
        {
            _dhCache[pubkey] = k;
        }
        return k;
    }

    private static bool IsPerPeer(Exception e) =>
        e is RecipientHasNoKeyException or DhKeyChangedException or DhKeyUnsignedException or CryptographicException;

    /// <summary>Wraps the current sender key to each of <paramref name="to"/> and sends one offer. A peer without a usable DH key is skipped and reported.</summary>
    private async Task OfferToAsync(List<string> to)
    {
        if (to.Count == 0)
        {
            return;
        }
        string channel;
        OwnKey own;
        lock (_gate)
        {
            channel = _channelId;
            own = _own;
        }
        var bundles = new List<VoiceKeyBundle>();
        foreach (var pubkey in to)
        {
            try
            {
                var dh = await DhForAsync(pubkey).ConfigureAwait(false);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var ct = VoiceCrypto.KeyWrap(_client.Identity, dh, channel, own.Key, own.Salt, own.KeyId, nonce);
                bundles.Add(new VoiceKeyBundle(pubkey, Hex.Encode(ct), Hex.Encode(nonce)));
            }
            catch (Exception e) when (IsPerPeer(e))
            {
                Emit(new VoiceKeySkippedEvent(pubkey, e.Message));
            }
            catch (Exception e) when (!_stop.IsCancellationRequested)
            {
                Fail(e);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if (bundles.Count > 0)
        {
            Post(new VoiceKeyOfferMessage(channel, bundles));
        }
    }

    private async Task UnwrapAsync(JsonElement raw)
    {
        var from = Str(raw, "from_pubkey");
        var ct = Hex.TryDecode(Str(raw, "ciphertext_hex"));
        var nonce = Hex.TryDecode(Str(raw, "nonce_hex"));
        if (ct is null || nonce is not { Length: 12 })
        {
            return;
        }
        string channel;
        lock (_gate)
        {
            channel = _channelId;
        }
        byte[] dh;
        try
        {
            dh = await DhForAsync(from).ConfigureAwait(false);
        }
        catch (Exception e) when (IsPerPeer(e))
        {
            Emit(new VoiceKeySkippedEvent(from, e.Message));
            return;
        }
        catch (Exception e) when (!_stop.IsCancellationRequested)
        {
            Fail(e);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        (byte[] SenderKey, byte[] NonceSalt, uint KeyId) k;
        try
        {
            k = VoiceCrypto.KeyUnwrap(_client.Identity, dh, channel, ct, nonce);
        }
        catch (CryptographicException)
        {
            // A bundle that does not open is dropped: there is no key to use.
            return;
        }
        lock (_gate)
        {
            // A new key for this pair restarts its counter.
            foreach (var p in _roster.Values.Where(p => p.PublicKey == from))
            {
                _marks.Remove((p.SenderId, k.KeyId));
            }
            _remote[(from, k.KeyId)] = (k.SenderKey, k.NonceSalt);
        }
    }

    private void HandleEvent(WsEvent ev)
    {
        if (ev is HubErrorEvent error)
        {
            if (error.Context == "voice_position_update")
            {
                Emit(new VoicePositionRefusedEvent(error.Message));
            }
            else if (error.Context.StartsWith("voice", StringComparison.Ordinal))
            {
                Fail(new VoiceException(VoiceErrorKind.Refused, $"{error.Context}: {error.Message}"));
            }
            return;
        }
        if (ev is not UnknownEvent { RawType: var type } unknown || !type.StartsWith("voice_", StringComparison.Ordinal))
        {
            return;
        }
        var raw = Parse(unknown.Raw);
        lock (_gate)
        {
            // Messages about another room are not this session's.
            if (raw.TryGetProperty("channel_id", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() != _channelId)
            {
                return;
            }
            switch (type)
            {
                case "voice_participant_joined":
                    if (raw.TryGetProperty("participant", out var pj) && Participant(pj) is { } joined)
                    {
                        _roster[joined.SenderId] = joined;
                        Emit(new VoiceRosterEvent(RosterLocked()));
                    }
                    break;
                case "voice_participant_left":
                    var left = Str(raw, "public_key");
                    foreach (var p in _roster.Values.Where(p => p.PublicKey == left).ToList())
                    {
                        _roster.Remove(p.SenderId);
                        foreach (var m in _marks.Keys.Where(m => m.Item1 == p.SenderId).ToList())
                        {
                            _marks.Remove(m);
                        }
                    }
                    foreach (var r in _remote.Keys.Where(r => r.Item1 == left).ToList())
                    {
                        _remote.Remove(r);
                    }
                    // Whoever left kept the key: a new one for the rest.
                    _own = OwnKey.Next(_own);
                    Emit(new VoiceRosterEvent(RosterLocked()));
                    _ = OfferToAsync([.. _roster.Values.Select(p => p.PublicKey).Where(k => k != _me)]);
                    break;
                case "voice_roster_update":
                    _roster = Participants(raw, "participants").ToDictionary(p => p.SenderId);
                    Emit(new VoiceRosterEvent(RosterLocked()));
                    break;
                case "voice_participant_speaking":
                    Emit(new VoiceSpeakingEvent(Str(raw, "public_key"), raw.TryGetProperty("speaking", out var sp) && sp.ValueKind == JsonValueKind.True));
                    break;
                case "voice_key_request":
                    var newcomer = Str(raw, "new_pubkey");
                    if (U16(raw, "new_sender_id") is { } nid && !_roster.ContainsKey(nid))
                    {
                        _roster[nid] = new VoiceParticipant(newcomer, nid);
                    }
                    if (newcomer != _me)
                    {
                        _ = OfferToAsync([newcomer]);
                    }
                    break;
                case "voice_key_received":
                    if (U16(raw, "from_sender_id") is { } fid && !_roster.ContainsKey(fid))
                    {
                        _roster[fid] = new VoiceParticipant(Str(raw, "from_pubkey"), fid);
                    }
                    _ = UnwrapAsync(raw);
                    break;
                case "voice_talk_granted":
                    if (Str(raw, "public_key") == _me)
                    {
                        _maySpeak = true;
                        Emit(new VoiceTalkGrantedEvent());
                    }
                    break;
                case "voice_move":
                    Emit(new VoiceMoveRequestedEvent(
                        Str(raw, "target_channel_id"),
                        Str(raw, "target_channel_name"),
                        OptStr(raw, "source_channel_id"),
                        OptStr(raw, "event_id"),
                        raw.TryGetProperty("auto", out var auto) && auto.ValueKind == JsonValueKind.True));
                    break;
                case "voice_zone_state":
                    _zones = raw.TryGetProperty("zones", out var zs) && zs.ValueKind == JsonValueKind.Array
                        ? [.. zs.EnumerateArray().Select(Zone).OfType<VoiceZone>()]
                        : [];
                    Emit(new VoiceZonesEvent([.. _zones]));
                    break;
                case "voice_zone_created":
                    if (Zone(raw) is { } created)
                    {
                        _zones = [.. _zones.Where(x => x.ZoneId != created.ZoneId), created];
                        Emit(new VoiceZonesEvent([.. _zones]));
                    }
                    break;
                case "voice_zone_destroyed":
                    var gone = Str(raw, "zone_id");
                    _zones = [.. _zones.Where(z => z.ZoneId != gone)];
                    Emit(new VoiceZonesEvent([.. _zones]));
                    break;
                case "voice_position_updated":
                    var zoneId = Str(raw, "zone_id");
                    var who = Str(raw, "pubkey");
                    var position = raw.TryGetProperty("position", out var pos) ? Numbers(pos) : [];
                    var i = _zones.FindIndex(x => x.ZoneId == zoneId);
                    if (i >= 0)
                    {
                        var z = _zones[i];
                        _zones[i] = z with { Positions = new Dictionary<string, IReadOnlyList<double>>(z.Positions) { [who] = position } };
                    }
                    Emit(new VoicePositionEvent(zoneId, who, position));
                    break;
                case "voice_joined":
                case "voice_rtc_answer":
                    break;
                default:
                    Emit(new VoiceUnknownEvent(type, unknown.Raw));
                    break;
            }
        }
    }

    private void OnDatagram(byte[] payload)
    {
        // sender_id[2] || packet_type[1] || key_id[4] || ctr[8] || ts[4] || AEAD
        if (payload.Length < 3 + 32)
        {
            return;
        }
        var senderId = BinaryPrimitives.ReadUInt16BigEndian(payload);
        var whisper = payload[2] == 1;
        var sealedPacket = payload.AsSpan(3);
        var keyId = BinaryPrimitives.ReadUInt32BigEndian(sealedPacket);
        var ctr = BinaryPrimitives.ReadUInt64BigEndian(sealedPacket[4..]);
        lock (_gate)
        {
            if (_deafened || !_roster.TryGetValue(senderId, out var from) || !_remote.TryGetValue((from.PublicKey, keyId), out var k))
            {
                return;
            }
            var mark = (senderId, keyId);
            if (_marks.TryGetValue(mark, out var seen) && ctr <= seen)
            {
                return;
            }
            (uint KeyId, ulong Ctr, uint Ts, byte[] Opus) opened;
            try
            {
                opened = VoiceCrypto.PacketOpen(k.Key, k.Salt, sealedPacket);
            }
            catch (CryptographicException)
            {
                return;
            }
            // The watermark moves only for a packet that opened: a forged one must not be able to
            // lock the real sender out.
            _marks[mark] = ctr;
            Emit(new VoiceAudioEvent(from.PublicKey, senderId, keyId, opened.Ts, ctr, whisper, opened.Opus));
        }
    }

    /// <summary>Every 25 ms: end a transmission that went quiet, and send the trailing position once the hub's limit is safely behind.</summary>
    private void Housekeeping()
    {
        lock (_gate)
        {
            if (!_inRoom)
            {
                return;
            }
            var now = Now;
            if (_speaking && now - _lastSend > SpeakingIdleMs)
            {
                _speaking = false;
                Post(new VoiceSpeakingMessage(_channelId, false));
            }
            if (_position is { } p && now - _lastPosition >= PositionEveryMs)
            {
                _position = null;
                _lastPosition = now;
                Post(new VoicePositionUpdateMessage(p.ZoneId, p.Position));
            }
        }
    }

    // ---- JSON ----

    private static JsonElement Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static string Str(JsonElement e, string name) => OptStr(e, name) ?? "";

    private static string? OptStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ushort? U16(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt16(out var n) ? n : null;

    private static double[] Numbers(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? [.. e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble())] : [];

    private static VoiceParticipant? Participant(JsonElement e) =>
        OptStr(e, "public_key") is { } key && U16(e, "sender_id") is { } id ? new VoiceParticipant(key, id, OptStr(e, "display_name")) : null;

    private static List<VoiceParticipant> Participants(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? [.. a.EnumerateArray().Select(Participant).OfType<VoiceParticipant>()] : [];

    private static VoiceZone? Zone(JsonElement e)
    {
        if (OptStr(e, "zone_id") is not { } id)
        {
            return null;
        }
        var positions = new Dictionary<string, IReadOnlyList<double>>();
        if (e.TryGetProperty("positions", out var ps) && ps.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in ps.EnumerateObject())
            {
                positions[p.Name] = Numbers(p.Value);
            }
        }
        var attenuation = e.TryGetProperty("attenuation", out var a) ? a.Clone() : default;
        return new VoiceZone(id, Str(e, "name"), Str(e, "coordinate_system"), attenuation, positions);
    }
}
