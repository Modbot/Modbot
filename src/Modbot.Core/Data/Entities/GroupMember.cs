using System.ComponentModel.DataAnnotations.Schema;

namespace Modbot.Core.Data.Entities;

/// <summary>
/// One person's membership of the managed group, as the member list last showed it. The table is
/// <c>group_member</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written only by the member sweep (member and ban sync design §2). It is current state, not
/// history: the history is the fact log, and this table exists so the Members page can answer
/// "who is in the group right now, with which roles" without re-reading fifty pages of VRChat's
/// list. Anything here can be rebuilt by the next sweep.
/// </para>
/// <para>
/// <strong>Rows are never deleted.</strong> A person who leaves is marked with
/// <see cref="LeftAt"/> and stays, because the facts about them still refer to them and because
/// "when did they leave" is a question worth answering from the row as well as from the log. A
/// person who comes back has <see cref="LeftAt"/> cleared.
/// </para>
/// <para>
/// Every id is opaque text stored exactly as VRChat sent it (foundation §3.1.1), and every time
/// Modbot stamps comes from <c>IModbotClock</c>. The times VRChat states -- <see cref="JoinedAt"/>
/// -- are kept as VRChat stated them.
/// </para>
/// </remarks>
public class GroupMember
{
    /// <summary>The managed group. Part of the key so the table does not assume one group forever.</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>VRChat's id for the person. Opaque: never parsed, never validated.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>VRChat's id for the membership itself (<c>gmem_…</c>), when the list carried one.</summary>
    public string? MembershipId { get; set; }

    /// <summary>The role ids the person holds, as a JSON array of strings, sorted.</summary>
    [Column(TypeName = "jsonb")]
    public string Roles { get; set; } = "[]";

    /// <summary>When VRChat says they joined. Exact, and VRChat's, not Modbot's.</summary>
    public DateTimeOffset? JoinedAt { get; set; }

    /// <summary>VRChat's own word for the membership: <c>member</c>, <c>inactive</c>, and so on. Kept as text, never matched against a list.</summary>
    public string? MembershipStatus { get; set; }

    /// <summary>Whether the person shows this group on their profile: <c>visible</c>, <c>friends</c>, <c>hidden</c>.</summary>
    public string? Visibility { get; set; }

    /// <summary>Whether the group shows above their name tag in-game.</summary>
    public bool IsRepresenting { get; set; }

    /// <summary>
    /// The notes group managers keep on a member, when the bot's role may read them. Staff-written
    /// text about a person: shown only to people who may see members, never to the person.
    /// </summary>
    public string? ManagerNotes { get; set; }

    /// <summary>The first sweep that listed this person.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>The most recent sweep that listed this person. The sweep's own marker for "still here".</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// Set when a full sweep no longer listed the person. Null while they are a member. Cleared if
    /// they come back.
    /// </summary>
    public DateTimeOffset? LeftAt { get; set; }

    /// <summary>
    /// Changes the sweep noticed but has not yet written as facts, as a JSON array.
    /// </summary>
    /// <remarks>
    /// The sweep waits one audit-log poll before recording an inferred join, leave or role change,
    /// so that the audit log -- which is authoritative and exact -- gets to record the event first
    /// and the sweep records only what it missed (design §4). While it waits, the change lives
    /// here. Null when nothing is waiting.
    /// </remarks>
    [Column(TypeName = "jsonb")]
    public string? WaitingFacts { get; set; }

    /// <summary>The list entry exactly as VRChat sent it, on the most recent sweep.</summary>
    [Column(TypeName = "jsonb")]
    public string? Raw { get; set; }
}

/// <summary>
/// One person on the managed group's ban list, as the list last showed it. The table is
/// <c>group_ban</c>.
/// </summary>
/// <remarks>
/// Same shape and same rules as <see cref="GroupMember"/>: written only by the ban sweep, never
/// deleted, a lifted ban is marked with <see cref="LiftedAt"/> rather than removed. The audit log
/// still says who banned them and why they were unbanned; this table says whether they are
/// banned <em>now</em>, which the audit log alone cannot (it starts when Modbot did).
/// </remarks>
public class GroupBan
{
    public string GroupId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    /// <summary>When VRChat says the ban was issued. Exact, and VRChat's.</summary>
    public DateTimeOffset? BannedAt { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Set when a full sweep no longer listed the ban. Null while it stands.</summary>
    public DateTimeOffset? LiftedAt { get; set; }

    /// <inheritdoc cref="GroupMember.WaitingFacts"/>
    [Column(TypeName = "jsonb")]
    public string? WaitingFacts { get; set; }

    /// <summary>The list entry exactly as VRChat sent it, on the most recent sweep.</summary>
    [Column(TypeName = "jsonb")]
    public string? Raw { get; set; }
}
