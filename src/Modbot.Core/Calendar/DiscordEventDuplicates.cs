using System.Globalization;
using System.Text;
using Modbot.Core.Discord;

namespace Modbot.Core.Calendar;

/// <summary>
/// Finds Discord server events that look like copies of each other (calendar design §16): the same
/// or nearly the same title, starting close together, whoever made them.
/// </summary>
/// <remarks>
/// <para>
/// Seen on a live server on 2026-10-02: three tools each made Discord events for the same evenings,
/// so the server's list showed two of everything. Modbot cannot tell which copy members should keep,
/// so this only finds them; nothing is deleted.
/// </para>
/// <para>
/// <strong>The title.</strong> Compared as <see cref="Plain"/> gives it: lower case, letters and
/// digits only (so case, spaces, punctuation and emoji do not count), with a bracketed tag at the
/// front left out, because one tool writes "[VRChat, Group Public] Movie night" where another writes
/// "Movie night". A bracket after a word stays: "Game Night (Among Us)" is not "Game Night
/// (Minecraft)". Two titles are the same when one plain title equals the other, or holds it whole and the
/// shorter has at least <see cref="ShortestContainedTitle"/> letters: "Movie night" inside "Movie
/// night at the cinema" counts, a two-letter title inside anything does not.
/// </para>
/// <para>
/// <strong>The time.</strong> Starts at most <see cref="StartsWithin"/> apart. A copy made by a tool
/// that rounds the time, or by hand, is a few minutes off; two different events at the same time are
/// rare enough that the list says "possible".
/// </para>
/// </remarks>
public static class DiscordEventDuplicates
{
    /// <summary>How far apart two copies may start.</summary>
    public static readonly TimeSpan StartsWithin = TimeSpan.FromMinutes(15);

    /// <summary>The fewest letters a title may have and still count as held inside a longer one.</summary>
    public const int ShortestContainedTitle = 6;

    /// <summary>
    /// The title as it is compared: lower case, letters and digits only, with a bracketed tag at the
    /// front left out unless that leaves nothing. Accents and styled letters are reduced to plain ones.
    /// </summary>
    public static string Plain(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var withoutBrackets = Letters(WithoutBrackets(title));
        return withoutBrackets.Length > 0 ? withoutBrackets : Letters(title);
    }

    /// <summary>Whether two titles name the same event, as <see cref="Plain"/> compares them.</summary>
    public static bool SameTitle(string? a, string? b)
    {
        var x = Plain(a);
        var y = Plain(b);

        if (x.Length == 0 || y.Length == 0)
            return false;

        if (string.Equals(x, y, StringComparison.Ordinal))
            return true;

        var (shorter, longer) = x.Length <= y.Length ? (x, y) : (y, x);
        return shorter.Length >= ShortestContainedTitle && longer.Contains(shorter, StringComparison.Ordinal);
    }

    /// <summary>Whether two events look like copies: the same title, starting within <see cref="StartsWithin"/>.</summary>
    public static bool AreCopies(DiscordServerEvent a, DiscordServerEvent b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        return (a.StartsAt - b.StartsAt).Duration() <= StartsWithin && SameTitle(a.Name, b.Name);
    }

    /// <summary>
    /// The events that have at least one copy, in groups: each group is every event linked to the
    /// others by being a copy of one of them. Groups come soonest first, and so do the events in one.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<DiscordServerEvent>> Find(IReadOnlyList<DiscordServerEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var sorted = events
            .DistinctBy(e => e.Id, StringComparer.Ordinal)
            .OrderBy(e => e.StartsAt)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        // Each event starts in a group of its own; a copy found joins the two groups.
        var group = Enumerable.Range(0, sorted.Count).ToArray();

        int Root(int i)
        {
            while (group[i] != i)
                i = group[i] = group[group[i]];

            return i;
        }

        for (var i = 0; i < sorted.Count; i++)
        {
            // Sorted by start, so only the events just after this one can be close enough.
            for (var j = i + 1; j < sorted.Count && sorted[j].StartsAt - sorted[i].StartsAt <= StartsWithin; j++)
            {
                if (AreCopies(sorted[i], sorted[j]))
                    group[Root(j)] = Root(i);
            }
        }

        return [.. Enumerable.Range(0, sorted.Count)
            .GroupBy(Root)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<DiscordServerEvent>)[.. g.Select(i => sorted[i])])
            .OrderBy(g => g[0].StartsAt)];
    }

    /// <summary>
    /// Leaves out the bracketed parts that come before the first word (a tag such as
    /// "[VRChat, Group Public]"). A bracket after a word is part of the name: "Game Night (Among Us)"
    /// and "Game Night (Minecraft)" are two events.
    /// </summary>
    private static string WithoutBrackets(string text)
    {
        var kept = new StringBuilder(text.Length);
        var depth = 0;
        var droppingThisBracket = false;
        var seenWord = false;

        foreach (var c in text)
        {
            if (c is '[' or '(' or '{')
            {
                if (depth == 0)
                    droppingThisBracket = !seenWord;
                depth++;
                continue;
            }

            if (c is ']' or ')' or '}')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 0)
            {
                kept.Append(c);
                if (char.IsLetterOrDigit(c))
                    seenWord = true;
            }
            else if (!droppingThisBracket)
            {
                kept.Append(c);
            }
        }

        return kept.ToString();
    }

    private static string Letters(string text)
    {
        // Compatibility form turns styled letters (𝐌, Ｍ) into plain ones and splits accents off,
        // so "Café" and "Cafe" read the same.
        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var kept = new StringBuilder(decomposed.Length);

        foreach (var rune in decomposed.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;

            if (Rune.IsLetterOrDigit(rune))
                kept.Append(Rune.ToLowerInvariant(rune).ToString());
        }

        return kept.ToString();
    }
}
