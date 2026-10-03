using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Modbot.Core.Bluesky;

/// <summary>One link or tag Modbot marks in a post's text, as a range of UTF-8 bytes.</summary>
/// <param name="Kind"><see cref="BlueskyText.LinkKind"/> or <see cref="BlueskyText.TagKind"/>.</param>
/// <param name="Start">The first byte, counted in UTF-8 from the start of the text.</param>
/// <param name="End">One past the last byte.</param>
/// <param name="Value">The link's address, or the tag without its <c>#</c>.</param>
/// <param name="Shown">The characters the range covers, as shown in the text.</param>
public sealed record BlueskyFacet(string Kind, int Start, int End, string Value, string Shown);

/// <summary>One stretch of a post's text for the preview: plain words, a link or a tag.</summary>
/// <param name="Kind"><c>text</c>, <c>link</c> or <c>tag</c>.</param>
public sealed record BlueskyTextPart(string Kind, string Text);

/// <summary>
/// A Bluesky post's text, how it is counted, and the links and tags marked in it (Bluesky design
/// §3.5, facts 12 and 13). One place, used by the composer's check, the preview and the sender, so
/// what the preview draws and counts is what goes out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Counted the way Bluesky counts.</strong> A post holds 300 graphemes (what a reader sees as
/// one character: an emoji with its skin tone, a letter with its accent) and 3000 bytes of UTF-8.
/// Graphemes are counted with <see cref="StringInfo"/>, which follows Unicode's rules; the browser
/// counts with <c>Intl.Segmenter</c> only to show the counter while typing, and the server decides.
/// </para>
/// <para>
/// <strong>Links and tags, never mentions.</strong> Bluesky does not find links itself: the client
/// marks them, by UTF-8 byte ranges. Modbot marks <c>https://</c> and <c>http://</c> links and
/// <c>#tags</c>. An <c>@handle</c> stays plain text, because a mention notifies the person, and
/// Bluesky's developer guidelines forbid automated actions that notify people (facts 13, 24).
/// </para>
/// </remarks>
public static partial class BlueskyText
{
    /// <summary>The most graphemes a post's text holds.</summary>
    public const int GraphemeLimit = 300;

    /// <summary>The most UTF-8 bytes a post's text holds.</summary>
    public const int ByteLimit = 3000;

    /// <summary>The longest tag Bluesky takes, in graphemes, without its <c>#</c>.</summary>
    public const int TagLimit = 64;

    /// <summary>The most a card picture may be: Bluesky's limit for a link card's thumb.</summary>
    public const int CardPictureMaxBytes = 1_000_000;

    /// <summary>The longest card description sent.</summary>
    public const int CardDescriptionLimit = 300;

    public const string LinkKind = "link";
    public const string TagKind = "tag";

    /// <summary>The words for a text over either limit (Bluesky design §3.5).</summary>
    public const string TooLong = "The Bluesky text is longer than 300 characters.";

    /// <summary>How many graphemes the text holds.</summary>
    public static int Graphemes(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : new StringInfo(text).LengthInTextElements;

    /// <summary>How many UTF-8 bytes the text holds.</summary>
    public static int Bytes(string? text) => string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);

    /// <summary>Whether the text fits both limits.</summary>
    public static bool Fits(string? text) => Graphemes(text) <= GraphemeLimit && Bytes(text) <= ByteLimit;

    /// <summary>
    /// Whether a value is shaped like an app password: four groups of four letters or digits, joined
    /// by dashes, the way Bluesky shows one (fact 28). The account's main password is refused, so it is
    /// never kept.
    /// </summary>
    public static bool IsAppPassword(string? value) =>
        value is not null && AppPasswordShape().IsMatch(value.Trim());

    /// <summary>The post's address on bsky.app, known before it is sent, because Modbot picked its key.</summary>
    public static string PostLink(string did, string recordKey) =>
        $"https://bsky.app/profile/{did}/post/{recordKey}";

    /// <summary>The post's <c>at://</c> address.</summary>
    public static string PostUri(string did, string recordKey) =>
        $"at://{did}/app.bsky.feed.post/{recordKey}";

    /// <summary>
    /// The links and tags in <paramref name="text"/>, in order, with their UTF-8 byte ranges. Links
    /// lose trailing punctuation a sentence put after them; tags lose theirs, and a tag of only digits
    /// or longer than <see cref="TagLimit"/> graphemes is left as plain text.
    /// </summary>
    public static IReadOnlyList<BlueskyFacet> Facets(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var found = new List<(int Index, int Length, string Kind, string Value)>();

        foreach (Match match in LinkPattern().Matches(text))
        {
            var link = match.Groups["link"];
            var value = TrimLink(link.Value);
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && uri.Host.Length > 0)
                found.Add((link.Index, value.Length, LinkKind, value));
        }

