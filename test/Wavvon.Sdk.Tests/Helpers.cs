using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Wavvon.Sdk.Tests;

/// <summary>An in-process hub: each request goes to the delegate.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return respond(request, body);
    }
}

internal static class Http
{
    public static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value) };

    public static HttpResponseMessage Text(HttpStatusCode status, string text = "") =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    public static Func<string, string?> Headers(IReadOnlyDictionary<string, string> headers)
    {
        var map = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        return name => map.GetValueOrDefault(name);
    }
}

internal static class Fast
{
    public static RetryOptions Retry { get; } = new() { BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(1) };
}
