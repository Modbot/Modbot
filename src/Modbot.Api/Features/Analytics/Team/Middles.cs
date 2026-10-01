namespace Modbot.Api.Features.Analytics.Team;

/// <summary>The middle value of a set of numbers: what the Moderation tab gives as "typical".</summary>
/// <remarks>
/// A middle rather than an average because the numbers it is used on are lopsided. One join
/// request left over a weekend would drag an average wait to hours while most were answered in
/// minutes, and one very busy moderator would set the team's usual for everybody else. With an
/// even count it is halfway between the two middle values.
/// </remarks>
public static class Middles
{
    /// <summary>The middle value, or null when there are none.</summary>
    public static decimal? Of(IEnumerable<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var sorted = values.Order().ToList();

        if (sorted.Count == 0)
            return null;

        var half = sorted.Count / 2;

        return sorted.Count % 2 == 1 ? sorted[half] : (sorted[half - 1] + sorted[half]) / 2;
    }
}
