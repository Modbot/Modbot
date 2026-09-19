namespace Modbot.Core.Data.Entities;

/// <summary>
/// One person Modbot has invited to the group on its own. The table is <c>group_auto_invite</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the memory that stops the same person being invited over and over (auto-invites design
/// §6), and it is also where the thirty-second pacing is measured from (§5.2). Both have to
/// survive a restart, so both are rows rather than anything held in the process.
/// </para>
/// <para>
/// <strong>The row is written before the invite is sent</strong>, and the answer is written back
/// afterwards. If the process dies between the two, an invite is remembered as having happened
/// that may not have. The other order loses the record of an invite that did go out and the
/// person is asked again, and being pestered is the failure the feature was asked to avoid.
/// </para>
/// <para>
/// A refused invite still counts. A person VRChat keeps saying no about — blocked, deleted,
/// whatever the reason — would otherwise be retried every thirty seconds forever.
/// </para>
/// <para>
/// Every time here comes from <c>IModbotClock</c>, and the id is opaque text stored exactly as it
/// arrived (foundation §3.1.1).
/// </para>
/// </remarks>
public class GroupAutoInvite
{
    /// <summary>The person. VRChat's id, never parsed and never validated.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The first time Modbot invited them.</summary>
    public DateTimeOffset FirstInvitedAt { get; set; }

    /// <summary>The most recent time. What "invite again after" is counted from.</summary>
    public DateTimeOffset InvitedAt { get; set; }

    /// <summary>How many invites have gone out to them.</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// VRChat's number for the instance they were standing in when the last invite went out.
    /// Kept so the fact and the row say the same thing.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// Whether VRChat accepted the last invite. Null while the answer has not come back — which,
    /// after a crash between the two writes, is where it stays.
    /// </summary>
    public bool? Worked { get; set; }

    /// <summary>What went wrong, in the gate's own words, when the last invite did not work.</summary>
    public string? Problem { get; set; }
}
