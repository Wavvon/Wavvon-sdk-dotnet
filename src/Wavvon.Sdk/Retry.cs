using System.Net;

namespace Wavvon.Sdk;

/// <summary>How hard to retry a transient failure before giving up with <see cref="HubUnavailableException"/>.</summary>
public sealed record RetryOptions
{
    /// <summary>Attempts before giving up. <see cref="int.MaxValue"/> keeps trying. Default 8.</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>The first backoff ceiling. Default 500 ms.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The backoff ceiling never grows past this. Default 30 s.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Per-attempt timeout; a timeout counts as transient. Default 15 s.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Called before each wait, so a program can show that it is retrying.</summary>
    public Action<RetryInfo>? OnRetry { get; init; }

    /// <summary>The defaults.</summary>
    public static RetryOptions Default { get; } = new();
}

/// <summary>What failed before a retry.</summary>
/// <param name="Attempt">The attempt that failed, starting at 1.</param>
/// <param name="Delay">How long the SDK waits before the next attempt.</param>
/// <param name="Status">The transient HTTP status, or <see langword="null"/> after a network error or timeout.</param>
/// <param name="Error">The network error or timeout, if that is what failed.</param>
public sealed record RetryInfo(int Attempt, TimeSpan Delay, HttpStatusCode? Status, Exception? Error);

/// <summary>Capped exponential backoff with full jitter, and the rule for what counts as transient.</summary>
public static class RetryPolicy
{
    /// <summary>429 is "not now"; 5xx is the hub or a proxy in front of it failing.</summary>
    public static bool IsTransient(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>Full jitter: uniform in [0, min(MaxDelay, BaseDelay * 2^(attempt-1))].</summary>
    public static TimeSpan BackoffDelay(int attempt, RetryOptions? options = null) =>
        BackoffDelay(attempt, options ?? RetryOptions.Default, Random.Shared.NextDouble);

    internal static TimeSpan BackoffDelay(int attempt, RetryOptions options, Func<double> random)
    {
        var ceiling = Math.Min(options.MaxDelay.TotalMilliseconds, options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(Math.Floor(random() * ceiling));
    }

    /// <summary>
    /// Runs <paramref name="attempt"/> until it yields a non-transient response. The hub sends no
    /// Retry-After, so the wait is capped exponential backoff with jitter. When the budget runs out
    /// this throws <see cref="HubUnavailableException"/> rather than handing back the last 429/5xx:
    /// a transient failure must never be mistaken for an answer.
    /// </summary>
    /// <param name="attempt">
    /// Makes one attempt with a token that also carries the per-attempt timeout. It may send several
    /// requests (login does) and return whichever response decides the outcome.
    /// </param>
    /// <param name="options">The retry budget; <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the whole operation, waits included.</param>
    public static Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> attempt,
        RetryOptions? options = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(attempt, options ?? RetryOptions.Default, Random.Shared.NextDouble, cancellationToken);

    internal static async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> attempt,
        RetryOptions options,
        Func<double> random,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        HttpStatusCode? status = null;
        Exception? error = null;
        for (var n = 1; ; n++)
        {
            status = null;
            error = null;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(options.Timeout);
                try
                {
                    var response = await attempt(timeout.Token).ConfigureAwait(false);
                    if (!IsTransient(response.StatusCode))
                    {
                        return response;
                    }
                    status = response.StatusCode;
                    response.Dispose();
                }
                catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
                {
                    error = new TimeoutException($"no answer within {options.Timeout}", e);
                }
                catch (HttpRequestException e)
                {
                    error = e;
                }
                catch (IOException e)
                {
                    error = e;
                }
            }
            if (n >= options.MaxAttempts)
            {
                break;
            }
            var delay = BackoffDelay(n, options, random);
            options.OnRetry?.Invoke(new RetryInfo(n, delay, status, error));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        throw new HubUnavailableException(
            status is { } s
                ? $"hub kept answering {(int)s} after {options.MaxAttempts} attempts"
                : $"hub unreachable after {options.MaxAttempts} attempts",
            status,
            error);
    }
}
