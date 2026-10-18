using System.Net;
using Xunit;

namespace Wavvon.Sdk.Tests;

public sealed class RetryTests
{
    private static Task<HttpResponseMessage> Send(Func<CancellationToken, Task<HttpResponseMessage>> attempt, RetryOptions? options = null) =>
        RetryPolicy.SendAsync(attempt, options ?? Fast.Retry, () => 1, TestContext.Current.CancellationToken);

    [Fact]
    public void TreatsOnly429And5xxAsTransient()
    {
        Assert.All([429, 500, 502, 503], s => Assert.True(RetryPolicy.IsTransient((HttpStatusCode)s)));
        Assert.All([200, 400, 401, 403, 404], s => Assert.False(RetryPolicy.IsTransient((HttpStatusCode)s)));
    }

    [Fact]
    public void BacksOffExponentiallyUpToTheCap()
    {
        var options = new RetryOptions { BaseDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromMilliseconds(1000) };
        int[] delays = [.. new[] { 1, 2, 3, 4, 5, 9 }.Select(n => (int)RetryPolicy.BackoffDelay(n, options, () => 1).TotalMilliseconds)];
        Assert.Equal([100, 200, 400, 800, 1000, 1000], delays);
        Assert.Equal(TimeSpan.Zero, RetryPolicy.BackoffDelay(3, options, () => 0));
        Assert.Equal(30_000, RetryPolicy.BackoffDelay(int.MaxValue, RetryOptions.Default, () => 1).TotalMilliseconds);
    }

    [Fact]
    public async Task Retries429UntilAnAnswerArrives()
    {
        var statuses = new Queue<HttpStatusCode>([HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK]);
        var seen = new List<HttpStatusCode?>();
        using var response = await Send(
            _ => Task.FromResult(new HttpResponseMessage(statuses.Dequeue())),
            Fast.Retry with { OnRetry = i => seen.Add(i.Status) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable], seen);
    }

    [Fact]
    public async Task RetriesNetworkErrorsAndTimeouts()
    {
        var n = 0;
        using var response = await Send(async ct =>
        {
            switch (n++)
            {
                case 0:
                    throw new HttpRequestException("connection refused");
                case 1:
                    await Task.Delay(Timeout.Infinite, ct);
                    break;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, Fast.Retry with { Timeout = TimeSpan.FromMilliseconds(20) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, n);
    }

    [Fact]
    public async Task NeverReturnsATransientFailureAsTheAnswer()
    {
        var e = await Assert.ThrowsAsync<HubUnavailableException>(() =>
            Send(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)), Fast.Retry with { MaxAttempts = 3 }));
        Assert.Equal(HttpStatusCode.TooManyRequests, e.LastStatus);
    }

    [Fact]
    public async Task ReturnsADefinitiveRefusalWithoutRetrying()
    {
        var calls = 0;
        using var response = await Send(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CallerCancellationIsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RetryPolicy.SendAsync(ct => Task.FromCanceled<HttpResponseMessage>(ct), Fast.Retry, cts.Token));
    }
}

public sealed class PagerTests
{
    private sealed record Row(string Code);

    private static readonly Row[] Rows = [.. Enumerable.Range(0, 7).Select(i => new Row($"c{i}"))];

    /// <summary>A hub-like keyset endpoint: rows after the cursor, at most <c>limit</c>.</summary>
    private static Task<IReadOnlyList<Row>> Endpoint(string? cursor, int limit, CancellationToken _)
    {
        var from = cursor is null ? 0 : Array.FindIndex(Rows, r => r.Code == cursor) + 1;
        return Task.FromResult<IReadOnlyList<Row>>(Rows.Skip(from).Take(limit).ToArray());
    }

    private static async Task<List<Row>> Collect(Func<string?, int, CancellationToken, Task<IReadOnlyList<Row>>> fetch, int limit)
    {
        var rows = new List<Row>();
        await foreach (var row in Pager.PaginateAsync(fetch, r => r.Code, limit, TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }
        return rows;
    }

    [Fact]
    public async Task WalksEveryPageNotJustTheFirst() => Assert.Equal(Rows, await Collect(Endpoint, 3));

    [Fact]
    public async Task StopsOnAnExactlyFullLastPageAfterOneEmptyFetch()
    {
        var cursors = new List<string?>();
        var rows = await Collect((c, l, ct) =>
        {
            cursors.Add(c);
            return Endpoint(c, l, ct);
        }, 7);
        Assert.Equal(Rows, rows);
        Assert.Equal([null, "c6"], cursors);
    }

    [Fact]
    public async Task ClampsThePageSizeToWhatTheHubServes()
    {
        var limits = new List<int>();
        await Collect((c, l, ct) =>
        {
            limits.Add(l);
            return Endpoint(c, l, ct);
        }, 10_000);
        Assert.Equal(Pager.MaxPageSize, limits[0]);
    }

    [Fact]
    public async Task RefusesToLoopOnAHubThatIgnoresTheCursor() =>
        await Assert.ThrowsAsync<WavvonException>(() =>
            Collect((_, l, _) => Task.FromResult<IReadOnlyList<Row>>(Rows.Take(l).ToArray()), 3));

    [Fact]
    public async Task PropagatesAFailedPageInsteadOfEndingTheList()
    {
        var e = await Assert.ThrowsAsync<HubUnavailableException>(() => Collect((c, l, ct) =>
            c is null ? Endpoint(c, l, ct) : throw new HubUnavailableException("503", HttpStatusCode.ServiceUnavailable, null), 3));
        Assert.Equal("503", e.Message);
    }
}
