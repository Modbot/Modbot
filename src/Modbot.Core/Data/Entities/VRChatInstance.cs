namespace Modbot.Core.Data.Entities;

/// <summary>
/// One instance -- one room, open from the moment somebody first appears in it until it closes.
/// The table is <c>vrchat_instance</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The key is Modbot's own id, not VRChat's.</strong> VRChat's instance ids are short
/// numbers like <c>39047</c> that are only unique within a world while the room is alive, and are
/// handed out again afterwards. Keying on the number would silently glue two unrelated evenings
/// into one row: the same dozen people, a week apart, filed as a single instance, with a "longest
/// session" figure that never happened. So every room Modbot sees gets an id of its own, and
/// VRChat's number is stored beside it as ordinary data (<see cref="VRChatInstanceId"/>).
/// </para>
/// <para>
/// <strong>When does the same number become a different room?</strong> Two answers, and the
/// better one is used when it is available:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <em>The group's own list.</em> <c>/groups/{groupId}/instances</c> says which rooms the managed
/// group has open right now, so one dropping off the list has ended, exactly and at a known time.
/// Anything with that number afterwards is certainly new. This is the authority, and it is the
/// only way Modbot learns about a room <strong>nobody running the client is standing in</strong>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>Time, for everywhere else.</em> A moderator in a public world is somewhere the group's
/// list does not reach. There, a location unseen for <see cref="CountsAsNewAfter"/> is treated as
/// finished, and the next sighting of that number opens a new row. Three days is long enough that
/// no real room spans the gap and short enough that a number genuinely does get reissued.
/// </description>
/// </item>
/// </list>
/// <para>
/// Both rules only ever <em>split</em>. Neither merges two rows into one, because a wrongly split
/// instance is two short sessions that are each true, while a wrongly merged one is a single long
/// session that is false.
/// </para>
/// <para>
/// Every time here comes from <c>IModbotClock</c>, and every VRChat id is opaque text stored
/// exactly as it arrived (foundation section 3.1.1).
/// </para>
/// </remarks>
public class VRChatInstance
{
    /// <summary>
    /// How long a location must go unseen before the same instance number counts as a new room.
    /// </summary>
    /// <remarks>
    /// Only used where the group's live list cannot answer -- a public or friends world a
    /// moderator wandered into. For the group's own instances the list is exact and this never
    /// comes into it.
    /// </remarks>
    public static readonly TimeSpan CountsAsNewAfter = TimeSpan.FromHours(72);

    /// <summary>Modbot's own id for this room. Means nothing to VRChat.</summary>
    public Guid Id { get; set; }

    // ── Where the room is ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole location string as VRChat wrote it, qualifiers and all --
    /// <c>wrld_4cf5…:85019~group(grp_…)~groupAccessType(plus)</c>. Kept verbatim because it is
    /// what a client reported and what an audit entry carries, and because the parts below are a
    /// convenience taken from it rather than a replacement for it.
    /// </summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>The world id out of the location. Joins to <see cref="VRChatWorld"/> for a name.</summary>
    public string WorldId { get; set; } = string.Empty;

    /// <summary>
    /// VRChat's own id for the instance -- the <c>39047</c> part. Not unique on its own and not
    /// unique over time, which is the whole reason <see cref="Id"/> exists.
    /// </summary>
    public string? VRChatInstanceId { get; set; }

    /// <summary>
    /// The group this room belongs to, when it is a group instance. Null for a public or friends
    /// world a moderator happened to be in.
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// Public, group, friends, private -- in VRChat's own words, as text rather than an enum so
    /// that a kind this build does not know is still recorded.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>For a group instance, how open it is: members, plus, or public.</summary>
    public string? GroupAccessType { get; set; }

    /// <summary>Which region VRChat put the room in, when it said.</summary>
    public string? Region { get; set; }

    // ── How long it was open, and how busy ────────────────────────────────────────────────

    /// <summary>
    /// The first moment Modbot knew this room existed -- not necessarily when it opened, since a
    /// room that has been running for an hour is still new to Modbot the first time it is polled.
    /// </summary>
    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>The most recent moment the room was known to still exist.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// When the room was found to have ended, or null while it is believed open. Set exactly when
    /// a group instance leaves the group's live list, and by the time rule everywhere else.
    /// </summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// How the end was decided: <c>list</c> when the group's live list stopped carrying it,
    /// <c>time</c> when nothing had been seen for <see cref="CountsAsNewAfter"/>. Recorded
    /// because the two are not equally trustworthy and a reader deserves to know which they have.
    /// </summary>
    public string? ClosedBy { get; set; }

    /// <summary>How many people were in the room the last time Modbot counted.</summary>
    public int? LastUserCount { get; set; }

    /// <summary>The most people seen in the room at once, over its whole life.</summary>
    public int? PeakUserCount { get; set; }

    /// <summary>
    /// Whether this room has ever appeared in the managed group's live instance list. When true,
    /// the list is the authority on when it ends and the time rule is never applied to it.
    /// </summary>
    public bool SeenInGroupList { get; set; }
}
