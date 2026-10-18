using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wavvon.Sdk;

// Wire shapes: Wavvon-server crates/hub/src/routes/apps/models.rs.

/// <summary>Who invoked a command or pressed a component.</summary>
/// <param name="Pubkey">Their public key.</param>
/// <param name="DisplayName">Their display name, if any.</param>
public sealed record AuthorInfo(string Pubkey, string? DisplayName = null);

/// <summary>A verified dispatch from the hub to the app's webhook. Switch on the concrete type.</summary>
public abstract record Dispatch
{
    private protected Dispatch() { }
}

/// <summary>Someone ran one of the app's slash commands.</summary>
/// <param name="HubUrl">The hub.</param>
/// <param name="ChannelId">Where.</param>
/// <param name="MessageIdHint">The id the invoking message would have had.</param>
/// <param name="Author">Who.</param>
/// <param name="Command">The command name, without the slash.</param>
/// <param name="ArgsRaw">Everything after the command name.</param>
/// <param name="ArgsTokens">The arguments split on whitespace.</param>
public sealed record SlashInvocation(
    string HubUrl,
    string ChannelId,
    string MessageIdHint,
    AuthorInfo Author,
    string Command,
    string ArgsRaw,
    IReadOnlyList<string> ArgsTokens) : Dispatch;

/// <summary>Someone pressed a button or chose from a select on one of the app's messages.</summary>
/// <param name="HubUrl">The hub.</param>
/// <param name="ChannelId">Where.</param>
/// <param name="MessageId">The message carrying the component.</param>
/// <param name="CustomId">The component's id.</param>
/// <param name="Values">The chosen values of a select.</param>
/// <param name="User">Who.</param>
public sealed record ComponentInteraction(
    string HubUrl,
    string ChannelId,
    string MessageId,
    string CustomId,
    IReadOnlyList<string> Values,
    AuthorInfo User) : Dispatch;

/// <summary>A verified dispatch whose <c>type</c> this SDK does not know. Delivered, never dropped.</summary>
/// <param name="RawType">The dispatch's own <c>type</c>.</param>
/// <param name="Raw">The whole body.</param>
public sealed record UnknownDispatch(string RawType, JsonElement Raw) : Dispatch;

/// <summary>A rich card under a message.</summary>
public sealed record Embed
{
    /// <summary>The heading.</summary>
    public string? Title { get; init; }

    /// <summary>Where the heading links to.</summary>
    public string? Url { get; init; }

    /// <summary>The body text.</summary>
    public string? Description { get; init; }

    /// <summary>The accent colour, for example <c>#ffb000</c>.</summary>
    public string? Color { get; init; }

    /// <summary>Name/value pairs.</summary>
    public IReadOnlyList<EmbedField>? Fields { get; init; }

    /// <summary>A small image beside the text.</summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>A large image under the text.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>A line at the bottom.</summary>
    public EmbedFooter? Footer { get; init; }
}

/// <summary>One name/value pair of an <see cref="Embed"/>.</summary>
/// <param name="Name">The label.</param>
/// <param name="Value">The value.</param>
/// <param name="Inline">Whether it may share a line with its neighbours.</param>
public sealed record EmbedField(string Name, string Value, bool? Inline = null);

/// <summary>The footer of an <see cref="Embed"/>.</summary>
/// <param name="Text">The text.</param>
public sealed record EmbedFooter(string Text);

/// <summary>One option of a select component.</summary>
/// <param name="Label">What the user sees.</param>
/// <param name="Value">What the app receives.</param>
/// <param name="Description">A second line.</param>
public sealed record SelectOption(string Label, string Value, string? Description = null);

/// <summary>A button or a select.</summary>
/// <param name="Type"><c>button</c> or <c>select</c>.</param>
/// <param name="CustomId">Comes back in <see cref="ComponentInteraction.CustomId"/>.</param>
public sealed record AppComponent(string Type, string CustomId)
{
    /// <summary>The button's text.</summary>
    public string? Label { get; init; }

