using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Kiota.Serialization.Json;
using Wavvon.Sdk.Generated.Models;

namespace Wavvon.Sdk;

// Server -> client frames this SDK models (ws-protocol.md, checked against Wavvon-server
// crates/hub/src/routes/chat_models.rs). Every other `type` arrives as UnknownEvent.

/// <summary>One frame from the hub WebSocket. Switch on the concrete type.</summary>
public abstract record WsEvent
{
    private protected WsEvent() { }

    /// <summary>
    /// Turns one text frame into a typed event. A frame of a type this SDK does not model, or that is
    /// not JSON at all, becomes an <see cref="UnknownEvent"/>; nothing is dropped.
    /// </summary>
    public static WsEvent Parse(string frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(frame);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new UnknownEvent("", frame);
        }
        var type = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("type", out var t)
            && t.ValueKind == JsonValueKind.String
                ? t.GetString()!
                : "";
        try
        {
            return type switch
            {
                "hello" => Read<HelloEvent>(root),
                "error" => Read<HubErrorEvent>(root),
                "pong" => Read<PongEvent>(root),
                "message" => new MessageEvent(Text(root, "channel_id"), ReadMessage(root)),
                "message_edited" => new MessageEditedEvent(Text(root, "channel_id"), ReadMessage(root)),
                "message_deleted" => Read<MessageDeletedEvent>(root),
                "message_pinned" => Read<MessagePinnedEvent>(root),
                "message_unpinned" => Read<MessageUnpinnedEvent>(root),
                "reactions_updated" => Read<ReactionsUpdatedEvent>(root),
                "typing" => Read<TypingEvent>(root),
                "dm" => Read<DmEvent>(root),
                "hub_event" => Read<HubEvent>(root),
                "replay_complete" => Read<ReplayCompleteEvent>(root),
                "replay_unavailable" => Read<ReplayUnavailableEvent>(root),
                "app_launch" => Read<AppLaunchEvent>(root),
                "app_open" => Read<AppOpenEvent>(root),
                "app_close" => Read<AppCloseEvent>(root),
                "mini_app_message" => Read<MiniAppMessageEvent>(root),
                _ => new UnknownEvent(type, frame),
            };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // A known type with a shape this SDK does not expect is still delivered.
            return new UnknownEvent(type, frame);
        }
    }

    private static T Read<T>(JsonElement e) => e.Deserialize<T>(WavvonJson.Options) ?? throw new JsonException("null frame");

    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";

    private static Message ReadMessage(JsonElement e) =>
        new JsonParseNode(e.GetProperty("message")).GetObjectValue(Message.CreateFromDiscriminatorValue)
        ?? throw new JsonException("message frame without a message");
}

/// <summary>A frame of a type this SDK does not model, or not JSON at all. Never swallowed.</summary>
/// <param name="RawType">The frame's own <c>type</c>; <c>binary</c> for a binary frame; empty if unparseable.</param>
/// <param name="Raw">The frame as received (base64 for a binary frame).</param>
public sealed record UnknownEvent(string RawType, string Raw) : WsEvent;

/// <summary>The first frame on every connection.</summary>
/// <param name="LiveSeq">The hub's current <c>hub_event</c> seq.</param>
public sealed record HelloEvent(long LiveSeq) : WsEvent;

/// <summary>The hub refused something this socket sent.</summary>
/// <param name="Context">What was refused.</param>
/// <param name="Message">The hub's explanation.</param>
public sealed record HubErrorEvent(string Context, string Message) : WsEvent;

/// <summary>The answer to a <see cref="PingMessage"/>.</summary>
/// <param name="Nonce">The ping's nonce.</param>
/// <param name="OutboundLossPct">Voice loss the hub measured, when it has a figure.</param>
public sealed record PongEvent(long Nonce, double? OutboundLossPct) : WsEvent;

/// <summary>A new message in a subscribed channel.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="Message">The message, as the REST API models it.</param>
public sealed record MessageEvent(string ChannelId, Message Message) : WsEvent;

