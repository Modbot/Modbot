using System.Text.RegularExpressions;
using Modbot.Core.Moderation;
using Serilog;

namespace Modbot.Moderation;

/// <summary>One term that matched a piece of text.</summary>
/// <param name="Matched">The person's own words that matched, from the original text.</param>
/// <param name="Reason">The Hub note for the term, when it has one.</param>
public sealed record TermHit(string TermKey, string Term, string Matched, string? Reason);

/// <summary>A term list made ready to match: patterns compiled, switched-off terms left out.</summary>
public sealed class CompiledTermList
{
    internal CompiledTermList(IReadOnlyList<CompiledTerm> terms) => Terms = terms;

    internal IReadOnlyList<CompiledTerm> Terms { get; }

    public int Count => Terms.Count;
}

internal sealed record CompiledTerm(StoredTerm Source, string? Folded, Regex? Pattern);

/// <summary>
/// Checks text against term lists (AI moderation design §4.1). No database, no network: the same
/// answer for the same text every time, which is what the "Try it" box and the tests rely on.
/// </summary>
public static class TermMatcher
{
    /// <summary>Every regular expression gets this long per match, and a timeout is no match.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public const int DefaultWithinWords = 12;

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static CompiledTermList Compile(IEnumerable<StoredTerm> terms, IEnumerable<string>? excluded = null)
    {
        var skip = new HashSet<string>(excluded ?? [], StringComparer.Ordinal);
        var compiled = new List<CompiledTerm>();

        foreach (var term in terms)
        {
            if (skip.Contains(term.Id))
                continue;

            switch (term.Kind)
            {
                case TermKind.Word or TermKind.Contains when !string.IsNullOrWhiteSpace(term.Text):
                    var folded = NormalisedText.FoldTerm(term.Text);
                    if (folded.Length > 0)
                        compiled.Add(new CompiledTerm(term, folded, null));
                    break;

                case TermKind.Regex when !string.IsNullOrEmpty(term.Pattern):
                    if (CompilePattern(term.Pattern, out _) is { } regex)
                        compiled.Add(new CompiledTerm(term, null, regex));
                    break;

                case TermKind.Combination when term.AllOf is { Count: > 0 } || term.AnyOf is { Count: > 0 }:
                    compiled.Add(new CompiledTerm(term, null, null));
                    break;
            }
        }

        return new CompiledTermList(compiled);
    }