    /// <summary>The button's style.</summary>
    public string? Style { get; init; }

    /// <summary>Shown but not pressable.</summary>
    public bool? Disabled { get; init; }

    /// <summary>The select's text before a choice.</summary>
    public string? Placeholder { get; init; }

    /// <summary>The fewest values a select accepts.</summary>
    public int? MinValues { get; init; }

    /// <summary>The most values a select accepts.</summary>
    public int? MaxValues { get; init; }

    /// <summary>The select's options.</summary>
    public IReadOnlyList<SelectOption>? Options { get; init; }
}

/// <summary>A row of components.</summary>
/// <param name="Components">The components, left to right.</param>
public sealed record ComponentRow(IReadOnlyList<AppComponent> Components)
{
    /// <summary>Always <c>row</c>.</summary>
    public string Type { get; } = "row";
}

/// <summary>A "Play" card that launches a game.</summary>
/// <param name="EntryUrl">What the game webview loads.</param>
/// <param name="Name">The game's name.</param>
/// <param name="Description">A line about it.</param>
/// <param name="ThumbnailUrl">A picture.</param>
public sealed record GameLaunchCard(string EntryUrl, string Name, string? Description = null, string? ThumbnailUrl = null);

/// <summary>The message an app posts in answer.</summary>
/// <param name="Body">The text.</param>
public sealed record AppReply(string Body)
{
    /// <summary>Cards under the text.</summary>
    public IReadOnlyList<Embed>? Embeds { get; init; }

    /// <summary>Buttons and selects.</summary>
    public IReadOnlyList<ComponentRow>? Components { get; init; }

    /// <summary>A message this replies to.</summary>
    public string? ReplyTo { get; init; }
}

/// <summary>A reaction the app adds.</summary>
/// <param name="MessageId">The message.</param>
/// <param name="Emoji">The emoji.</param>
public sealed record AppReaction(string MessageId, string Emoji);

/// <summary>What a slash command handler answers.</summary>
public sealed record AppResponse
{
    /// <summary>A message to post.</summary>
    public AppReply? Reply { get; init; }

    /// <summary>Only the invoker sees the reply.</summary>
    public bool? Ephemeral { get; init; }

    /// <summary>Reactions to add.</summary>
    public IReadOnlyList<AppReaction>? Reactions { get; init; }

    /// <summary>The app will post later on its own.</summary>
    public bool? Defer { get; init; }

    /// <summary>A "Play" card attached to the reply; ignored without a reply.</summary>
    public GameLaunchCard? Game { get; init; }
}

/// <summary>A new body or components for the message a component sits on.</summary>
public sealed record ComponentUpdate
{
    /// <summary>The new text.</summary>
    public string? Body { get; init; }

    /// <summary>The new components.</summary>
    public IReadOnlyList<ComponentRow>? Components { get; init; }
}

/// <summary>A reply only the user who pressed sees.</summary>
/// <param name="Body">The text.</param>
public sealed record EphemeralReply(string Body);

/// <summary>What a component interaction handler answers.</summary>
public sealed record ComponentResponse
{
    /// <summary>Edit the message the component sits on.</summary>
    public ComponentUpdate? Update { get; init; }

    /// <summary>Answer only the user who pressed.</summary>
    public EphemeralReply? EphemeralReply { get; init; }

    /// <summary>The app will act later on its own.</summary>
    public bool? Defer { get; init; }
}

/// <summary>The outgoing-webhook envelope: a hub event plus delivery fields.</summary>
/// <param name="Seq">Position in the hub's event log.</param>
/// <param name="Event">For example <c>member.joined</c>.</param>
/// <param name="HubUrl">The hub that emitted it.</param>
/// <param name="At">Unix seconds.</param>
/// <param name="Payload">The event's own fields.</param>
/// <param name="WebhookId">The webhook this delivery is for.</param>
/// <param name="Truncated">The payload was cut to fit.</param>
public sealed record OutgoingWebhookEvent(
    long Seq,
    string Event,
    string HubUrl,
    long At,
    JsonElement Payload,
    string? WebhookId = null,
    bool? Truncated = null) : HubEvent(Seq, Event, HubUrl, At, Payload);

