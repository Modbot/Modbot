namespace Modbot.Core.Data.Entities;

/// <summary>
/// One person's record of being acted on by moderators, added up from the fact log. The table is
/// <c>modbot_repeat_offender</c>.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.8.4: kicks, warns, bans and removals across every moderator and every instance, so a
/// moderator can see "fourth kick in thirty days, by three different moderators" without reading
/// the whole timeline.
/// </para>
/// <para>
/// <strong>A cache, never a source</strong> (spec 5.10.2). Every column is recomputed from facts
/// by <c>ReviewJob</c>; nothing is ever written here that a fact does not account for, and a
/// wrong number is a re-run. Rows exist only for people with at least one action against them.
/// </para>
/// <para>
/// Unbans are counted but do not count as actions: an unban is relief, not another strike. The
/// 30- and 90-day columns are measured from <see cref="ComputedAt"/>, and
/// <see cref="CountsChangeAt"/> says when they next go stale on their own so the job knows which
/// rows to refresh even when nothing new has happened to the person.
/// </para>
/// </remarks>
public class RepeatOffender
{
    public FactPlatform SubjectPlatform { get; set; }

    /// <summary>Opaque; never parsed or validated (spec 3.1.1).</summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>Kicked out of a group instance.</summary>
    public int InstanceKicks { get; set; }

    public int Warns { get; set; }

    public int Bans { get; set; }

    /// <summary>Counted for the record; not an action against the person.</summary>
    public int Unbans { get; set; }

    /// <summary>Removed from the group -- VRChat's own word for a kick from the group.</summary>
    public int Removals { get; set; }

    /// <summary>Join requests rejected or blocked.</summary>
    public int Rejections { get; set; }

    /// <summary>Every action against them, all time. The sum of the kinds above, minus unbans.</summary>
    public int Actions { get; set; }

    public int ActionsLast30Days { get; set; }

    public int ActionsLast90Days { get; set; }

    /// <summary>How many different moderators have acted on them, all time.</summary>
    public int Moderators { get; set; }

    public int ModeratorsLast90Days { get; set; }

    public DateTimeOffset FirstActionAt { get; set; }

    public DateTimeOffset LastActionAt { get; set; }

    /// <summary>The fact type of the most recent action.</summary>
    public string LastActionType { get; set; } = string.Empty;

    /// <summary>Who took the most recent action, when VRChat named them.</summary>
    public string? LastActorId { get; set; }

    /// <summary>
    /// <c>once</c>, <c>more-than-once</c> or <c>repeat</c> -- see <see cref="RepeatOffenderStatus"/>.
    /// The rule that decides it is shown next to it wherever it is shown, because "repeat" alone is
    /// a claim a moderator has to take on trust (spec 5.10.3).
    /// </summary>
    public string Status { get; set; } = RepeatOffenderStatus.Once;

    /// <summary>
    /// The next instant at which one of the windowed counts changes with nothing new happening --
    /// an action falling out of the 30- or 90-day window. Null when nothing is due to fall out.
    /// </summary>
    public DateTimeOffset? CountsChangeAt { get; set; }

    public DateTimeOffset ComputedAt { get; set; }
}

/// <summary>The three words <see cref="RepeatOffender.Status"/> can hold.</summary>
public static class RepeatOffenderStatus
{
    /// <summary>One action against them, ever.</summary>
    public const string Once = "once";

    /// <summary>Two or more actions, but not enough recently to count as a repeat offender.</summary>
    public const string MoreThanOnce = "more-than-once";

    /// <summary>At least the configured number of actions in the last 30 days.</summary>
    public const string Repeat = "repeat";
}
