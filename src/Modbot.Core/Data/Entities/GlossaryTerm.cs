namespace Modbot.Core.Data.Entities;

/// <summary>
/// A word the community uses and what it means, typed by staff on the Terms and rules page and
/// answered by <c>/term</c> in Discord. The table is <c>glossary_term</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The limits fit one Discord message.</strong> A reply is at most 2,000 characters, and the
/// longest <c>/term</c> answer is the name, the other names and the meaning together:
/// 100 + 10 × 50 + 1,000 plus the few characters around them. A name is also what Discord
/// autocomplete hands back, and Discord allows 100 characters there.
/// </para>
/// <para>
/// Free text staff typed. Nothing here names a member, so a purge has nothing to find in this table.
/// </para>
/// </remarks>
public class GlossaryTerm
{
    public const int MaxNameLength = 100;

    public const int MaxOtherNames = 10;

    public const int MaxOtherNameLength = 50;

    public const int MaxMeaningLength = 1000;

    /// <summary>How many terms the page holds.</summary>
    public const int MaxTerms = 500;

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Other names that mean the same, one entry each. <c>/term</c> answers to these too.</summary>
    public string[] OtherNames { get; set; } = [];

    public string Meaning { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
