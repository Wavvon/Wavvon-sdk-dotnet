using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Wavvon.Sdk.AspNetCore;

/// <summary>Minimal-API routes for what a hub POSTs to a program.</summary>
public static class WavvonEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>POST <paramref name="pattern"/></c> to a <see cref="DispatchHandler"/>: the app's
    /// <c>webhook_url</c>. Requests that do not verify get 401; verified slash commands and component
    /// interactions get the handler's answer as JSON.
    /// </summary>
    /// <param name="endpoints">The app or route group.</param>
    /// <param name="pattern">The route, for example <c>/hook</c>.</param>
    /// <param name="options">The pinned hub key and the handlers.</param>
    public static RouteHandlerBuilder MapWavvonDispatch(this IEndpointRouteBuilder endpoints, string pattern, DispatchHandlerOptions options)
    {
        var handler = new DispatchHandler(options);
        return endpoints.MapPost(pattern, async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body, cancellationToken).ConfigureAwait(false);
            var result = await handler.HandleAsync(
                name => request.Headers.TryGetValue(name, out var value) ? value.ToString() : null,
                body.GetBuffer().AsMemory(0, (int)body.Length),
                cancellationToken).ConfigureAwait(false);
            return Results.Content(result.Body, result.ContentType, statusCode: result.StatusCode);
        });
    }
}
