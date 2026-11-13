using System.Text.Json;

namespace Wavvon.Sdk;

/// <summary>Configures a <see cref="DispatchHandler"/>.</summary>
public sealed record DispatchHandlerOptions
{
    /// <summary>The hub's key as the program pinned it (<c>GET /info</c> <c>public_key</c>).</summary>
    public required string HubPublicKey { get; init; }

    /// <summary>Answers a slash command. Without it, the hub gets <c>{}</c>.</summary>
    public Func<SlashInvocation, CancellationToken, Task<AppResponse>>? OnSlashCommand { get; init; }

    /// <summary>Answers a component interaction. Without it, the hub gets <c>{}</c>.</summary>
    public Func<ComponentInteraction, CancellationToken, Task<ComponentResponse>>? OnComponent { get; init; }

    /// <summary>A verified dispatch of a type this SDK does not know; the result is serialised as the answer. Default: <c>{}</c>.</summary>
    public Func<UnknownDispatch, CancellationToken, Task<object?>>? OnUnknown { get; init; }

    /// <summary>Clock and window for the signature check.</summary>
    public VerifyOptions Verify { get; init; } = new();
}

/// <summary>What to send back to the hub.</summary>
/// <param name="StatusCode">200 for a verified dispatch, 401 for anything that did not verify.</param>
/// <param name="ContentType">The body's media type.</param>
/// <param name="Body">The body.</param>
public sealed record DispatchResult(int StatusCode, string ContentType, string Body);

/// <summary>
/// The logic behind an app's <c>webhook_url</c>, independent of any web framework: verify, decode,
/// call the handler, serialise its answer. Anything that does not verify gets 401. The hub waits 5 s
/// for the answer. <c>Wavvon.Sdk.AspNetCore</c> maps it onto a minimal-API route.
/// </summary>
/// <param name="options">The hub key and the handlers.</param>
public sealed class DispatchHandler(DispatchHandlerOptions options)
{
    private readonly DispatchHandlerOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Handles one POST to the webhook URL.</summary>
    /// <param name="header">Looks up a request header by name, case-insensitively; <see langword="null"/> when absent.</param>
    /// <param name="body">The raw request body.</param>
    /// <param name="cancellationToken">Cancels the handler.</param>
    public async Task<DispatchResult> HandleAsync(Func<string, string?> header, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        Dispatch dispatch;
        try
        {
            dispatch = WebhookVerifier.VerifyDispatch(header, body.Span, _options.HubPublicKey, _options.Verify);
        }
        catch (WebhookVerificationException e)
        {
            return new DispatchResult(401, "text/plain; charset=utf-8", e.Message);
        }
        object? answer = dispatch switch
        {
            SlashInvocation s when _options.OnSlashCommand is { } f => await f(s, cancellationToken).ConfigureAwait(false),
            ComponentInteraction c when _options.OnComponent is { } f => await f(c, cancellationToken).ConfigureAwait(false),
            UnknownDispatch u when _options.OnUnknown is { } f => await f(u, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
        var json = answer is null ? "{}" : JsonSerializer.Serialize(answer, answer.GetType(), WavvonJson.Options);
        return new DispatchResult(200, "application/json", json);
    }
}
