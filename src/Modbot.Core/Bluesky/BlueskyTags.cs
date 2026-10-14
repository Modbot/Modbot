using System.Globalization;
using System.Text;

namespace Modbot.Core.Bluesky;

/// <summary>
/// The tags at the end of an event's Bluesky post: made from a category's name, or typed by hand, and
/// fitted into the 300 characters a post holds.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Letters and digits only.</strong> A tag in the post's text is found by <see cref="BlueskyText.Facets"/>,
/// which ends it at a space and drops punctuation from its end, so a tag with either would be marked
/// as something shorter than was meant. A tag made of digits alone, or longer than
/// <see cref="BlueskyText.TagLimit"/> graphemes, is not a tag to Bluesky at all, so none is made from a name.
/// </para>
/// <para>
/// <strong>The text is never cut for a tag.</strong> <see cref="Append"/> adds tags to a text that already
/// fits and drops them from the end when they do not.
/// </para>
/// </remarks>
public static class BlueskyTags
{
    /// <summary>The most tags typed into "Tags on every post".</summary>
    public const int MostEveryPost = 5;

    /// <summary>The words for a tag longer than Bluesky takes.</summary>
    public const string TooLong = "A Bluesky tag is longer than 64 characters.";

    /// <summary>
    /// The tag a name gives: its words joined in CamelCase, each word's first letter capital and the
    /// rest as typed ("Dance night" → <c>DanceNight</c>). Empty when the name has no letters or digits
    /// or gives only digits.
    /// </summary>
    public static string FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var tag = new StringBuilder();
        var newWord = true;

        foreach (var rune in name.EnumerateRunes())
        {
            if (!IsTagRune(rune))
            {
                newWord = true;
                continue;
            }

            tag.Append(newWord ? Rune.ToUpperInvariant(rune).ToString() : rune.ToString());
            newWord = false;
        }

        var made = Usable(tag.ToString());
        return IsTooLong(made) ? string.Empty : made;
    }

    /// <summary>
    /// A tag as typed, made fit to use: no <c>#</c>, no spaces or punctuation, the letters as they
    /// were typed. Empty for none. One longer than Bluesky takes is returned as it is, for the caller to refuse (<see cref="IsTooLong"/>).
    /// </summary>
    public static string Clean(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return string.Empty;

        var tag = new StringBuilder();

        foreach (var rune in typed.EnumerateRunes())
        {
            if (IsTagRune(rune))
                tag.Append(rune.ToString());
        }

        return Usable(tag.ToString());
    }

    /// <summary>
    /// A category's tag: the one it was given (empty for none), else the one its name makes.
    /// </summary>
    /// <param name="own">The tag set on the category: null for the name's, empty for none.</param>
    public static string ForCategory(string name, string? own) =>
        own is null ? FromName(name) : Clean(own);

    /// <summary>Whether a cleaned tag is longer than Bluesky takes. <see cref="Clean"/> leaves such a tag as it is.</summary>
    public static bool IsTooLong(string tag) => BlueskyText.Graphemes(tag) > BlueskyText.TagLimit;

    /// <summary>
    /// The tags typed into "Tags on every post": split at spaces and commas, each cleaned, none twice
    /// whatever the letter case, at most <see cref="MostEveryPost"/>. Anything beyond is left out.
    /// </summary>
    public static IReadOnlyList<string> ParseList(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return [];

        var tags = new List<string>();

        foreach (var word in typed.Split([' ', ',', '\n', '\r', '\t', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = Clean(word);

            if (tag.Length > 0 && !IsTooLong(tag) && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                tags.Add(tag);

            if (tags.Count == MostEveryPost)
                break;
        }

        return tags;
    }

    /// <summary>The tags as the setting keeps them: <c>#VRChat #Karaoke</c>.</summary>
    public static string Write(IEnumerable<string> tags) =>
        string.Join(' ', tags.Select(t => "#" + t));

    /// <summary>
    /// Adds <paramref name="tags"/> on a last line of <paramref name="text"/>, as many from the start as
    /// fit Bluesky's limits, a tag never twice. The text is never cut: a tag that does not fit, and
    /// every one after it, is left out.
    /// </summary>
    public static string Append(string text, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tags);

        var result = text;
        var added = new List<string>();

        foreach (var tag in tags)
        {
            if (tag.Length == 0 || added.Contains(tag, StringComparer.OrdinalIgnoreCase))
                continue;

            var next = result + (added.Count == 0 ? (text.Length == 0 ? string.Empty : "\n") : " ") + "#" + tag;

            if (!BlueskyText.Fits(next))
                break;

            result = next;
            added.Add(tag);
        }

        return result;
    }

    private static string Usable(string tag) =>
        tag.Length == 0 || tag.All(char.IsDigit) ? string.Empty : tag;

    private static bool IsTagRune(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter
        or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter
        or UnicodeCategory.OtherLetter
        or UnicodeCategory.DecimalDigitNumber
        or UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark;
}