/// <summary>A message was edited.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="Message">The message after the edit.</param>
public sealed record MessageEditedEvent(string ChannelId, Message Message) : WsEvent;

/// <summary>A message was deleted.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="MessageId">The deleted message.</param>
public sealed record MessageDeletedEvent(string ChannelId, string MessageId) : WsEvent;

/// <summary>A message was pinned.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="MessageId">The pinned message.</param>
public sealed record MessagePinnedEvent(string ChannelId, string MessageId) : WsEvent;

/// <summary>A message was unpinned.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="MessageId">The unpinned message.</param>
public sealed record MessageUnpinnedEvent(string ChannelId, string MessageId) : WsEvent;

/// <summary>One emoji's reactions on a message.</summary>
/// <param name="Emoji">The emoji.</param>
/// <param name="Count">How many reacted with it.</param>
/// <param name="Me">Whether this identity is one of them.</param>
public sealed record ReactionCount(string Emoji, int Count, bool Me);

/// <summary>The reactions on a message changed.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="MessageId">The message.</param>
/// <param name="Reactions">Every reaction it now has.</param>
public sealed record ReactionsUpdatedEvent(string ChannelId, string MessageId, IReadOnlyList<ReactionCount> Reactions) : WsEvent;

/// <summary>Someone started or stopped typing.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="PublicKey">Who.</param>
/// <param name="DisplayName">Their display name, if they have one.</param>
/// <param name="IsTyping">Started (<see langword="true"/>) or stopped.</param>
public sealed record TypingEvent(
    string ChannelId,
    string PublicKey,
    string? DisplayName,
    [property: JsonPropertyName("typing")] bool IsTyping) : WsEvent;

/// <summary>A direct message (cleartext only; tier 1 does no DM encryption).</summary>
/// <param name="ConversationId">The conversation.</param>
/// <param name="Sender">The sender's public key.</param>
/// <param name="SenderName">The sender's display name, if any.</param>
/// <param name="Content">The text.</param>
/// <param name="Timestamp">When it was sent.</param>
public sealed record DmEvent(string ConversationId, string Sender, string? SenderName, string Content, long Timestamp) : WsEvent;

/// <summary>
/// One envelope for live push, replay and outgoing webhooks (apps.md §5). The event connection tracks
/// <see cref="Seq"/> and resumes from it after a reconnect.
/// </summary>
/// <param name="Seq">Position in the hub's event log.</param>
/// <param name="Event">For example <c>member.joined</c> or <c>message.created</c>.</param>
/// <param name="HubUrl">The hub that emitted it.</param>
/// <param name="At">Unix seconds.</param>
/// <param name="Payload">The event's own fields.</param>
/// <param name="ActorPubkey">Replay only, when the audit row has it.</param>
/// <param name="TargetPubkey">Replay only, when the audit row has it.</param>
/// <param name="ChannelId">Replay only, when the audit row has it.</param>
/// <param name="Replayed"><see langword="true"/> when the event came from a replay.</param>
public record HubEvent(
    long Seq,
    string Event,
    string HubUrl,
    long At,
    JsonElement Payload,
    string? ActorPubkey = null,
    string? TargetPubkey = null,
    string? ChannelId = null,
    bool? Replayed = null) : WsEvent;

/// <summary>A replay finished; live events follow.</summary>
/// <param name="Replayed">How many events the replay carried.</param>
/// <param name="LiveFromSeq">The seq live events continue from.</param>
public sealed record ReplayCompleteEvent(long Replayed, long LiveFromSeq) : WsEvent;

/// <summary>The resume point is older than the hub keeps (72 hours): events were missed.</summary>
/// <param name="EarliestSeq">The oldest seq the hub still has.</param>
/// <param name="EarliestAt">When that event happened, unix seconds.</param>
public sealed record ReplayUnavailableEvent(long EarliestSeq, long EarliestAt) : WsEvent;

/// <summary>A mini-app launch card appeared in a channel.</summary>
/// <param name="AppId">The app's public key.</param>
/// <param name="Title">The card's title.</param>
/// <param name="Description">The card's text.</param>
/// <param name="ChannelId">The channel.</param>
public sealed record AppLaunchEvent(string AppId, string Title, string Description, string ChannelId) : WsEvent;

