namespace Modbot.Core.Data.Entities;

/// <summary>
/// A banned person asking for the ban to be lifted, as staff logged it (batch 3, step G).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A record, not an action.</strong> An appeal is received somewhere Modbot cannot see: a
/// Discord message, an email, a ticket. Staff write it down here so the year's report can say how
/// many came in and how each ended. Upholding one does not unban anybody; staff still lift the ban
/// the way they do today, and the person's profile is one click away.
/// </para>
/// <para>
/// <strong>A row and two facts.</strong> The row is the current state, which the Appeals list and the
/// report read. <c>modbot.appeal.add</c> and <c>modbot.appeal.decide</c> keep the history in the
/// audit log beside everything else about the person, the way a watch does.
/// </para>
/// <para>
/// The person is a VRChat account: the group's bans are VRChat bans. The id is opaque text, stored
/// as sent (foundation §3.1.1).
/// </para>
/// </remarks>
public class Appeal
{
    /// <summary>The longest a note may be. A line or two; the write-up of the ban is the case file.</summary>
    public const int MaxNoteLength = 1000;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The person who appealed. Opaque; never parsed, never validated.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The day the appeal reached the team, as staff entered it. Not the day it was logged.</summary>
    public DateOnly ReceivedOn { get; set; }

    public AppealOutcome Outcome { get; set; } = AppealOutcome.Pending;

    /// <summary>The case file of the ban being appealed, when staff linked one.</summary>
    public Guid? CaseFileId { get; set; }

    /// <summary>What staff wrote when they logged it. May be empty.</summary>
    public string Note { get; set; } = string.Empty;

    public DateTimeOffset LoggedAt { get; set; }

    public Guid LoggedByUserId { get; set; }

    /// <summary>Their username as it was then (§5.9.1).</summary>
    public string LoggedByUsername { get; set; } = string.Empty;

    /// <summary>When it was upheld or turned down. Null while it waits.</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    public Guid? DecidedByUserId { get; set; }

    public string? DecidedByUsername { get; set; }
}

/// <summary>
/// How an appeal ended. <strong>These values are stored. Never renumber one.</strong>
/// </summary>
public enum AppealOutcome
{
    Pending = 0,
    Upheld = 1,
    TurnedDown = 2,
}
