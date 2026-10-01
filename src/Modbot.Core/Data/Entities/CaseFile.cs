using System.ComponentModel.DataAnnotations.Schema;

namespace Modbot.Core.Data.Entities;

/// <summary>
/// One reason a moderator can pick when writing up a ban -- Harassment, Spam, Underage -- or when
/// kicking, unbanning or turning a join request down (<see cref="UsedFor"/>). The table is
/// <c>ban_reason</c>; the name stayed when the list stopped being only about bans.
/// </summary>
/// <remarks>
/// <para>
/// Foundation §5.8.2: classification is one tap, not a text box. A row of buttons costs a second
/// and gets used; a free-text field gets a single-digit completion rate. The list is the group's
/// to edit (<see cref="ModbotPermissions.EditClassifications"/>), and it is seeded with a plain
/// default set on first use so nobody has to invent one before writing the first case file.
/// </para>
/// <para>
/// <strong>Reasons are never deleted</strong>, only switched off. Case files cite them by id, and
/// a case file that pointed at a reason nobody can name any more would have lost its
/// classification -- which is the signal the whole feature exists to collect.
/// </para>
/// </remarks>
public class BanReason
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The word on the button. Short.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>One line under the button saying what it covers.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Where it sits in the row of buttons. Lower first.</summary>
    public int SortOrder { get; set; }

    /// <summary>Switched-off reasons stay on old case files and disappear from the buttons.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether picking this reason means the written reason cannot be left empty. True for
    /// "Other": a case file that says only "other" says nothing.
    /// </summary>
    public bool NeedsWrittenReason { get; set; }

    /// <summary>
    /// Which actions offer this reason. A ban reason and the reason a ban was lifted are different
    /// questions: "Harassment" is no answer to "why lift the ban?", and "Appeal upheld" is no
    /// reason to ban anybody (M4 §9, ban case files open question 2).
    /// </summary>
    public ReasonUse UsedFor { get; set; } = ReasonUse.Ban | ReasonUse.Kick | ReasonUse.Reject;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The actions a <see cref="BanReason"/> is offered on, as a bitfield. The column is
/// <c>ban_reason.used_for</c>.
/// </summary>
/// <remarks>
/// <strong>These values are stored. Never renumber one</strong>: a reason would quietly move to a
/// different action, and nothing would say so.
/// </remarks>
[Flags]
public enum ReasonUse
{
    None = 0,
    Ban = 1 << 0,
    Kick = 1 << 1,
    Unban = 1 << 2,

    /// <summary>Turning a join request down.</summary>
    Reject = 1 << 3,

    All = Ban | Kick | Unban | Reject,
}

