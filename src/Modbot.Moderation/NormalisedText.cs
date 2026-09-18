using System.Globalization;
using System.Text;

namespace Modbot.Moderation;

/// <summary>
/// Text made comparable, remembering where each character came from so a match can quote the
/// person's own words (AI moderation design §4.1).
/// </summary>
/// <remarks>
/// <para>
/// Two strengths. <see cref="Lower"/> is lower case, with invisible characters removed, spaces
/// collapsed, and full-width and "fancy text" letters (bold, script, circled, squared) turned into
/// plain ones -- what a regular expression runs against first. <see cref="Folded"/> also removes
/// accents and folds the look-alike digits, symbols and Cyrillic letters people use to get past a
/// word filter.
/// </para>
/// <para>
/// <strong>Tables, not <c>string.Normalize</c>.</strong> Modbot builds with invariant globalization,
/// and in that mode <c>Normalize</c> hands non-ASCII text back unchanged -- "NITRÖ" stayed "nitrö"
/// and a filter believed it was doing something. The tables cover what people actually type to
/// dodge a filter; they are not a full Unicode compatibility mapping.
/// </para>
/// </remarks>
public sealed class NormalisedText
{
    private readonly string _original;
    private readonly int[] _starts;
    private readonly int[] _ends;

    private NormalisedText(string original, string text, int[] starts, int[] ends)
    {
        _original = original;
        Text = text;
        _starts = starts;
        _ends = ends;
    }

    public string Text { get; }

    public static NormalisedText Lower(string text) => Build(text, fold: false);

    public static NormalisedText Folded(string text) => Build(text, fold: true);

    /// <summary>The folded form of a term, for comparing against <see cref="Folded"/> text.</summary>
    public static string FoldTerm(string term) => Build(term, fold: true).Text.Trim();

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

    /// <summary>The original text behind a stretch of <see cref="Text"/>.</summary>
    public string Original(int start, int length)
    {
        if (Text.Length == 0 || length <= 0)
            return string.Empty;

        start = Math.Clamp(start, 0, Text.Length - 1);
        var last = Math.Clamp(start + length - 1, start, Text.Length - 1);

        return _original[_starts[start].._ends[last]].Trim();
    }

    private static NormalisedText Build(string original, bool fold)
    {
        var text = new StringBuilder(original.Length);
        var starts = new List<int>(original.Length);
        var ends = new List<int>(original.Length);

        var index = 0;
        foreach (var rune in original.EnumerateRunes())
        {
            var start = index;
            index += rune.Utf16SequenceLength;

            if (IsInvisible(rune))
                continue;

            var category = Rune.GetUnicodeCategory(rune);

            if (Rune.IsWhiteSpace(rune) || category is UnicodeCategory.SpaceSeparator)
            {
                // Runs of spaces become one, so "k  y  s" and "k y s" read the same.
                if (text.Length > 0 && text[^1] != ' ')
                    Append(' ');
                continue;
            }

            // Accents typed as separate combining marks go with the rest of the accents.
            if (fold && category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
                continue;

            var plain = Plain(rune);
            var lower = char.ToLowerInvariant(plain ?? '\0');

            if (plain is null)
            {
                foreach (var c in Rune.ToLowerInvariant(rune).ToString())
                    Append(fold ? FoldChar(c) : c);
                continue;
            }

            Append(fold ? FoldChar(lower) : lower);

            void Append(char c)
            {
                text.Append(c);
                starts.Add(start);
                ends.Add(index);
            }
        }

        return new NormalisedText(original, text.ToString(), [.. starts], [.. ends]);
    }

    private static bool IsInvisible(Rune rune) => rune.Value is
        0x00AD or 0x034F or 0x180E or 0x200B or 0x200C or 0x200D or 0x200E or 0x200F
        or 0x2060 or 0x2061 or 0x2062 or 0x2063 or 0xFEFF;

    /// <summary>The plain ASCII letter or digit behind a full-width or "fancy text" one, or null.</summary>
    private static char? Plain(Rune rune)
    {
        var v = rune.Value;

        // Full-width ASCII: ＦＲＥＥ.
        if (v is >= 0xFF01 and <= 0xFF5E)
            return (char)(v - 0xFEE0);

        // Mathematical letters: 𝐅𝐑𝐄𝐄, 𝓕𝓡𝓔𝓔, 𝔽ℝ𝔼𝔼 and the rest, 52 to a style.
        if (v is >= 0x1D400 and <= 0x1D6A3)
        {
            var i = (v - 0x1D400) % 52;
            return (char)(i < 26 ? 'A' + i : 'a' + i - 26);
        }

        // Mathematical digits, ten to a style.
        if (v is >= 0x1D7CE and <= 0x1D7FF)
            return (char)('0' + (v - 0x1D7CE) % 10);

        // Circled: Ⓐ ⓐ.
        if (v is >= 0x24B6 and <= 0x24CF)
            return (char)('A' + v - 0x24B6);
        if (v is >= 0x24D0 and <= 0x24E9)
            return (char)('a' + v - 0x24D0);

        // Parenthesised, circled and squared capitals: 🄰 🅐 🅰.
        if (v is >= 0x1F130 and <= 0x1F149)
            return (char)('A' + v - 0x1F130);
        if (v is >= 0x1F150 and <= 0x1F169)
            return (char)('A' + v - 0x1F150);
        if (v is >= 0x1F170 and <= 0x1F189)
            return (char)('A' + v - 0x1F170);

        return null;
    }

    private static char FoldChar(char c) => Folds.TryGetValue(c, out var folded) ? folded : c;

    /// <summary>
    /// Accented Latin letters, look-alike digits and symbols, and the Cyrillic and Greek letters that
    /// look like Latin ones. Every entry is also a false positive somewhere, so the list stays short.
    /// </summary>
    private static readonly Dictionary<char, char> Folds = BuildFolds();

    private static Dictionary<char, char> BuildFolds()
    {
        var folds = new Dictionary<char, char>
        {
            ['0'] = 'o',
            ['1'] = 'i',
            ['3'] = 'e',
            ['4'] = 'a',
            ['5'] = 's',
            ['7'] = 't',
            ['@'] = 'a',
            ['$'] = 's',
        };

        (string From, char To)[] groups =
        [
            ("àáâãäåāăąǎ", 'a'),
            ("çćĉċč", 'c'),
            ("ďđ", 'd'),
            ("èéêëēĕėęě", 'e'),
            ("ĝğġģ", 'g'),
            ("ĥħ", 'h'),
            ("ìíîïĩīĭįıǐ", 'i'),
            ("ĵ", 'j'),
            ("ķ", 'k'),
            ("ĺļľŀł", 'l'),
            ("ñńņňŉ", 'n'),
            ("òóôõöøōŏőǒ", 'o'),
            ("ŕŗř", 'r'),
            ("śŝşšș", 's'),
            ("ţťŧț", 't'),
            ("ùúûüũūŭůűųǔ", 'u'),
            ("ŵ", 'w'),
            ("ýÿŷ", 'y'),
            ("źżž", 'z'),

            // Cyrillic and Greek letters drawn the same as Latin ones.
            ("аα", 'a'),
            ("е", 'e'),
            ("оο", 'o'),
            ("р", 'p'),
            ("с", 'c'),
            ("у", 'y'),
            ("х", 'x'),
            ("і", 'i'),
            ("ѕ", 's'),
            ("ј", 'j'),
        ];

        foreach (var (from, to) in groups)
        {
            foreach (var c in from)
                folds[c] = to;
        }

        return folds;
    }
}
