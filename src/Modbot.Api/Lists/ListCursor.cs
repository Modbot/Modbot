using System.Globalization;

namespace Modbot.Api.Lists;

/// <summary>Which way a cursor reads: on to the next page, or back to the one before.</summary>
public enum ListDirection
{
    /// <summary>The rows after the cursor's row, in the list's own order.</summary>
    Next = 1,

    /// <summary>The rows before it.</summary>
    Back = 2,
}

/// <summary>
/// Where a reader is in a list, as a piece of text they hand back to ask for the next or the
/// previous page.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a cursor and not a page number.</strong> These lists are written to while somebody
/// is reading them — a member sweep finishes, a ban lands, a case file is written. Skipping a
/// count of rows means page two is measured from wherever the list starts <em>now</em>, so a row
/// inserted above the boundary pushes one row from page one onto page two (it is read twice) and
/// a row removed pulls one off page two (it is never read at all). Naming the last row instead,
/// and asking for what comes after it, cannot do either.
/// </para>
/// <para>
/// This is the rule <see cref="Features.Audit.AuditQuery"/> already follows, generalised so every
/// list spells it the same way. Its two halves are the same two: the value the list is ordered on,
/// and a tie-break that is unique per row. Both are required. A cursor on the ordering value alone
/// silently drops every row sharing the boundary value — and these lists tie constantly, because a
/// sweep stamps a whole batch of rows with one moment.
/// </para>
/// <para>
/// <strong>The text is the server's to write and the caller's to echo.</strong> It is readable on
/// purpose — a moderator can see in the address bar where a link points, and so can whoever is
/// reading a script later — but nothing promises its shape will not change. A cursor that cannot
/// be read, or that was written for a different ordering, is treated as no cursor at all and the
/// first page is served: a stale bookmark should show the list, not an error page.
/// </para>
/// <para>
/// The sort is carried because the same list can be ordered several ways. A cursor holding a
/// display name, read back while the list is ordered by join date, would compare a name against a
/// date and hand back an arbitrary slice of the list. Carrying the name of the ordering turns that
/// into a first page instead of a wrong answer.
/// </para>
/// </remarks>
/// <param name="Direction">Whether the page wanted is after this row or before it.</param>
/// <param name="Sort">The ordering this was written under.</param>
/// <param name="Value">
/// The ordering value of the row the page starts from, written out as text. Null when that row has
/// no value to order on — no join date, no name yet — which is its own place in the order.
/// </param>
/// <param name="Id">The row's tie-break: its id, unique within the list.</param>
public sealed record ListCursor(ListDirection Direction, string Sort, string? Value, string Id)
{
    /// <summary>
    /// Between the four parts. Percent-encoding escapes it, so no part can contain one.
    /// </summary>
    /// <remarks>
    /// <c>Uri.EscapeDataString</c> leaves <c>-</c>, <c>.</c>, <c>_</c> and <c>~</c> alone, so none
    /// of those would do: a display name or a legacy VRChat id may hold any of them (spec 3.1.1).
    /// </remarks>
    private const char Between = '!';

    /// <summary>Marks a row that has no ordering value, told apart from one whose value is empty.</summary>
    private const string Missing = "-";

    /// <summary>Marks a value that is present; what follows it is the value itself.</summary>
    private const char Present = '=';

    public override string ToString()
    {
        var word = Direction == ListDirection.Back ? "back" : "next";
        var value = Value is null ? Missing : Present + Uri.EscapeDataString(Value);

        return string.Join(Between, word, Uri.EscapeDataString(Sort), value, Uri.EscapeDataString(Id));
    }

    /// <summary>
    /// The cursor this text carries, or null when there is none to read.
    /// </summary>
    /// <param name="text">What the caller sent. Null, blank and nonsense all answer null.</param>
    /// <param name="sort">
    /// The ordering the list is about to use. A cursor written under a different one answers null,
    /// so the reader gets the first page of the ordering they asked for.
    /// </param>
    public static ListCursor? Read(string? text, string sort)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parts = text.Split(Between);
        if (parts.Length != 4)
            return null;

        var direction = parts[0] switch
        {
            "next" => ListDirection.Next,
            "back" => ListDirection.Back,
            _ => (ListDirection?)null,
        };

        if (direction is null)
            return null;

        string written, value, id;

        try
        {
            written = Uri.UnescapeDataString(parts[1]);
            value = Uri.UnescapeDataString(parts[2]);
            id = Uri.UnescapeDataString(parts[3]);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (!string.Equals(written, sort, StringComparison.Ordinal) || id.Length == 0)
            return null;

        if (value == Missing)
            return new ListCursor(direction.Value, sort, null, id);

        return value.Length > 0 && value[0] == Present
            ? new ListCursor(direction.Value, sort, value[1..], id)
            : null;
    }

    /// <summary>A moment as a cursor value: the round-trip form, so it reads back to the same instant.</summary>
    public static string? Text(DateTimeOffset? at) =>
        at?.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A cursor value back as a moment, or null when it is missing or will not parse.</summary>
    public static DateTimeOffset? Time(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var at)
            ? at
            : null;
}
