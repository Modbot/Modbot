namespace Modbot.Core.Data.Entities;

/// <summary>
/// A moderator saying "keep an eye on this person" (watching a person design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A watch never acts.</strong> It makes the person Flagged wherever Flagged is shown, it
/// raises a notification when they walk into one of the group's instances, and it can carry a day
/// to check back on them. Nothing on VRChat or Discord changes because somebody is watched.
/// </para>
/// <para>
/// <strong>A row, not only a fact.</strong> A note is a fact and nothing else, because nothing ever
/// asks "which notes stand" for two hundred people at once. A watch is asked exactly that, on every
/// roster read, so it is kept as current state here; the facts
/// <c>modbot.watch.add</c>, <c>modbot.watch.end</c> and <c>modbot.watch.followed-up</c> keep the
/// history in the audit log beside everything else about the person.
/// </para>
/// <para>
/// <strong>One standing watch per account.</strong> A unique index over the rows that have not
/// ended says so. A watch whose end day has passed still stands until the background pass writes
/// its end, but every reader already treats it as over from <see cref="EndsAt"/>.
/// </para>
/// </remarks>
public class PersonWatch
{
    /// <summary>
    /// The longest a reason may be. A reason is a line, not a note: the write-up of what happened
    /// belongs in a note or a case file.
    /// </summary>
    public const int MaxReasonLength = 200;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Which of the person's accounts is watched: VRChat or Discord.</summary>
    public FactPlatform SubjectPlatform { get; set; }

    /// <summary>The account's id. Opaque, never checked for shape (foundation §3.1.1).</summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>
    /// Why, in the moderator's words. Required. Read only under the audit log's permission: the
    /// roster, the join card and Live say "Watched" and never this.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The Modbot account that started it.</summary>
    public Guid SetByUserId { get; set; }

    /// <summary>Their username as it was then.</summary>
    public string SetByUsername { get; set; } = string.Empty;

    public DateTimeOffset SetAt { get; set; }

    /// <summary>When it stops on its own. Null means it stands until somebody stops it.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>When somebody should check on the person again. Null means no follow-up.</summary>
    public DateTimeOffset? FollowUpAt { get; set; }

    /// <summary>
    /// When the reminder for <see cref="FollowUpAt"/> was raised. Null while it has not been, so
    /// the background pass says each follow-up once.
    /// </summary>
    public DateTimeOffset? FollowUpRemindedAt { get; set; }

    /// <summary>When it ended, by hand or because <see cref="EndsAt"/> passed. Null while it stands.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Who stopped it. Null when it ran out on its own.</summary>
    public Guid? EndedByUserId { get; set; }

    public string? EndedByUsername { get; set; }

    /// <summary>Whether it still stands at <paramref name="now"/>.</summary>
    public bool StandsAt(DateTimeOffset now) => EndedAt is null && (EndsAt is null || EndsAt > now);
}