    /// <summary>
    /// A pattern ready to run, or null with the reason it cannot be.
    /// </summary>
    /// <remarks>
    /// The non-backtracking engine first: it runs in time proportional to the text whatever the
    /// pattern, so a pattern written badly cannot stall message indexing. Patterns it cannot run
    /// (lookaround, backreferences) fall back to the ordinary engine, still under the timeout.
    /// </remarks>
    public static Regex? CompilePattern(string pattern, out string? error)
    {
        error = null;

        try
        {
            return new Regex(pattern, Options | RegexOptions.NonBacktracking, MatchTimeout);
        }
        catch (NotSupportedException)
        {
            // Falls through to the ordinary engine below.
        }
        catch (ArgumentException e)
        {
            error = e.Message;
            return null;
        }

        try
        {
            return new Regex(pattern, Options, MatchTimeout);
        }
        catch (ArgumentException e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>Every term in the list that matches, at most once each.</summary>
    public static IReadOnlyList<TermHit> Check(CompiledTermList list, string text, ModerationTargets target)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (string.IsNullOrWhiteSpace(text) || list.Count == 0)
            return [];

        var field = FieldOf(target);
        NormalisedText? lower = null;
        NormalisedText? folded = null;
        List<Word>? words = null;
        var hits = new List<TermHit>();

        foreach (var term in list.Terms)
        {
            if (term.Source.Fields is { Count: > 0 } fields && !fields.Contains(field, StringComparer.Ordinal))
                continue;

            folded ??= NormalisedText.Folded(text);

            var matched = term.Source.Kind switch
            {
                TermKind.Word => FindText(folded, term.Folded!, wholeWord: true),
                TermKind.Contains => FindText(folded, term.Folded!, wholeWord: false),
                TermKind.Regex => FindPattern(lower ??= NormalisedText.Lower(text), term.Pattern!)
                                  ?? FindPattern(folded, term.Pattern!),
                TermKind.Combination => FindCombination(folded, words ??= Words(folded.Text), term.Source),
                _ => null,
            };

            if (matched is { Length: > 0 })
                hits.Add(new TermHit(term.Source.Id, term.Source.Label, Shorten(matched), term.Source.Note));
        }

        return hits;
    }

    /// <summary>The Hub's field name a target is checked as. A Discord message is free text, like a bio.</summary>
    public static string FieldOf(ModerationTargets target) => target switch
    {
        ModerationTargets.DisplayName => "displayName",
        ModerationTargets.Status => "status",
        ModerationTargets.Pronouns => "pronouns",
        _ => "bio",
    };

    private static string? FindText(NormalisedText text, string term, bool wholeWord)
    {
        var haystack = text.Text;
        var from = 0;

        while (from <= haystack.Length - term.Length)
        {
            var at = haystack.IndexOf(term, from, StringComparison.Ordinal);
            if (at < 0)
                return null;

            if (!wholeWord || IsBoundary(haystack, at - 1) && IsBoundary(haystack, at + term.Length))
                return text.Original(at, term.Length);

            from = at + 1;
        }

        return null;
    }

    private static bool IsBoundary(string text, int index)
        => index < 0 || index >= text.Length || !NormalisedText.IsWordChar(text[index]);

    private static string? FindPattern(NormalisedText text, Regex pattern)
    {
        try
        {
            var match = pattern.Match(text.Text);
            return match.Success && match.Length > 0 ? text.Original(match.Index, match.Length) : null;
        }
        catch (RegexMatchTimeoutException)
        {
            Log.Warning("A moderation pattern ran out of time and was treated as no match: {Pattern}", pattern.ToString());
            return null;
        }
    }

    private readonly record struct Word(int Start, int Length, string Text);

    private static List<Word> Words(string text)
    {
        var words = new List<Word>();
        var i = 0;

        while (i < text.Length)
        {
            if (!NormalisedText.IsWordChar(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && (NormalisedText.IsWordChar(text[i]) || text[i] == '\''))
                i++;

            words.Add(new Word(start, i - start, text[start..i].Replace("'", "", StringComparison.Ordinal)));
        }

        return words;
    }

    /// <summary>Where a phrase starts, as word positions, matching whole words.</summary>
    private static List<int> Occurrences(List<Word> words, string phrase)
    {
        var parts = Words(NormalisedText.FoldTerm(phrase)).Select(w => w.Text).ToArray();
        var found = new List<int>();

        if (parts.Length == 0)
            return found;

        for (var i = 0; i + parts.Length <= words.Count; i++)
        {
            var all = true;
            for (var j = 0; j < parts.Length && all; j++)
                all = string.Equals(words[i + j].Text, parts[j], StringComparison.Ordinal);

            if (all)
                found.Add(i);
        }

        return found;
    }

    private static string? FindCombination(NormalisedText text, List<Word> words, StoredTerm term)
    {
        if (words.Count == 0)
            return null;

        foreach (var excuse in term.NoneOf ?? [])
        {
            if (Occurrences(words, excuse).Count > 0)
                return null;
        }

        // Each allOf phrase is a group of its own; the anyOf phrases together make one more.
        var groups = new List<List<int>>();

        foreach (var required in term.AllOf ?? [])
            groups.Add(Occurrences(words, required));

        if (term.AnyOf is { Count: > 0 } any)
            groups.Add(any.SelectMany(p => Occurrences(words, p)).ToList());

        if (groups.Count == 0 || groups.Any(g => g.Count == 0))
            return null;

        var within = Math.Max(1, term.WithinWords ?? DefaultWithinWords);

        foreach (var anchor in groups.SelectMany(g => g).Distinct().Order())
        {
            var chosen = new List<int>(groups.Count);

            foreach (var group in groups)
            {
                var pick = group.Where(p => p >= anchor && p <= anchor + within).DefaultIfEmpty(-1).Min();
                if (pick < 0)
                    break;
                chosen.Add(pick);
            }

            if (chosen.Count != groups.Count)
                continue;

            var first = words[chosen.Min()];
            var last = words[chosen.Max()];
            return text.Original(first.Start, last.Start + last.Length - first.Start);
        }

        return null;
    }

    private static string Shorten(string text) => text.Length <= 300 ? text : text[..300];
}