/// <summary>A mini-app session opened for this identity.</summary>
/// <param name="AppId">The app's public key.</param>
/// <param name="ChannelId">The channel.</param>
/// <param name="MiniAppUrl">What the webview loads.</param>
/// <param name="SessionToken">The session the mini-app proves itself with.</param>
/// <param name="RequiresCamera">Whether the mini-app asks for the camera.</param>
public sealed record AppOpenEvent(string AppId, string ChannelId, string MiniAppUrl, string SessionToken, bool RequiresCamera) : WsEvent;

/// <summary>A mini-app session closed.</summary>
/// <param name="AppId">The app's public key.</param>
/// <param name="ChannelId">The channel.</param>
public sealed record AppCloseEvent(string AppId, string ChannelId) : WsEvent;

/// <summary>An opaque mini-app payload. On the host side <see cref="FromPubkey"/> names the player.</summary>
/// <param name="AppId">The app's public key.</param>
/// <param name="ChannelId">The channel.</param>
/// <param name="Payload">Whatever the two sides agreed on.</param>
/// <param name="FromPubkey">The player who sent it, when the hub relays to the host.</param>
public sealed record MiniAppMessageEvent(string AppId, string ChannelId, string Payload, string? FromPubkey) : WsEvent;

/// <summary>A frame a tier-1 program sends over the socket.</summary>
/// <param name="Type">The wire <c>type</c>.</param>
public abstract record ClientMessage(string Type);

/// <summary>Receive a channel's live messages.</summary>
/// <param name="ChannelId">The channel.</param>
public sealed record SubscribeMessage(string ChannelId) : ClientMessage("subscribe");

/// <summary>Stop receiving a channel's live messages.</summary>
/// <param name="ChannelId">The channel.</param>
public sealed record UnsubscribeMessage(string ChannelId) : ClientMessage("unsubscribe");

/// <summary>Show or clear the typing indicator.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="IsTyping">Show (<see langword="true"/>) or clear.</param>
public sealed record TypingMessage(string ChannelId, [property: JsonPropertyName("typing")] bool IsTyping) : ClientMessage("typing");

/// <summary>Ask for a <see cref="PongEvent"/>. Needs the <c>ws.ping</c> capability.</summary>
/// <param name="Nonce">Echoed back in the pong.</param>
public sealed record PingMessage(long Nonce) : ClientMessage("ping");

/// <summary>Replay hub events after a seq. The event connection sends this itself after a reconnect.</summary>
/// <param name="SinceSeq">The last seq already seen.</param>
public sealed record ResumeMessage(long SinceSeq) : ClientMessage("resume");

/// <summary>Press a button or choose from a select.</summary>
/// <param name="MessageId">The message carrying the component.</param>
/// <param name="CustomId">The component's id.</param>
/// <param name="Values">The chosen values of a select.</param>
public sealed record ComponentInteractionMessage(string MessageId, string CustomId, IReadOnlyList<string>? Values = null)
    : ClientMessage("component_interaction");

/// <summary>Mini-app host: show a launch card in a channel.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="Title">The card's title.</param>
/// <param name="Description">The card's text.</param>
public sealed record AppAnnounceMessage(string ChannelId, string Title, string Description) : ClientMessage("app_announce");

/// <summary>Mini-app host: close the session; clients dismiss their webviews.</summary>
/// <param name="ChannelId">The channel.</param>
public sealed record AppDismissMessage(string ChannelId) : ClientMessage("app_dismiss");

/// <summary>Mini-app host: relay an opaque payload to one joined player.</summary>
/// <param name="AppId">The app's public key.</param>
/// <param name="ChannelId">The channel.</param>
/// <param name="Payload">Whatever the two sides agreed on.</param>
/// <param name="ToPubkey">The player to deliver to.</param>
public sealed record MiniAppMessage(string AppId, string ChannelId, string Payload, string? ToPubkey = null) : ClientMessage("mini_app_message");
