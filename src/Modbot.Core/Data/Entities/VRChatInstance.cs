namespace Modbot.Core.Data.Entities;

/// <summary>
/// One instance -- one instance, open from the moment somebody first appears in it until it closes.
/// The table is <c>vrchat_instance</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The key is Modbot's own id, not VRChat's.</strong> VRChat's instance ids are short
/// numbers like <c>39047</c> that are only unique within a world while the instance is alive, and are
/// handed out again afterwards. Keying on the number would silently glue two unrelated evenings
/// into one row: the same dozen people, a week apart, filed as a single instance, with a "longest
/// session" figure that never happened. So every instance Modbot sees gets an id of its own, and
/// VRChat's number is stored beside it as ordinary data (<see cref="VRChatInstanceId"/>).
/// </para>
/// <para>
/// <strong>When does the same number become a different instance?</strong> Two answers, and the
/// better one is used when it is available:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <em>The group's own list.</em> <c>/groups/{groupId}/instances</c> says which instances the managed
/// group has open right now, so one dropping off the list has ended, exactly and at a known time.
/// Anything with that number afterwards is certainly new. This is the authority, and it is the
/// only way Modbot learns about an instance <strong>nobody running the client is standing in</strong>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>Time, for everywhere else.</em> A moderator in a public world is somewhere the group's
/// list does not reach. There, a location unseen for <see cref="CountsAsNewAfter"/> is treated as
/// finished, and the next sighting of that number opens a new row. Three days is long enough that
/// no real instance spans the gap and short enough that a number genuinely does get reissued.
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
    /// How long a location must go unseen before the same instance number counts as a new instance.
    /// </summary>
    /// <remarks>
    /// Only used where the group's live list cannot answer -- a public or friends world a
    /// moderator wandered into. For the group's own instances the list is exact and this never
    /// comes into it.
    /// </remarks>
    public static readonly TimeSpan CountsAsNewAfter = TimeSpan.FromHours(72);

    /// <summary>Modbot's own id for this instance. Means nothing to VRChat.</summary>
    public Guid Id { get; set; }

    // ── Where the instance is ─────────────────────────────────────────────────────────────────

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
    /// The group this instance belongs to, when it is a group instance. Null for a public or friends
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

    /// <summary>Which region VRChat put the instance in, when it said.</summary>
    public string? Region { get; set; }

    // ── How long it was open, and how busy ────────────────────────────────────────────────

    /// <summary>
    /// The first moment Modbot knew this instance existed -- not necessarily when it opened, since a
    /// instance that has been running for an hour is still new to Modbot the first time it is polled.
    /// </summary>
    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>The most recent moment the instance was known to still exist.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// When the instance was found to have ended, or null while it is believed open. Set exactly when
    /// a group instance leaves the group's live list, and by the time rule everywhere else.
    /// </summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// How the end was decided: <c>list</c> when the group's live list stopped carrying it,
    /// <c>time</c> when nothing had been seen for <see cref="CountsAsNewAfter"/>. Recorded
    /// because the two are not equally trustworthy and a reader deserves to know which they have.
    /// </summary>
    public string? ClosedBy { get; set; }

    /// <summary>How many people were in the instance the last time Modbot counted.</summary>
    public int? LastUserCount { get; set; }

    /// <summary>The most people seen in the instance at once, over its whole life.</summary>
    public int? PeakUserCount { get; set; }

    /// <summary>
    /// How many people are in the instance right now, as best Modbot knows: <c>n_users</c> from the
    /// instance's own page, or the group list's count when the page cannot be read. Every change is
    /// kept in <see cref="InstanceHeadCount"/>. Set only through <c>HeadCounts.Record</c>.
    /// </summary>
    public int? HeadCount { get; set; }

    /// <summary><c>page</c> or <c>list</c>: where <see cref="HeadCount"/> came from — the instance's own page, or the group's list.</summary>
    public string? HeadCountSource { get; set; }

    /// <summary>The instance page's <c>userCount</c> at the last good read, kept beside <c>n_users</c>.</summary>
    public int? PageUserCount { get; set; }

    /// <summary>When the instance's own page was last read successfully.</summary>
    public DateTimeOffset? PageReadAt { get; set; }

    /// <summary>
    /// When reading the instance's own page was last tried, whatever the answer. What spaces the reads
    /// out, so an instance whose page keeps failing is not asked again on every pass.
    /// </summary>
    public DateTimeOffset? PageCheckedAt { get; set; }

    /// <summary>
    /// Whether this instance has ever appeared in the managed group's live instance list. When true,
    /// the list is the authority on when it ends and the time rule is never applied to it.
    /// </summary>
    public bool SeenInGroupList { get; set; }

    // ── The Discord announcement, when there is one ───────────────────────────────────────

    /// <summary>
    /// The Discord message announcing this instance, or null if none was posted.
    /// </summary>
    /// <remarks>
    /// Kept on the instance rather than in a table of its own because there is exactly one message
    /// per instance and it lives and dies with it. Holding the id is what makes the announcement a
    /// single message that keeps being brought up to date rather than a new message every minute.
    /// </remarks>
    public string? AnnouncementMessageId { get; set; }

    /// <summary>
    /// The channel the announcement went to. Stored beside the id because an operator can change
    /// the channel setting, and a message can only be edited in the channel it is actually in.
    /// </summary>
    public string? AnnouncementChannelId { get; set; }

    /// <summary>When the announcement was last written or rewritten.</summary>
    public DateTimeOffset? AnnouncementUpdatedAt { get; set; }

    /// <summary>
    /// Whether the announcement has had its last word -- the edit that says the instance has closed.
    /// Once true nothing touches the message again, so a finished night stops costing Discord
    /// calls forever.
    /// </summary>
    public bool AnnouncementFinished { get; set; }
}