/// <summary>
/// The write-up of one ban: who was banned, why in the moderator's own words, which reasons
/// were picked, what the person's profile looked like at the time, and the evidence attached.
/// The table is <c>case_file</c>.
/// </summary>
/// <remarks>
/// <para>
/// VRChat's audit log says "Gunner24 banned GayHater59" and nothing else -- no reason, anywhere
/// (audit-log research §2). Three months later, when the ban is disputed or the moderator has
/// left, that line is almost useless. The case file is the group's own record of why (foundation
/// §5.8.3, evidence design §1).
/// </para>
/// <para>
/// <strong>Case files are never deleted.</strong> One can be marked withdrawn, with a note, and
/// the row stays. Every create, edit, withdrawal and snapshot recapture is also a fact
/// (<c>modbot.report.*</c>), so the edit history survives whatever this row currently says.
/// </para>
/// <para>
/// <strong>The snapshot never changes after capture</strong>, with one deliberate exception: a
/// moderator may press "refresh and capture again" once, when the profile Modbot held at the
/// time was old. The snapshot that is replaced is kept in full in the fact that records the
/// recapture, so nothing is lost either way.
/// </para>
/// <para>
/// Every id is opaque text stored as VRChat sent it (foundation §3.1.1); every time Modbot stamps
/// comes from <c>IModbotClock</c>.
/// </para>
/// </remarks>
public class CaseFile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The person who was banned. Opaque; never parsed, never validated.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The group the ban is in, as configured when the case file was written.</summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// VRChat's id for the audit-log entry that recorded the ban, when Modbot has one. Null for a
    /// ban that predates Modbot's audit-log window and is known only from the ban list.
    /// </summary>
    public string? AuditEntryId { get; set; }

    /// <summary>The fact id of the ban event, when Modbot recorded one.</summary>
    public long? BanFactId { get; set; }

    /// <summary>When the ban happened, as best Modbot knows: the audit entry, else the ban list.</summary>
    public DateTimeOffset? BannedAt { get; set; }

    // ── Who wrote it ───────────────────────────────────────────────────────────────────────

    public Guid AuthorUserId { get; set; }

    /// <summary>The author's username at the time. Names change; this is what was seen then (§5.9.1).</summary>
    public string AuthorUsername { get; set; } = string.Empty;

    // ── What it says ───────────────────────────────────────────────────────────────────────

    /// <summary>The <see cref="BanReason"/> ids picked, as a JSON array of strings.</summary>
    [Column(TypeName = "jsonb")]
    public string ReasonIds { get; set; } = "[]";

    /// <summary>
    /// The moderator's own account of why, as Markdown. Rendered with raw HTML disabled and
    /// links restricted; images resolve only to evidence attached to this case file (evidence
    /// design §17).
    /// </summary>
    public string WrittenReason { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    public string? UpdatedByUsername { get; set; }

    // ── Withdrawn, never deleted ───────────────────────────────────────────────────────────

    public DateTimeOffset? WithdrawnAt { get; set; }

    public Guid? WithdrawnByUserId { get; set; }

    public string? WithdrawnByUsername { get; set; }

    /// <summary>Why it was withdrawn. Required to withdraw.</summary>
    public string? WithdrawnNote { get; set; }

    public bool IsWithdrawn => WithdrawnAt is not null;

    // ── Lifted ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When the ban this case file is about was lifted from Modbot. Null while it stands, and for a
    /// ban lifted in VRChat itself, which Modbot cannot tie to a case file.
    /// </summary>
    /// <remarks>
    /// The case file is not changed in any other way. A ban that was lifted still had its reasons,
    /// and "this ban was overturned" belongs beside them, not in place of them (M4 §9).
    /// </remarks>
    public DateTimeOffset? LiftedAt { get; set; }

    public Guid? LiftedByUserId { get; set; }

    public string? LiftedByUsername { get; set; }

    /// <summary>The <c>modbot.action.unban</c> fact that lifted it.</summary>
    public long? UnbanFactId { get; set; }

    /// <summary>The unban's <see cref="BanReason"/> ids, as a JSON array of strings: why it was lifted.</summary>
    [Column(TypeName = "jsonb")]
    public string LiftReasonIds { get; set; } = "[]";

    /// <summary>The note the moderator wrote on the unban, if any.</summary>
    public string? LiftNote { get; set; }

    public bool IsLifted => LiftedAt is not null;

    // ── The person at the time ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The person's <c>vrchat_user</c> row as it stood when the case file was written: display
    /// name, bio, status, pronouns, avatar and profile picture, tags, the 18+ flag, and the raw
    /// profile VRChat returned. Null only when Modbot had never fetched a profile for them.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? ProfileAtBan { get; set; }

    /// <summary>Their <c>group_member</c> row at the time -- roles, joined date -- or null if no sweep had listed them.</summary>
    [Column(TypeName = "jsonb")]
    public string? MembershipAtBan { get; set; }

    /// <summary>Their <c>group_ban</c> row at the time, or null if the ban list did not hold them yet.</summary>
    [Column(TypeName = "jsonb")]
    public string? BanListEntryAtBan { get; set; }

    /// <summary>When the snapshot above was taken. Modbot's clock.</summary>
    public DateTimeOffset SnapshotTakenAt { get; set; }

    /// <summary>
    /// When VRChat was last asked about the person, as of the snapshot. The profile is only ever
    /// as current as this; a snapshot taken from a six-hour-old profile says so.
    /// </summary>
    public DateTimeOffset? ProfileRefreshedAt { get; set; }

    /// <summary>Set when the one permitted recapture was used. Null means it is still available.</summary>
    public DateTimeOffset? SnapshotRecapturedAt { get; set; }
}
