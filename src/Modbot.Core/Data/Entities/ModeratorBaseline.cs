namespace Modbot.Core.Data.Entities;

/// <summary>
/// What one moderator usually does in a day, so a day can be compared against it. The table is
/// <c>modbot_moderator_baseline</c>.
/// </summary>
/// <remarks>
/// <para>
/// Built from the daily totals rather than from facts: the per-moderator action counts already
/// exist there, one row per day per kind, so the whole table is a few hundred rows summed. It is
/// rewritten on every detection run and is a cache like the daily totals themselves.
/// </para>
/// <para>
/// The window is the last 90 days up to and not including today, so today's burst cannot raise
/// the usual it is being compared against.
/// </para>
/// </remarks>
public class ModeratorBaseline
{
    public FactPlatform Platform { get; set; }

    public string ModeratorId { get; set; } = string.Empty;

    /// <summary>Actions against people in the window: instance kicks, warns, bans, removals, rejections.</summary>
    public decimal Actions { get; set; }

    /// <summary>Days in the window on which they did at least one of those.</summary>
    public int ActiveDays { get; set; }

    /// <summary><see cref="Actions"/> over <see cref="ActiveDays"/>: "their usual is 3 a day".</summary>
    public decimal ActionsPerActiveDay { get; set; }

    public DateOnly? FirstDay { get; set; }

    public DateOnly? LastDay { get; set; }

    public DateTimeOffset ComputedAt { get; set; }
}
