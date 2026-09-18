using Microsoft.EntityFrameworkCore;

namespace Modbot.Api.Lists;

/// <summary>
/// One page of a list, with the cursors either side of it.
/// </summary>
/// <param name="Rows">The page itself, in the order the list reads.</param>
/// <param name="Next">Send back to get the following page. Null on the last page.</param>
/// <param name="Previous">Send back to get the page before. Null on the first page.</param>
public sealed record ListPage<T>(IReadOnlyList<T> Rows, string? Next, string? Previous);

/// <summary>
/// Reads one page of a list that is paged by cursor.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of cursor paging that is the same everywhere: ask for one row more than was
/// wanted so "is there another page" needs no second query, put the rows back the right way round
/// when reading backwards, and work out which of the two cursors to hand back. The other half —
/// the ordering, and the comparison that says which rows come after a given row — belongs to each
/// list, because each orders on its own columns. <see cref="Features.Audit.AuditQuery"/> wrote its
/// own before this existed and still does; a shared expression builder covering every ordering
/// would be more code than the comparisons it replaced, and none of it readable.
/// </para>
/// <para>
/// The extra row is never a count. Counting a filtered list on every page turn is a scan, and on
/// the fact log it is a scan of every partition; the audit log has refused to do it since it was
/// written. Lists small enough to count honestly still carry a total, and say so.
/// </para>
/// </remarks>
public static class ListPaging
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    /// <summary>The limit a caller asked for, held inside what the list will serve.</summary>
    public static int Limit(int? asked, int fallback = DefaultLimit, int most = MaxLimit) =>
        Math.Clamp(asked ?? fallback, 1, most);

    /// <param name="ordered">
    /// Already ordered <em>in the direction being read</em> and already narrowed by the cursor.
    /// Reading backwards means the ordering is reversed, so the rows arrive nearest-first and are
    /// turned round here.
    /// </param>
    /// <param name="sort">The name of the ordering, written into the cursors handed back.</param>
    /// <param name="limit">How many rows the page holds.</param>
    /// <param name="at">The cursor this read started from, or null for the first page.</param>
    /// <param name="key">A row's ordering value and its tie-break id.</param>
    public static async Task<ListPage<T>> ReadAsync<T>(
        IQueryable<T> ordered,
        string sort,
        int limit,
        ListCursor? at,
        Func<T, (string? Value, string Id)> key,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(key);

        var rows = await ordered.Take(limit + 1).ToListAsync(ct);

        var more = rows.Count > limit;
        if (more)
            rows.RemoveAt(rows.Count - 1);

        var back = at?.Direction == ListDirection.Back;
        if (back)
            rows.Reverse();

        return new ListPage<T>(rows, Forward(), Backward());

        string? Cursor(ListDirection direction, T row)
        {
            var (value, id) = key(row);
            return new ListCursor(direction, sort, value, id).ToString();
        }

        // Reading forwards, there is a next page when the extra row came back. Reading backwards,
        // the page we came from is still there, so there is always one.
        string? Forward()
        {
            if (rows.Count == 0)
                return null;

            return back || more ? Cursor(ListDirection.Next, rows[^1]) : null;
        }

        // The mirror: reading forwards from a cursor, the page we came from is still there;
        // reading backwards, the extra row says whether anything is left above.
        string? Backward()
        {
            if (rows.Count == 0)
                return null;

            return back ? (more ? Cursor(ListDirection.Back, rows[0]) : null)
                : at is not null ? Cursor(ListDirection.Back, rows[0])
                : null;
        }
    }
}
