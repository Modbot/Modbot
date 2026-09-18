using System.Text;

namespace Modbot.Discord.Cards;

/// <summary>
/// The two things every card does to text it did not write: make it inert, and cut it to
/// Discord's limits.
/// </summary>
/// <remarks>
/// <para>
/// One copy, because the cards used to each have their own and they had drifted apart: the
/// moderation log's escape left <c>[</c> and <c>]</c> alone, which was harmless while a name was
/// plain text and is not harmless now that a name sits inside <c>[…](…)</c>. A single <c>]</c> in
/// a display name would have ended the link early and put the address on screen.
/// </para>
/// <para>
/// There are two escapes rather than one because they guard different things. A name is a short
/// label that lands inside a link, a field value or a title, and anything a person could hide
/// formatting in is escaped. Free text -- a ban reason, an event description -- is a paragraph
/// somebody wrote to be read, and escaping <c>:</c> or <c>-</c> there would leave backslashes on
/// screen in a sentence.
/// </para>
/// </remarks>
public static class CardText
{
    /// <summary>The longest a name is allowed to be on a card before it is cut.</summary>
    public const int MaxNameLength = 100;

    /// <summary>
    /// A name for a slot Discord prints exactly as given: an embed's title, its author line, its
    /// footer.
    /// </summary>
    /// <remarks>
    /// These slots are not markdown, so escaping one would leave the backslashes on screen --
    /// a person called <c>*nova*</c> would read as <c>\*nova\*</c> in the title of their own card.
    /// Control characters still go, because a line break in a title is a title that shoves the
    /// card about.
    /// </remarks>
    public static string Plain(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);

        var plain = new StringBuilder(text.Length);

        foreach (var c in text)
            plain.Append(char.IsControl(c) ? ' ' : c);

        var trimmed = plain.ToString().Trim();

        return trimmed.Length <= max ? trimmed : string.Concat(trimmed.AsSpan(0, max - 1), "…");
    }

    /// <summary>
    /// Makes a display name, a world name or any other short label inert: every character Discord
    /// reads as formatting is escaped, and control characters become spaces so one name cannot
    /// start a heading, a quote or a list on a line of its own.
    /// </summary>
    public static string EscapeName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var escaped = new StringBuilder(text.Length + 8);

        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                escaped.Append(' ');
                continue;
            }

            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '<' or '#' or '-' or '[' or ']' or '(' or ')' or ':' or '@')
                escaped.Append('\\');

            escaped.Append(c);
        }

        return escaped.ToString().Trim();
    }

    /// <summary>
    /// Backslash-escapes the markdown in free text -- a reason, a description -- so it renders as
    /// typed.
    /// </summary>
    /// <remarks>
    /// Lighter than <see cref="EscapeName"/> on purpose: this is a sentence a moderator reads, and
    /// escaping every colon and hyphen in one would put backslashes through the middle of it. The
    /// brackets are escaped all the same, because free text goes into a description that may sit
    /// beside a link and an unbalanced bracket there is somebody else's link.
    /// </remarks>
    public static string EscapeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var escaped = new StringBuilder(text.Length + 8);

        foreach (var c in text)
        {
            if (c is '*' or '_' or '~' or '`' or '|' or '>' or '#' or '@' or '[' or ']' or '\\')
                escaped.Append('\\');

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Cuts to a Discord limit with an ellipsis, never leaving a backslash as the last character:
    /// a cut that landed mid-escape would turn the ellipsis into the thing being escaped.
    /// </summary>
    public static string Fit(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length <= max)
            return text;

        var cut = max - 1;
        if (cut > 0 && text[cut - 1] == '\\')
            cut--;

        return string.Concat(text.AsSpan(0, cut), "…");
    }
}
