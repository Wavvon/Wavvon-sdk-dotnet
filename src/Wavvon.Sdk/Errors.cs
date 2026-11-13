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
