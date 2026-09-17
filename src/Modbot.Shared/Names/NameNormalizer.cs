using System.Buffers;
using System.Globalization;
using System.Text;

namespace Modbot.Shared.Names;

/// <summary>
/// Turns a display name or username, however it is dressed up, into two plain forms.
/// </summary>
/// <remarks>
/// <para>
/// People write their names in "fonts" -- mathematical bold and script letters, small capitals,
/// full-width letters, letters borrowed from Greek, Cyrillic, Thai or Armenian because they look
/// like a Latin one -- and decorate them with combining marks, brackets, hearts and invisible
/// characters. The original is always what is stored and shown; these forms are extra.
/// </para>
/// <list type="bullet">
/// <item><description><see cref="Readable"/>: the name in plain letters, keeping its casing. For lists and
/// for reading aloud. <c>𝕬𝖑𝖊𝖝</c> is <c>Alex</c>, <c>ꜱɪᴇɴɴᴀ</c> is <c>sienna</c>, <c>Addеrаll</c> is <c>Adderall</c>.</description></item>
/// <item><description><see cref="Searchable"/>: the readable form lower-cased with accents and every mark removed,
/// for matching. The same rules run over a search term, so a moderator typing <c>alex</c> finds <c>𝕬𝖑𝖊𝖝</c>.</description></item>
/// </list>
/// <para>
/// The rules, in the order they run over each word (words are split at spaces):
/// </para>
/// <list type="number">
/// <item><description>Invisible and control characters go. Any kind of space becomes one space.</description></item>
/// <item><description>Compatibility letters decompose to their plain letters (<see cref="NameDecompositionTable"/>):
/// mathematical, full-width, circled, superscript, ligatures, and accented Latin letters into letter plus marks.
/// The readable form keeps a non-Latin letter composed with its own marks (й, ά).</description></item>
/// <item><description>Look-alikes fold to the letter they stand for (<see cref="NameFoldingTable"/>): small
/// capitals, hooked and stroked letters, the font alphabets, and Unicode's confusables. A letter from
/// another script folds only when the word is not written in that script: <c>Blondiе</c> folds its Cyrillic
/// е, <c>Кира</c> stays Cyrillic.</description></item>
/// <item><description>Marks: the readable form drops decorative marks (zalgo, accents on Latin letters) and keeps
/// a script's own marks on its own letters; the searchable form drops them all.</description></item>
/// <item><description>A digit of another script is a plain digit inside a word written in that script, and a
/// flourish (୨୧) anywhere else.</description></item>
/// <item><description>Symbols and punctuation outside ASCII go, unless <c>keepEmoji</c> keeps the emoji.
/// Letters of ornamental scripts (cuneiform, hieroglyphs, runes) go with them, and so does a lone letter of
/// another script beside Latin letters: the ツ in <c>Asherツ</c>, the 亞 either side of <c>亞Rebel亞</c>.</description></item>
/// <item><description>Runs of spaces become one space. The searchable form is lower-cased as well.</description></item>
/// </list>
/// <para>
/// Modbot builds with invariant globalization, where <c>string.Normalize</c> hands non-ASCII text
/// back unchanged, so the decompositions are a table of Modbot's own. Deterministic, never
/// throws, one pooled buffer and one string per call.
/// </para>
/// </remarks>
public static class NameNormalizer
{
    /// <summary>
    /// Goes up whenever the rules or tables change what they produce. Stored searchable forms
    /// made by an older version are made again (see the name catch-up).
    /// </summary>
    public const int Version = 1;

    /// <summary>The name in plain letters, with its casing. Empty for null, blank, or nothing but decoration.</summary>
    /// <param name="keepEmoji">Keep emoji and the symbols that read as emoji; other decoration still goes.</param>
    public static string Readable(string? name, bool keepEmoji = false)
        => Normalize(name, searchable: false, keepEmoji);

    /// <summary>The name as search compares it: plain, lower-case, without marks. Empty for nothing to match.</summary>
    public static string Searchable(string? name)
        => Normalize(name, searchable: true, keepEmoji: false);