        foreach (Match match in TagPattern().Matches(text))
        {
            var tag = match.Groups["tag"];
            var value = TrailingPunctuation().Replace(tag.Value, string.Empty);

            if (value.Length == 0 || value.All(char.IsDigit) || Graphemes(value) > TagLimit)
                continue;

            // The range covers the '#' as well as the word.
            var start = tag.Index - 1;
            if (found.Any(f => start < f.Index + f.Length && f.Index < start + value.Length + 1))
                continue;

            found.Add((start, value.Length + 1, TagKind, value));
        }

        return [.. found
            .OrderBy(f => f.Index)
            .Select(f =>
            {
                var start = Encoding.UTF8.GetByteCount(text.AsSpan(0, f.Index));
                var end = start + Encoding.UTF8.GetByteCount(text.AsSpan(f.Index, f.Length));
                return new BlueskyFacet(f.Kind, start, end, f.Value, text.Substring(f.Index, f.Length));
            })];
    }

    /// <summary>The facets as the post record carries them (<c>app.bsky.richtext.facet</c>).</summary>
    public static JsonArray FacetsJson(IReadOnlyList<BlueskyFacet> facets)
    {
        ArgumentNullException.ThrowIfNull(facets);

        return new JsonArray([.. facets.Select(f => (JsonNode)new JsonObject
        {
            ["index"] = new JsonObject { ["byteStart"] = f.Start, ["byteEnd"] = f.End },
            ["features"] = new JsonArray(f.Kind == LinkKind
                ? new JsonObject { ["$type"] = "app.bsky.richtext.facet#link", ["uri"] = f.Value }
                : new JsonObject { ["$type"] = "app.bsky.richtext.facet#tag", ["tag"] = f.Value }),
        })]);
    }

    /// <summary>The text cut into plain words, links and tags, for the preview to colour.</summary>
    public static IReadOnlyList<BlueskyTextPart> Parts(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var bytes = Encoding.UTF8.GetBytes(text);
        var parts = new List<BlueskyTextPart>();
        var at = 0;

        foreach (var facet in Facets(text))
        {
            if (facet.Start > at)
                parts.Add(new BlueskyTextPart("text", Encoding.UTF8.GetString(bytes, at, facet.Start - at)));

            parts.Add(new BlueskyTextPart(facet.Kind, Encoding.UTF8.GetString(bytes, facet.Start, facet.End - facet.Start)));
            at = facet.End;
        }

        if (at < bytes.Length)
            parts.Add(new BlueskyTextPart("text", Encoding.UTF8.GetString(bytes, at, bytes.Length - at)));

        return parts;
    }

    /// <summary>The first link in the text, which the card is made for, or null when there is none.</summary>
    public static string? CardLink(string? text) =>
        Facets(text).FirstOrDefault(f => f.Kind == LinkKind)?.Value;

    /// <summary>The host a card shows under its title, without <c>www.</c>.</summary>
    public static string CardHost(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri)
            ? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host)
            : link;

    /// <summary>A card description no longer than <see cref="CardDescriptionLimit"/> characters, cut at a whole grapheme.</summary>
    public static string CardDescription(string? text)
    {
        var tidy = (text ?? string.Empty).Trim();
        if (tidy.Length <= CardDescriptionLimit)
            return tidy;

        var info = new StringInfo(tidy);
        var cut = info.LengthInTextElements;
        while (cut > 0 && info.SubstringByTextElements(0, cut).Length > CardDescriptionLimit - 1)
            cut--;

        return info.SubstringByTextElements(0, cut).TrimEnd() + "…";
    }

    private static string TrimLink(string link)
    {
        var value = TrailingLinkPunctuation().Replace(link, string.Empty);

        // A closing bracket belongs to the link only when the link opened one.
        while (value.EndsWith(')') && value.Count(c => c == '(') < value.Count(c => c == ')'))
            value = TrailingLinkPunctuation().Replace(value[..^1], string.Empty);

        return value;
    }

    [GeneratedRegex(@"^[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AppPasswordShape();

    [GeneratedRegex(@"(?:^|(?<=[\s(]))(?<link>https?://[^\s]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"[.,;:!?""'\]}>]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingLinkPunctuation();

    [GeneratedRegex(@"(?:^|(?<=\s))[#＃](?<tag>[^\s­⁠ ​‌‍⃢#＃]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\p{P}+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingPunctuation();
}
