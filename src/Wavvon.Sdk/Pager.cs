using System.Runtime.CompilerServices;

namespace Wavvon.Sdk;

/// <summary>Walks the hub's keyset-paged lists to the end.</summary>
public static class Pager
{
    /// <summary>The hub clamps <c>limit</c> to this; asking for more would look like a short page.</summary>
    public const int MaxPageSize = 500;

    /// <summary>
    /// Yields every row of a keyset-paged list. The hub's dialect is a bare array plus <c>limit</c>
    /// and <c>cursor</c>, where the cursor is the key of the previous page's last row (<c>code</c> for
    /// invites, <c>public_key</c> for users, <c>id</c> for most others). A page shorter than
    /// <c>limit</c> is the end; one page is not the list.
    /// </summary>
    /// <param name="fetchPage">
    /// Fetches the page after a cursor (<see langword="null"/> for the first) with a page size. It
    /// names the endpoint's cursor parameter, and must throw on failure: an error must never read as
    /// an empty page. Check the endpoint's capability (<c>list.cursor</c> or <c>list.cursor.lists</c>)
    /// first; a hub without it ignores the cursor, which this detects and reports rather than looping.
    /// </param>
    /// <param name="key">The row's key, which becomes the next cursor.</param>
    /// <param name="limit">Rows per page, clamped to 1..<see cref="MaxPageSize"/>.</param>
    /// <param name="cancellationToken">Stops the walk.</param>
    /// <exception cref="WavvonException">The hub returned the same page twice.</exception>
    public static async IAsyncEnumerable<T> PaginateAsync<T>(
        Func<string?, int, CancellationToken, Task<IReadOnlyList<T>>> fetchPage,
        Func<T, string> key,
        int limit = 100,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetchPage);
        ArgumentNullException.ThrowIfNull(key);
        var size = Math.Clamp(limit, 1, MaxPageSize);
        string? cursor = null;
        string? previousFirst = null;
        while (true)
        {
            var page = await fetchPage(cursor, size, cancellationToken).ConfigureAwait(false);
            if (page.Count > 0 && cursor is not null && key(page[0]) == previousFirst)
            {
                throw new WavvonException("the hub returned the same page twice: it does not honour the cursor on this endpoint");
            }
            foreach (var row in page)
            {
                yield return row;
            }
            if (page.Count < size)
            {
                yield break;
            }
            previousFirst = key(page[0]);
            cursor = key(page[^1]);
        }
    }
}
