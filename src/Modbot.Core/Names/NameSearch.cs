using Modbot.Shared.Names;

namespace Modbot.Core.Names;

/// <summary>
/// The patterns a typed search term becomes, for the name columns and their searchable twins.
/// </summary>
/// <remarks>
/// A search matches the name as stored <em>and</em> its searchable form, each against the term
/// in the same shape: the name against the term as typed, the searchable column against the
/// term's searchable form. Typing <c>alex</c> finds <c>𝕬𝖑𝖊𝖝</c> through the second; pasting
/// <c>𝕬𝖑𝖊𝖝</c> finds it through either. Both are ILIKE with the term's own <c>%</c>, <c>_</c>
/// and <c>\</c> escaped, because a moderator typing an underscore means an underscore, and
/// legacy VRChat ids contain anything at all (foundation §3.1.1).
/// </remarks>
public static class NameSearch
{
    /// <summary>The term with ILIKE's three special characters escaped, and nothing else.</summary>
    public static string Escape(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        return term.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    /// <summary>An ILIKE pattern matching the term anywhere in a name, literally.</summary>
    public static string Pattern(string term) => "%" + Escape(term) + "%";

    /// <summary>
    /// The pattern for a searchable column: the term's own searchable form, anywhere. Null when
    /// nothing of the term survives folding (a term of decoration alone), so that no clause is
    /// added rather than one that matches every row.
    /// </summary>
    public static string? SearchablePattern(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        var searchable = NameNormalizer.Searchable(term);
        return searchable.Length == 0 ? null : "%" + Escape(searchable) + "%";
    }
}
