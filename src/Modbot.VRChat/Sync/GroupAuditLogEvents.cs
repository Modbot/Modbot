using Modbot.Core.Data.Entities;

namespace Modbot.VRChat.Sync;

/// <summary>
/// VRChat's group audit-log event types, and the fact types they mean.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This list is not authoritative and cannot be.</strong> VRChat publishes
/// <c>eventType</c> as a free-form string -- the OpenAPI schema types it as <c>string</c> with one
/// example, not as an enum -- so there is no contract to compile against and no way to know the
/// full set without asking VRChat for it. Every mapping here is an observation.
/// </para>
/// <para>
/// That is why the unmapped path is loud rather than silent. An entry whose type is not in this
/// table is counted, sampled and reported (see <c>SyncDiagnostics</c>), so a type Modbot has never
/// seen -- or one whose name here is simply wrong -- surfaces within one poll instead of quietly
/// costing the group its moderation history. Dropping it silently would be the worst available
/// behaviour: the fact log would look healthy and be incomplete.
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

    public const string MemberBan = "group.member.user.ban";
    public const string MemberUnban = "group.member.user.unban";
    public const string RoleAssign = "group.member.role.assign";
    public const string RoleUnassign = "group.member.role.unassign";
    public const string InviteCreate = "group.invite.create";

    /// <summary>
    /// The spellings Modbot believes VRChat actually emits.
    /// </summary>
    /// <remarks>
    /// These are the ones that matter. If VRChat does not declare one of these for a real group
    /// (see <c>AuditLogVocabulary</c>) then the name here is probably wrong, and the fact it maps
    /// to is being lost — which for a ban is the group's moderation history going missing while
    /// everything looks healthy.
    /// </remarks>
    private static readonly Dictionary<string, FactType> PrimaryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [MemberJoin] = FactType.MemberJoined,
        [MemberLeave] = FactType.MemberLeft,
        [MemberRemove] = FactType.MemberKicked,
        [MemberBan] = FactType.MemberBanned,
        [MemberUnban] = FactType.MemberUnbanned,
        [RoleAssign] = FactType.RoleGranted,
        [RoleUnassign] = FactType.RoleRevoked,
        [InviteCreate] = FactType.InviteCreated,
    };

    /// <summary>
    /// Spellings VRChat has used, or plausibly could.
    /// </summary>
    /// <remarks>
    /// The prefix under which bans and kicks live has moved before. These are cheap insurance in
    /// one direction only — if VRChat never emits them nothing happens, and if it does, a ban is
    /// recorded rather than reported as unknown. Each is unambiguous about what it means; nothing
    /// here could map to two different fact types.
    /// <para>
    /// They are kept separate from <see cref="PrimaryTypes"/> precisely so the vocabulary check
    /// can tell the two failures apart. An alias VRChat does not declare is the expected case and
    /// means nothing. A <em>primary</em> it does not declare is a bug in this file.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, FactType> AliasTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["group.member.ban"] = FactType.MemberBanned,
        ["group.member.unban"] = FactType.MemberUnbanned,
        ["group.member.kick"] = FactType.MemberKicked,
        ["group.user.ban"] = FactType.MemberBanned,
        ["group.user.unban"] = FactType.MemberUnbanned,
    };

    private static readonly Dictionary<string, FactType> Types =
        PrimaryTypes.Concat(AliasTypes).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>The event types Modbot knows how to record, for tests and for the UI.</summary>
    public static IReadOnlyCollection<string> Known => Types.Keys;

    /// <summary>The spellings Modbot expects VRChat to declare. A missing one is a defect.</summary>
    public static IReadOnlyCollection<string> Primary => PrimaryTypes.Keys;

    /// <summary>Speculative spellings. A missing one is normal and means nothing.</summary>
    public static IReadOnlyCollection<string> Aliases => AliasTypes.Keys;

    /// <summary>
    /// Maps one event type onto a fact type, or reports that Modbot has never heard of it.
    /// </summary>
    public static bool TryMap(string? eventType, out FactType type)
    {
        if (!string.IsNullOrWhiteSpace(eventType))
            return Types.TryGetValue(eventType, out type);

        type = default;
        return false;
    }
}
