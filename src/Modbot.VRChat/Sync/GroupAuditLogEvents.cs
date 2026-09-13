using Modbot.Core.Data.Entities;

namespace Modbot.VRChat.Sync;

/// <summary>
/// VRChat's group audit-log event types, and the fact types they mean.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This list is not authoritative and cannot be.</strong> VRChat publishes
/// <c>eventType</c> as a free-form string -- the OpenAPI schema types it as <c>string</c> with one
/// example, not as an enum -- so there is no contract to compile against. Every mapping here is an
/// observation, and live data is the only check on it: <c>group.user.ban</c> is what a real
/// group's log actually says for a ban, a spelling that was for a while listed here as a
/// speculative alias while the "primary" <c>group.member.user.ban</c> never appeared once.
/// </para>
/// <para>
/// That is why the unmapped path is loud rather than silent. An entry whose type is not in this
/// table is recorded under <see cref="FactType.Unrecognised"/> with VRChat's own wording kept, and
/// is counted, sampled and reported (see <c>SyncDiagnostics</c>), so a type Modbot has never seen
/// surfaces within one poll instead of quietly costing the group its moderation history.
/// </para>
/// <para>
/// Nothing here is ever keyed on an id's shape. <c>targetId</c> is documented as "typically a
/// UserID, GroupID, GroupRoleID, or Location" and Modbot never inspects it to find out which
/// (spec 3.1.1) -- the event type decides what the target means, and the target is carried
/// through untouched.
/// </para>
/// </remarks>
public static class GroupAuditLogEvents
{
    public const string MemberJoin = "group.member.join";
    public const string MemberLeave = "group.member.leave";

    /// <summary>Removed from the group by a moderator -- VRChat's word for a kick.</summary>
    public const string MemberRemove = "group.member.remove";

    /// <summary>
    /// The spelling live data shows VRChat using for a ban. <see cref="MemberUserBan"/> is kept
    /// because it appears in third-party write-ups and costs nothing to accept.
    /// </summary>
    public const string UserBan = "group.user.ban";
    public const string UserUnban = "group.user.unban";
    public const string MemberUserBan = "group.member.user.ban";
    public const string MemberUserUnban = "group.member.user.unban";

    public const string RoleAssign = "group.member.role.assign";
    public const string RoleUnassign = "group.member.role.unassign";
    public const string RoleUpdate = "group.role.update";

    public const string InviteCreate = "group.invite.create";
    public const string RequestCreate = "group.request.create";
    public const string RequestReject = "group.request.reject";
    public const string RequestBlock = "group.request.block";

    public const string PostCreate = "group.post.create";
    public const string PostDelete = "group.post.delete";

    public const string InstanceCreate = "group.instance.create";
    public const string InstanceClose = "group.instance.close";
    public const string InstanceUpdate = "group.instance.update";
    public const string InstanceAnnouncement = "group.instance.announcement";
    public const string InstanceKick = "group.instance.kick";
    public const string InstanceWarn = "group.instance.warn";

    public const string CalendarEventCreate = "group.calendarEvent.create";
    public const string CalendarEventDelete = "group.calendarEvent.delete";
    public const string CalendarEventSeriesUpdate = "group.calendarEvent.series.update";
    public const string CalendarEventSeriesDelete = "group.calendarEvent.series.delete";

    /// <summary>
    /// The group's own settings changed. Maps onto the same fact type the group-info producer
    /// writes from its polling; the two are told apart by <c>source</c>, and by the audit entry's
    /// id in the payload, which the producer's fact does not carry.
    /// </summary>
    public const string GroupUpdate = "group.update";

    /// <summary>
    /// Every spelling Modbot recognises, and what each one means. Several spellings may share a
    /// meaning; none is ambiguous about it.
    /// </summary>
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [MemberJoin] = FactType.MemberJoined,
        [MemberLeave] = FactType.MemberLeft,
        [MemberRemove] = FactType.MemberKicked,
        ["group.member.kick"] = FactType.MemberKicked,

        [UserBan] = FactType.MemberBanned,
        [MemberUserBan] = FactType.MemberBanned,
        ["group.member.ban"] = FactType.MemberBanned,

        [UserUnban] = FactType.MemberUnbanned,
        [MemberUserUnban] = FactType.MemberUnbanned,
        ["group.member.unban"] = FactType.MemberUnbanned,

        [RoleAssign] = FactType.RoleGranted,
        [RoleUnassign] = FactType.RoleRevoked,
        [RoleUpdate] = FactType.RoleUpdated,

        [InviteCreate] = FactType.InviteCreated,
        [RequestCreate] = FactType.JoinRequestCreated,
        [RequestReject] = FactType.JoinRequestRejected,
        [RequestBlock] = FactType.JoinRequestBlocked,

        [PostCreate] = FactType.GroupPostCreated,
        [PostDelete] = FactType.GroupPostDeleted,

        [InstanceCreate] = FactType.GroupInstanceCreated,
        [InstanceClose] = FactType.GroupInstanceClosed,
        [InstanceUpdate] = FactType.GroupInstanceUpdated,
        [InstanceAnnouncement] = FactType.GroupInstanceAnnouncement,
        [InstanceKick] = FactType.GroupInstanceKick,
        [InstanceWarn] = FactType.GroupInstanceWarn,

        [CalendarEventCreate] = FactType.CalendarEventCreated,
        [CalendarEventDelete] = FactType.CalendarEventDeleted,
        [CalendarEventSeriesUpdate] = FactType.CalendarEventSeriesUpdated,
        [CalendarEventSeriesDelete] = FactType.CalendarEventSeriesDeleted,

        [GroupUpdate] = FactType.GroupInfoChanged,
    };

    /// <summary>The event types Modbot knows how to record, for tests and for the UI.</summary>
    public static IReadOnlyCollection<string> Known => Types.Keys;

    /// <summary>
    /// Maps one event type onto a fact type, or reports that Modbot has never heard of it.
    /// </summary>
    public static bool TryMap(string? eventType, out string type)
    {
        if (!string.IsNullOrWhiteSpace(eventType) && Types.TryGetValue(eventType, out var found))
        {
            type = found;
            return true;
        }

        type = string.Empty;
        return false;
    }
}