    private static string Normalize(string? name, bool searchable, bool keepEmoji)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var writer = new Writer(name.Length);
        try
        {
            var i = 0;
            while (i < name.Length)
            {
                // Skip a run of breaks; one space stands for it.
                while (i < name.Length && IsBreak(name, i, out var breakLength))
                {
                    writer.Space();
                    i += breakLength;
                }

                var start = i;
                while (i < name.Length && !IsBreak(name, i, out _))
                    i += RuneLength(name, i);

                if (i > start)
                    Word(name, start, i, ref writer, searchable, keepEmoji);
            }

            return writer.ToString();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>How the letters of one word are spread over scripts, and what that means for it.</summary>
    private readonly struct WordShape
    {
        private readonly int _ornamentScripts;

        /// <param name="counts">Letters per script.</param>
        /// <param name="oneLetterOnly">Scripts whose letters in the word are all the same character.</param>
        /// <param name="foldable">Letters from other scripts that have a Latin look-alike.</param>
        public WordShape(ReadOnlySpan<byte> counts, int oneLetterOnly, int foldable)
        {
            var scripts = 0;
            var only = NameScript.Common;
            for (var s = 1; s < counts.Length; s++)
            {
                if (counts[s] == 0)
                    continue;
                scripts++;
                only = (NameScript)s;
            }

            HasLatin = counts[(int)NameScript.Latin] > 0;

            // Written in one script other than Latin, with at least two letters of it: a single
            // letter is as likely a heart, a smiley or an ornament as a word.
            WrittenInOwnScript = !HasLatin && scripts == 1 && counts[(int)only] >= 2;

            // What counts as "beside Latin letters" once the look-alikes have folded.
            var latinAfterFolding = HasLatin || (!WrittenInOwnScript && foldable > 0);
            _ornamentScripts = latinAfterFolding ? oneLetterOnly : 0;
        }

        public bool HasLatin { get; }

        public bool WrittenInOwnScript { get; }

        /// <summary>Whether a letter of this script may fold to the Latin letter it looks like.</summary>
        public bool Folds(NameScript script) => script is NameScript.Latin or NameScript.Common || !WrittenInOwnScript;

        /// <summary>
        /// One letter from another script, or the same one repeated, beside Latin letters is
        /// decoration: the ツ in <c>Asherツ</c>, the ఌ in <c>ఌCozyఌ</c>, the 亞 in <c>亞Rebel亞</c>.
        /// Two different letters are a word in that script.
        /// </summary>
        public bool IsOrnament(NameScript script) => (_ornamentScripts & (1 << (int)script)) != 0;
    }

    private static void Word(string name, int start, int end, ref Writer writer, bool searchable, bool keepEmoji)
    {
        // How many of the word's letters are in each script, counting a look-alike as the script
        // it is drawn from; whether each script's letters are all one character; and how many
        // letters from other scripts have a look-alike at all.
        Span<byte> counts = stackalloc byte[(int)NameScript.Decoration + 1];
        Span<int> first = stackalloc int[(int)NameScript.Decoration + 1];
        var oneLetterOnly = -1;
        var foldable = 0;
        for (var i = start; i < end; i += RuneLength(name, i))
        {
            var rune = RuneAt(name, i);
            foreach (var d in Decomposed(rune, searchable))
            {
                // Letters only: digits do not make a word, so a bow of two Oriya digits is not
                // "written in Oriya".
                if (!Rune.IsLetter(d) && Rune.GetUnicodeCategory(d) is not UnicodeCategory.LetterNumber)
                    continue;

                var script = NameScripts.Of(d.Value);
                if (script is NameScript.Common or NameScript.Decoration)
                    continue;

                var s = (int)script;
                if (counts[s] == 0)
                    first[s] = d.Value;
                else if (first[s] != d.Value)
                    oneLetterOnly &= ~(1 << s);

                if (counts[s] < byte.MaxValue)
                    counts[s]++;

                if (script is not NameScript.Latin && NameFoldingTable.Keys.BinarySearch(d.Value) >= 0)
                    foldable++;
            }
        }

        var shape = new WordShape(counts, oneLetterOnly, foldable);

        for (var i = start; i < end; i += RuneLength(name, i))
        {
            var rune = RuneAt(name, i);
            foreach (var d in Decomposed(rune, searchable))
                Emit(d, name, i + RuneLength(name, i), end, ref writer, searchable, keepEmoji, shape);
        }
    }

    private static void Emit(
        Rune rune,
        string name,
        int next,
        int end,
        ref Writer writer,
        bool searchable,
        bool keepEmoji,
        WordShape word)
    {
        var cp = rune.Value;

        if (cp < 0x80)
        {
            if (cp < 0x20 || cp == 0x7F)
                return;

            if (cp == ' ')
            {
                writer.Space();
                return;
            }

            writer.Append((char)cp, NameScripts.Of(cp), searchable);
            return;
        }

        var category = Rune.GetUnicodeCategory(rune);

        if (IsSpaceLike(cp, category))
        {
            writer.Space();
            return;
        }

        switch (category)
        {
            case UnicodeCategory.Format:
                // A joiner inside an emoji sequence is part of the emoji.
                if (keepEmoji && !searchable && cp == 0x200D && writer.LastWasEmoji)
                    writer.Append(rune, NameScript.Common, emoji: true);
                return;

            case UnicodeCategory.Control:
            case UnicodeCategory.PrivateUse:
            case UnicodeCategory.Surrogate:
                return;

            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.SpacingCombiningMark:
            case UnicodeCategory.EnclosingMark:
                if (searchable)
                    return;

                if (cp is 0xFE0E or 0xFE0F)
                {
                    if (keepEmoji && writer.LastWasEmoji)
                        writer.Append(rune, NameScript.Common, emoji: true);
                    return;
                }

                // A script's own marks stay on that script's letters. Anything on a Latin letter,
                // a digit, a symbol or a space is decoration; so is an enclosing mark, a generic
                // mark with no orthographic meaning wherever it sits, and a mark from one script
                // on a letter of another.
                if (category is UnicodeCategory.EnclosingMark)
                    return;

                if (!writer.LastWasLetter || writer.LastLetterScript is NameScript.Common or NameScript.Latin or NameScript.Decoration)
                    return;

                if (IsGenericMark(cp))
                {
                    if (!IsOrthographicMark(cp))
                        return;
                }
                else if (NameScripts.Of(cp) != writer.LastLetterScript)
                {
                    return;
                }

                writer.Append(rune, writer.LastLetterScript, emoji: false, letter: false);
                return;
        }

        var script = NameScripts.Of(cp);
        var letterOrDigit = Rune.IsLetterOrDigit(rune) || category is UnicodeCategory.LetterNumber;

        if (word.Folds(script))
        {
            var index = NameFoldingTable.Keys.BinarySearch(cp);
            if (index >= 0)
            {
                var folded = NameFoldingTable.Values[index];

                if (folded.Length == 1 && folded[0] == 'l' && NameFoldingTable.CapitalIOrSmallL.BinarySearch(cp) >= 0)
                    folded = ReadsAsCapital(name, next, end, ref writer) ? "I" : "l";

                foreach (var c in folded)
                    writer.Append(c, NameScripts.Of(c), searchable);

                return;
            }
        }

        if (letterOrDigit)
        {
            if (script is NameScript.Decoration || word.IsOrnament(script))
            {
                if (keepEmoji && !searchable)
                    writer.Append(rune, script, emoji: true);
                return;
            }

            // Spacing modifier letters (˚ ˏ ˋ) are letters to Unicode and ornament to everybody.
            if (script is NameScript.Common && category is UnicodeCategory.ModifierLetter)
            {
                Symbol(rune, ref writer, searchable, keepEmoji);
                return;
            }

            // A digit of another script is that digit inside a word written in that script.
            // Anywhere else -- beside Latin letters, or standing alone -- it is a bow or a
            // flourish (୨୧), never a number.
            if (category is UnicodeCategory.DecimalDigitNumber)
            {
                if (!word.WrittenInOwnScript)
                {
                    if (keepEmoji && !searchable)
                        writer.Append(rune, script, emoji: true);
                    return;
                }

                var value = Rune.GetNumericValue(rune);
                if (value is >= 0 and <= 9 && value == Math.Floor(value))
                    writer.Append((char)('0' + (int)value), NameScript.Common, searchable);
                else
                    writer.Append(rune, script, emoji: false, letter: false);
                return;
            }

            writer.Append(rune, script, emoji: false, letter: true, lower: searchable);
            return;
        }

        Symbol(rune, ref writer, searchable, keepEmoji);
    }

    private static void Symbol(Rune rune, ref Writer writer, bool searchable, bool keepEmoji)
    {
        if (searchable || !keepEmoji)
            return;

        if (IsEmoji(rune.Value) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.OtherNotAssigned)
            writer.Append(rune, NameScript.Common, emoji: true);
    }

    /// <summary>
    /// Whether an I-or-l look-alike reads as a capital: the letter before it decides, else the
    /// next letter in the word.
    /// </summary>
    private static bool ReadsAsCapital(string name, int next, int end, ref Writer writer)
    {
        if (writer.LastLetterCase is { } previous)
            return previous;

        for (var i = next; i < end; i += RuneLength(name, i))
        {
            foreach (var d in Decomposed(RuneAt(name, i), searchable: false))
            {
                if (Rune.IsUpper(d))
                    return true;
                if (Rune.IsLower(d))
                    return false;
            }
        }

        return false;
    }

    private static Decomposition Decomposed(Rune rune, bool searchable)
    {
        var index = NameDecompositionTable.Keys.BinarySearch(rune.Value);
        if (index < 0)
            return new Decomposition(rune);

        if (!searchable && NameDecompositionTable.KeepsComposedWhenReadable[index] == 1)
            return new Decomposition(rune);

        return new Decomposition(NameDecompositionTable.Values[index]);
    }

    /// <summary>A rune, or the string it decomposes to, enumerated as runes without allocating.</summary>
    private readonly struct Decomposition
    {
        private readonly Rune _single;
        private readonly string? _many;

        public Decomposition(Rune single)
        {
            _single = single;
            _many = null;
        }

        public Decomposition(string many)
        {
            _single = default;
            _many = many;
        }

        public Enumerator GetEnumerator() => new(this);

        public struct Enumerator
        {
            private readonly Decomposition _source;
            private int _index;
            private bool _done;

            public Enumerator(Decomposition source)
            {
                _source = source;
                _index = 0;
                _done = false;
                Current = default;
            }

            public Rune Current { get; private set; }

            public bool MoveNext()
            {
                if (_source._many is null)
                {
                    if (_done)
                        return false;
                    _done = true;
                    Current = _source._single;
                    return true;
                }

                if (_index >= _source._many.Length)
                    return false;

                Current = RuneAt(_source._many, _index);
                _index += Current.Utf16SequenceLength;
                return true;
            }
        }
    }

    // ── character classes ──────────────────────────────────────────────────────────────────

    private static bool IsBreak(string text, int index, out int length)
    {
        var rune = RuneAt(text, index);
        length = rune.Utf16SequenceLength;
        var cp = rune.Value;

        if (cp < 0x80)
            return cp is ' ' or '\t' or '\n' or '\r' or '\v' or '\f';

        return IsSpaceLike(cp, Rune.GetUnicodeCategory(rune));
    }

    private static bool IsSpaceLike(int cp, UnicodeCategory category) =>
        category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
        || cp is 0x2800 or 0x3164 or 0x115F or 0x1160 or 0xFFA0 or 0x0085;

    /// <summary>The combining-mark blocks that any script may borrow: the zalgo blocks.</summary>
    private static bool IsGenericMark(int cp) => cp is
        (>= 0x0300 and <= 0x036F) or (>= 0x1AB0 and <= 0x1AFF) or (>= 0x1DC0 and <= 0x1DFF)
        or (>= 0x20D0 and <= 0x20FF) or (>= 0xFE00 and <= 0xFE0F) or (>= 0xFE20 and <= 0xFE2F)
        or (>= 0xE0100 and <= 0xE01EF);

    /// <summary>
    /// Marks from the generic block that spell something on a Greek or Cyrillic letter (й, ё, ά,
    /// ῆ). Only those letters ever ask: a mark on a Latin letter is an accent, and accents fold.
    /// </summary>
    private static bool IsOrthographicMark(int cp) => cp is
        0x0300 or 0x0301 or 0x0304 or 0x0306 or 0x0308 or 0x030B or 0x0313 or 0x0314 or 0x0342 or 0x0345;

    /// <summary>What reads as emoji: the pictographs, the symbols and dingbats blocks, and the handful of older ones.</summary>
    private static bool IsEmoji(int cp) => cp is
        0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or 0x24C2 or 0x3030 or 0x303D or 0x3297 or 0x3299
        or (>= 0x2194 and <= 0x21AA) or (>= 0x231A and <= 0x23FF) or (>= 0x25AA and <= 0x25FE)
        or (>= 0x2600 and <= 0x27BF) or (>= 0x2934 and <= 0x2935) or (>= 0x2B05 and <= 0x2B55)
        or (>= 0x1F000 and <= 0x1FAFF) or (>= 0x1FB00 and <= 0x1FBFF);

    private static Rune RuneAt(string text, int index)
        => Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out _) == OperationStatus.Done
            ? rune
            : Rune.ReplacementChar;

