using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Retention;

/// <summary>
/// How long a fact is worth keeping.
/// </summary>
/// <remarks>
/// Spec 5.5. The two classes exist because the volume profiles differ by three orders of
/// magnitude while the value profiles run the opposite way: a ban from three years ago is exactly
/// what a moderator needs, and an instance join from three weeks ago is not.
/// </remarks>
public enum RetentionClass
{
    /// <summary>
    /// Bans, kicks, role changes, membership changes, Modbot's own audit entries. Hundreds a day.
    /// Kept forever by default.
    /// </summary>
    Moderation,

    /// <summary>
    /// Instance joins and leaves, avatar changes, voice sessions, and Modbot's operational noise.
    /// Up to ~18k an hour at peak. Ninety days by default.
    /// </summary>
    Presence,
}

/// <summary>
/// Which retention class each fact type belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="FactType"/> values are already blocked by class, but the mapping is spelled out
/// per member rather than inferred from numeric ranges: retention decides what gets destroyed, and
/// "it was in the 200s" is not a reason anyone should have to reconstruct from a range check.
/// </para>
/// <para>
/// An unmapped type falls to <see cref="RetentionClass.Moderation"/> -- kept forever. Keeping data
/// longer than intended is a storage bill; deleting it early is unrecoverable, and a new fact type
/// added without a thought about retention should fail in the safe direction.
/// </para>
/// </remarks>
public static class FactRetention
{
    public static RetentionClass ClassOf(FactType type) => type switch
    {
        FactType.MemberJoined => RetentionClass.Moderation,
        FactType.MemberLeft => RetentionClass.Moderation,
        FactType.MemberBanned => RetentionClass.Moderation,
        FactType.MemberUnbanned => RetentionClass.Moderation,
        FactType.MemberKicked => RetentionClass.Moderation,
        FactType.RoleGranted => RetentionClass.Moderation,
        FactType.RoleRevoked => RetentionClass.Moderation,
        FactType.InviteCreated => RetentionClass.Moderation,

        // Written only when the group's metadata actually changed, so it does not accumulate the
        // way operational noise does -- and it is what makes an old role grant readable years
        // later, once the role has been renamed twice.
        FactType.GroupInfoChanged => RetentionClass.Moderation,

        FactType.InstanceJoined => RetentionClass.Presence,
        FactType.InstanceLeft => RetentionClass.Presence,
        FactType.AvatarChanged => RetentionClass.Presence,

        // Discord membership and roles are membership history like VRChat's; voice sessions are
        // presence like instance sessions, and arrive at the same kind of rate.
        FactType.DiscordMemberJoined => RetentionClass.Moderation,
        FactType.DiscordMemberLeft => RetentionClass.Moderation,
        FactType.DiscordRoleGranted => RetentionClass.Moderation,
        FactType.DiscordRoleRevoked => RetentionClass.Moderation,
        FactType.DiscordVoiceJoined => RetentionClass.Presence,
        FactType.DiscordVoiceLeft => RetentionClass.Presence,

        FactType.Login => RetentionClass.Moderation,
        FactType.LoginFailed => RetentionClass.Moderation,
        FactType.PasswordChanged => RetentionClass.Moderation,
        FactType.ApiKeyCreated => RetentionClass.Moderation,
        FactType.ApiKeyRevoked => RetentionClass.Moderation,
        FactType.SettingsChanged => RetentionClass.Moderation,

        // Operational noise takes the short class deliberately (spec 5.9.2). "A sync failed last
        // March" is not history anyone needs, and letting it accumulate forever alongside the
        // moderation record would bury the latter.
        FactType.SyncFailed => RetentionClass.Presence,
        FactType.RateLimitColdStop => RetentionClass.Presence,
        FactType.WafBlocked => RetentionClass.Presence,
        FactType.MigrationApplied => RetentionClass.Presence,
        FactType.RetentionPruned => RetentionClass.Presence,
        FactType.PartitionCreated => RetentionClass.Presence,
        FactType.UserPurged => RetentionClass.Moderation,

        _ => RetentionClass.Moderation,
    };

    /// <summary>Every declared fact type in one class.</summary>
    public static IReadOnlyList<FactType> TypesIn(RetentionClass retention)
        => Enum.GetValues<FactType>().Where(t => ClassOf(t) == retention).ToList();

    public static IReadOnlyList<RetentionClass> All { get; } = Enum.GetValues<RetentionClass>();
}
