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
/// <strong>Only what Discord reads as formatting is escaped.</strong> A backslash in front of
/// anything else is a backslash a moderator sees wherever the text is not drawn as markdown --
/// a notification, a search result, a name they copied out to look up -- so <c>@</c> and
/// <c>:</c> are left alone. Neither is markdown: a name spelled like a mention is text because
/// mentions are off on every message the bot sends (Discord embeds design §4), and a colon is a
/// colon.
/// </para>
/// <para>
/// <strong>A heading, a quote and a list only start at the start of a line</strong>, so
/// <c>#</c>, <c>&gt;</c> and <c>-</c> are escaped there and nowhere else. Escaping every one of
/// them would put a backslash through the middle of every hyphenated name in the group.
/// </para>
/// <para>
/// There are two escapes rather than one because a name and a paragraph sit in different places.
/// A name is a short label that lands inside <c>[…](…)</c>, so its brackets <em>and</em> its
/// parentheses go; free text -- a ban reason, an event description -- is never a link's label, and
/// with <c>[</c> and <c>]</c> escaped a lone parenthesis cannot begin one. A name also loses its
/// control characters, which leaves it one line long and gives it exactly one place a line can
/// start; free text keeps its line breaks and has one after each of them.
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
    /// Anything Discord draws as formatting wherever it appears: bold, italics, strikethrough,
    /// code, spoilers, a link's brackets, and the <c>&lt;</c> that starts a mention, a channel, a
    /// timestamp or a custom emoji.
    /// </summary>
    private static bool IsFormatting(char c) =>
        c is '\\' or '*' or '_' or '~' or '`' or '|' or '<' or '[' or ']';

    /// <summary>
    /// A heading, a quote and a list: the three that mean something at the start of a line and
    /// nothing anywhere else. <c>E-Ray</c> is a name, not a bullet.
    /// </summary>
    private static bool StartsALine(char c) => c is '#' or '>' or '-';

    /// <summary>
    /// Makes a display name, a world name or any other short label inert: control characters become
    /// spaces, so the name is one line and cannot start a heading, a quote or a list of its own,
    /// and every character Discord reads as formatting is escaped -- the brackets and the
    /// parentheses included, because a name lands inside <c>[…](…)</c> and a single <c>]</c> there
    /// would end the link early and put the address on screen.
    /// </summary>
    public static string EscapeName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var escaped = new StringBuilder(text.Length + 8);

        // The name may be placed at the start of a line -- one of twenty in a list, a bullet in the
        // giveaway rules -- so its own first character is treated as one, leading spaces and all.
        var lineStart = true;

        foreach (var c in text)
        {
            var here = char.IsControl(c) ? ' ' : c;

            if (IsFormatting(here) || here is '(' or ')' || (lineStart && StartsALine(here)))
                escaped.Append('\\');

            escaped.Append(here);

            if (!char.IsWhiteSpace(here))
                lineStart = false;
        }

        return escaped.ToString().Trim();
    }

    /// <summary>
    /// Backslash-escapes the markdown in free text -- a reason, a description -- so it renders as
    /// typed.
    /// </summary>
    /// <remarks>
    /// The same rule as <see cref="EscapeName"/> with two differences, both because free text is a
    /// paragraph rather than a label. Its line breaks are kept, so every line has a start of its
    /// own to guard; and it is never a link's label, so its parentheses are left alone -- with
    /// <c>[</c> and <c>]</c> escaped, a lone <c>(</c> cannot begin a link.
    /// </remarks>
    public static string EscapeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var escaped = new StringBuilder(text.Length + 8);
        var lineStart = true;

        foreach (var c in text)
        {
            if (IsFormatting(c) || (lineStart && StartsALine(c)))
                escaped.Append('\\');

            escaped.Append(c);

            lineStart = c is '\n' or '\r' || (lineStart && char.IsWhiteSpace(c));
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