    /// <summary>How many UTF-16 units the character at <paramref name="index"/> takes; one for a lone surrogate.</summary>
    private static int RuneLength(string text, int index)
        => Rune.DecodeFromUtf16(text.AsSpan(index), out _, out var consumed) == OperationStatus.Done ? consumed : 1;

    // ── output ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A pooled buffer that collapses spaces and remembers what it last wrote.</summary>
    private ref struct Writer
    {
        private char[] _buffer;
        private int _length;
        private bool _spacePending;

        public Writer(int inputLength)
        {
            _buffer = ArrayPool<char>.Shared.Rent(Math.Max(32, inputLength * 4));
            _length = 0;
            _spacePending = false;
            LastLetterScript = NameScript.Common;
            LastWasLetter = false;
            LastWasEmoji = false;
            LastLetterCase = null;
        }

        /// <summary>The script of the last letter written; Common before any.</summary>
        public NameScript LastLetterScript { get; private set; }

        /// <summary>Whether the last thing written was a letter (or a mark on one).</summary>
        public bool LastWasLetter { get; private set; }

        public bool LastWasEmoji { get; private set; }

        /// <summary>True for a capital, false for a small letter, null when no cased letter has been written.</summary>
        public bool? LastLetterCase { get; private set; }

        public void Space()
        {
            if (_length > 0)
                _spacePending = true;

            LastWasLetter = false;
            LastWasEmoji = false;
        }

        /// <param name="lower">Write the lower-case letter; the case remembered is still the one given.</param>
        public void Append(char c, NameScript script, bool lower)
        {
            Flush();
            Ensure(1);
            _buffer[_length++] = lower ? char.ToLowerInvariant(c) : c;
            Note(new Rune(c), script, emoji: false, letter: char.IsLetter(c));
        }

        public void Append(Rune rune, NameScript script, bool emoji, bool? letter = null, bool lower = false)
        {
            Flush();
            var written = lower ? Rune.ToLowerInvariant(rune) : rune;
            Ensure(written.Utf16SequenceLength);
            _length += written.EncodeToUtf16(_buffer.AsSpan(_length));
            Note(rune, script, emoji, letter ?? Rune.IsLetter(rune));
        }

        private void Note(Rune rune, NameScript script, bool emoji, bool letter)
        {
            LastWasEmoji = emoji;
            LastWasLetter = letter;
            if (letter)
            {
                LastLetterScript = script;
                if (Rune.IsUpper(rune))
                    LastLetterCase = true;
                else if (Rune.IsLower(rune))
                    LastLetterCase = false;
            }
        }

        private void Flush()
        {
            if (!_spacePending)
                return;

            _spacePending = false;
            Ensure(1);
            _buffer[_length++] = ' ';
            LastWasLetter = false;
            LastWasEmoji = false;
        }

        private void Ensure(int more)
        {
            if (_length + more <= _buffer.Length)
                return;

            var bigger = ArrayPool<char>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + more));
            _buffer.AsSpan(0, _length).CopyTo(bigger);
            ArrayPool<char>.Shared.Return(_buffer);
            _buffer = bigger;
        }

        public override string ToString() => _length == 0 ? string.Empty : new string(_buffer, 0, _length);

        public void Dispose()
        {
            if (_buffer.Length > 0)
                ArrayPool<char>.Shared.Return(_buffer);
            _buffer = [];
        }
    }
}
