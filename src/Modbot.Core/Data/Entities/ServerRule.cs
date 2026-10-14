namespace Modbot.Core.Data.Entities;

/// <summary>
/// One of the community's rules for members, typed by staff on the Terms and rules page and
/// answered by <c>/rule</c> in Discord. The table is <c>server_rule</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The number is the place in the list.</strong> A rule has no number of its own: the first in
/// <see cref="Position"/> order is rule 1, the next rule 2, and moving a rule renumbers the ones
/// between. A gap in <see cref="Position"/> after a delete changes nothing anybody sees.
/// </para>
/// <para>
/// <strong>The limits fit one Discord message.</strong> The answer is the number, the title and the
/// text, at most 2,000 characters together.
/// </para>
/// <para>Free text staff typed. Nothing here names a member, so a purge has nothing to find in this table.</para>
/// </remarks>
public class ServerRule
{
    public const int MaxTitleLength = 100;

    public const int MaxTextLength = 1500;

    /// <summary>How many rules the page holds.</summary>
    public const int MaxRules = 100;

    public Guid Id { get; set; }

    /// <summary>Where the rule stands in the list, lowest first. Not the number shown: see the remarks.</summary>
    public int Position { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