/// <summary>Options for the webhook verifiers.</summary>
public sealed record VerifyOptions
{
    /// <summary>The clock; <see langword="null"/> for the system clock.</summary>
    public DateTimeOffset? Now { get; init; }

    /// <summary>The largest allowed difference between now and <c>X-Wavvon-Timestamp</c>. Default 300 s.</summary>
    public TimeSpan Tolerance { get; init; } = TimeSpan.FromSeconds(300);
}

/// <summary>
/// Verifies what the hub POSTs to a program: slash and component dispatch (Ed25519 by the hub key)
/// and outgoing webhooks (HMAC-SHA256). Both check <c>X-Wavvon-Signature-V1</c> over
/// <c>"wavvon/webhook-delivery/v1\0" || u64le(timestamp) || body</c> and the timestamp window.
/// Pass the raw request bytes: re-serialised JSON will not verify.
/// </summary>
public static class WebhookVerifier
{
    private static readonly byte[] DeliveryTag = Encoding.UTF8.GetBytes("wavvon/webhook-delivery/v1\0");
    private static readonly byte[] HkdfSalt = Encoding.UTF8.GetBytes("wavvon-webhook-signing");

    /// <summary><c>"wavvon/webhook-delivery/v1\0" || u64le(timestamp) || body</c> (wire-format.md, "Webhook delivery").</summary>
    public static byte[] DeliverySigningBytes(ulong timestamp, ReadOnlySpan<byte> body)
    {
        var bytes = new byte[DeliveryTag.Length + 8 + body.Length];
        DeliveryTag.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(DeliveryTag.Length), timestamp);
        body.CopyTo(bytes.AsSpan(DeliveryTag.Length + 8));
        return bytes;
    }

    /// <summary>
    /// The HMAC key for an outgoing webhook: HKDF-SHA256 over the secret's decoded bytes, salt
    /// <c>wavvon-webhook-signing</c>, empty info, 32 bytes.
    /// </summary>
    /// <param name="secretAsShown">The secret as the admin was shown it: base64url without padding.</param>
    public static byte[] OutgoingSigningKey(string secretAsShown)
    {
        ArgumentNullException.ThrowIfNull(secretAsShown);
        return OutgoingSigningKey(Base64Url.Decode(secretAsShown.Trim()));
    }

    /// <summary>The HMAC key for an outgoing webhook, from the secret's decoded bytes.</summary>
    public static byte[] OutgoingSigningKey(ReadOnlySpan<byte> rawSecret)
    {
        var key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, rawSecret, key, HkdfSalt, []);
        return key;
    }

    /// <summary>
    /// Verifies a slash command or component dispatch and decodes it. The
    /// <c>X-Wavvon-Hub-Pubkey</c> header is only compared against <paramref name="hubPublicKey"/>,
    /// never trusted.
    /// </summary>
    /// <param name="header">Looks up a request header by name, case-insensitively; <see langword="null"/> when absent.</param>
    /// <param name="body">The raw request body.</param>
    /// <param name="hubPublicKey">The hub's key as the program pinned it (<c>GET /info</c> <c>public_key</c>).</param>
    /// <param name="options">Clock and window.</param>
    /// <exception cref="WebhookVerificationException">Answer 401 and do nothing else.</exception>
    public static Dispatch VerifyDispatch(Func<string, string?> header, ReadOnlySpan<byte> body, string hubPublicKey, VerifyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(hubPublicKey);
        var sent = header("X-Wavvon-Hub-Pubkey");
        if (sent is not null && !string.Equals(sent, hubPublicKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new WebhookVerificationException("signed by a different hub");
        }
        var (timestamp, signature) = SignedTimestamp(header, options ?? new VerifyOptions());
        if (!Identity.Verify(hubPublicKey.ToLowerInvariant(), DeliverySigningBytes(timestamp, body), signature))
        {
            throw new WebhookVerificationException("bad signature");
        }
        var raw = ParseObject(body);
        var type = raw.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        try
        {
            return type switch
            {
                "slash_command" => raw.Deserialize<SlashInvocation>(WavvonJson.Options)!,
                "component_interaction" => raw.Deserialize<ComponentInteraction>(WavvonJson.Options)!,
                _ => new UnknownDispatch(type, raw),
            };
        }
        catch (JsonException)
        {
            return new UnknownDispatch(type, raw);
        }
    }

    /// <summary>Verifies an outgoing-webhook delivery and decodes its hub event envelope.</summary>
    /// <param name="header">Looks up a request header by name, case-insensitively; <see langword="null"/> when absent.</param>
    /// <param name="body">The raw request body.</param>
    /// <param name="secretAsShown">What the admin copied when the webhook was created or rotated.</param>
    /// <param name="options">Clock and window.</param>
    /// <exception cref="WebhookVerificationException">Answer 401 and do nothing else.</exception>
    public static OutgoingWebhookEvent VerifyOutgoingWebhook(Func<string, string?> header, ReadOnlySpan<byte> body, string secretAsShown, VerifyOptions? options = null) =>
        VerifyOutgoing(header, body, OutgoingSigningKey(secretAsShown), options);

    /// <summary>Verifies an outgoing-webhook delivery given the secret's decoded bytes.</summary>
    /// <exception cref="WebhookVerificationException">Answer 401 and do nothing else.</exception>
    public static OutgoingWebhookEvent VerifyOutgoingWebhook(Func<string, string?> header, ReadOnlySpan<byte> body, ReadOnlySpan<byte> rawSecret, VerifyOptions? options = null) =>
        VerifyOutgoing(header, body, OutgoingSigningKey(rawSecret), options);

    private static OutgoingWebhookEvent VerifyOutgoing(Func<string, string?> header, ReadOnlySpan<byte> body, byte[] key, VerifyOptions? options)
    {
        ArgumentNullException.ThrowIfNull(header);
        var (timestamp, signature) = SignedTimestamp(header, options ?? new VerifyOptions());
        var expected = HMACSHA256.HashData(key, DeliverySigningBytes(timestamp, body));
        if (!CryptographicOperations.FixedTimeEquals(signature, expected))
        {
            throw new WebhookVerificationException("bad signature");
        }
        try
        {
            return ParseObject(body).Deserialize<OutgoingWebhookEvent>(WavvonJson.Options)!;
        }
        catch (JsonException e)
        {
            throw new WebhookVerificationException($"verified body is not a hub event: {e.Message}");
        }
    }

    private static (ulong Timestamp, byte[] Signature) SignedTimestamp(Func<string, string?> header, VerifyOptions options)
    {
        var signatureHex = header("X-Wavvon-Signature-V1");
        var timestampText = header("X-Wavvon-Timestamp");
        // The body-only X-Wavvon-Signature is deliberately not accepted: on its own a captured
        // request replays forever.
        if (string.IsNullOrEmpty(signatureHex) || string.IsNullOrEmpty(timestampText))
        {
            throw new WebhookVerificationException("missing X-Wavvon-Signature-V1 or X-Wavvon-Timestamp");
        }
        if (!timestampText.All(char.IsAsciiDigit) || !ulong.TryParse(timestampText, out var timestamp))
        {
            throw new WebhookVerificationException("malformed X-Wavvon-Timestamp");
        }
        var now = (options.Now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var skew = Math.Abs((decimal)timestamp - now);
        if (skew > (decimal)options.Tolerance.TotalSeconds)
        {
            throw new WebhookVerificationException("timestamp outside the allowed window");
        }
        var signature = Hex.TryDecode(signatureHex) ?? throw new WebhookVerificationException("malformed signature");
        return (timestamp, signature);
    }

    private static JsonElement ParseObject(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            // Reported below.
        }
        throw new WebhookVerificationException("body is not a JSON object");
    }
}
