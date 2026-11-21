using System.Net;

namespace Wavvon.Sdk;

/// <summary>Base class: every exception this SDK throws on purpose derives from it.</summary>
public class WavvonException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public WavvonException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public WavvonException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>The hub does not advertise a capability the call needs. Never a version check.</summary>
public sealed class UnsupportedByHubException(string capability)
    : WavvonException($"this hub does not advertise the \"{capability}\" capability")
{
    /// <summary>The capability string the hub did not list in <c>GET /info</c>.</summary>
    public string Capability { get; } = capability;
}

/// <summary>
/// The hub could not be reached or kept saying "not now" (429, 5xx, timeout, network) until the
/// retry budget ran out. This is not an answer: the request may succeed later, and nothing about
/// the resource can be concluded from it.
/// </summary>
public sealed class HubUnavailableException(string message, HttpStatusCode? lastStatus, Exception? innerException)
    : WavvonException(message, innerException)
{
    /// <summary>The last transient status, or <see langword="null"/> after a network error or timeout.</summary>
    public HttpStatusCode? LastStatus { get; } = lastStatus;
}

/// <summary>A definitive refusal from the hub: any non-success status other than 429 and 5xx.</summary>
public sealed class HubException(HttpStatusCode status, string body)
    : WavvonException($"hub answered {(int)status}: {body}")
{
    /// <summary>The HTTP status the hub answered with.</summary>
    public HttpStatusCode Status { get; } = status;

    /// <summary>The response body, which carries the hub's own explanation.</summary>
    public string Body { get; } = body;
}

/// <summary>
/// A poll cursor is older than the hub's 72-hour window: events after it were missed and cannot be
/// replayed. Resynchronise from REST, then poll without a cursor.
/// </summary>
public sealed class ReplayUnavailableException(long earliestSeq, long earliestAt)
    : WavvonException($"events before seq {earliestSeq} are outside the hub's replay window")
{
    /// <summary>The oldest seq the hub still holds.</summary>
    public long EarliestSeq { get; } = earliestSeq;

    /// <summary>When that event happened, in Unix seconds.</summary>
    public long EarliestAt { get; } = earliestAt;
}

/// <summary>The challenge could not be answered safely (unknown scheme, wrong length).</summary>
public sealed class AuthSchemeException(string message) : WavvonException(message);

/// <summary>A webhook request failed verification; answer it with 401 and do nothing else.</summary>
public sealed class WebhookVerificationException(string message) : WavvonException(message);

/// <summary>The peer has published no DH key on this hub: nothing can be wrapped to it.</summary>
public sealed class RecipientHasNoKeyException(string publicKey)
    : WavvonException($"{publicKey} has published no DH key on this hub")
{
    /// <summary>The peer.</summary>
    public string PublicKey { get; } = publicKey;
}

/// <summary>
/// The hub holds a DH key for a peer that differs from the one pinned on first use. It is not used;
/// <see cref="WavvonClient.AcceptDhKeyAsync"/> re-pins once the program decides to.
/// </summary>
public sealed class DhKeyChangedException(string publicKey, string pinned, string fetched)
    : WavvonException($"the DH key of {publicKey} changed since it was pinned")
{
    /// <summary>The peer.</summary>
    public string PublicKey { get; } = publicKey;

    /// <summary>The DH key pinned earlier, hex.</summary>
    public string Pinned { get; } = pinned;

    /// <summary>The DH key the hub serves now, hex.</summary>
    public string Fetched { get; } = fetched;
}

/// <summary>The hub served a DH key record the peer did not sign. Never read as "no key".</summary>
public sealed class DhKeyUnsignedException(string publicKey)
    : WavvonException($"the DH key record of {publicKey} is not signed by it")
{
    /// <summary>The peer.</summary>
    public string PublicKey { get; } = publicKey;
}

/// <summary>What went wrong in a voice room.</summary>
public enum VoiceErrorKind
{
    /// <summary>The socket or the WebRTC data channel failed, or the hub did not answer in time.</summary>
    Transport,

    /// <summary>The hub refused a voice request.</summary>
    Refused,

    /// <summary>The packet is not one 20 ms Opus packet.</summary>
    BadFrame,

    /// <summary>The room was left, or is being joined again.</summary>
    NotInRoom,

    /// <summary>The room gives this program no talk power; the relay would drop what it sends.</summary>
    MayNotSpeak,

    /// <summary>The sealed packet would not fit one SCTP chunk.</summary>
    PacketTooLarge,

    /// <summary>The room has no zone with that id.</summary>
    UnknownZone,

    /// <summary>The position has the wrong number of coordinates for its zone.</summary>
    BadPosition,
}

/// <summary>A voice call failed; <see cref="Kind"/> says how.</summary>
public sealed class VoiceException(VoiceErrorKind kind, string message, Exception? innerException = null)
    : WavvonException(message, innerException)
{
    /// <summary>What went wrong.</summary>
    public VoiceErrorKind Kind { get; } = kind;
}
