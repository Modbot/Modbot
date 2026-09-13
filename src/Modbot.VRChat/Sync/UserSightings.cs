using Modbot.Core.Data.Entities;

namespace Modbot.VRChat.Sync;

/// <summary>One VRChat user, seen doing something, at a time.</summary>
/// <param name="Reason">
/// <see cref="RefreshReason.SeenInInstance"/> for a client's presence report,
/// <see cref="RefreshReason.SeenInFactLog"/> for anything else.
/// </param>
public readonly record struct UserSighting(string UserId, DateTimeOffset SeenAt, RefreshReason Reason);

/// <summary>
/// Which facts mention a VRChat user, and where in the fact they are.
/// </summary>
/// <remarks>
/// <para>
/// <strong>By fact type, never by the shape of the id.</strong> A fact's subject is "typically a
/// UserID, GroupID, GroupRoleID, or Location", and the obvious way to tell them apart -- does it
/// start with <c>usr_</c> -- is the exact check spec 3.1.1 forbids, because legacy user ids
/// start with nothing in particular. So the question asked is "does this <em>kind</em> of fact
/// have a person as its subject", and the answer is a list. A type not on the list contributes
/// no subject; its actor still counts, because an audit-log actor is always a person.
/// </para>
/// <para>
/// The profile sync's own facts are excluded on purpose. A refresh that wrote "profile changed"
/// must not count as a sighting of the person, or every refresh would queue the next one.
/// </para>
/// </remarks>
public static class UserSightings
{
    /// <summary>Fact types whose subject is a VRChat user.</summary>
    private static readonly HashSet<string> SubjectIsAUser = new(StringComparer.Ordinal)
    {
        FactType.MemberJoined,
        FactType.MemberLeft,
        FactType.MemberBanned,
        FactType.MemberUnbanned,
        FactType.MemberKicked,
        FactType.RoleGranted,
        FactType.RoleRevoked,
        FactType.InviteCreated,
        FactType.JoinRequestCreated,
        FactType.JoinRequestRejected,
        FactType.JoinRequestBlocked,
        FactType.GroupInstanceKick,
        FactType.GroupInstanceWarn,
        FactType.InstanceJoined,
        FactType.InstanceLeft,
        FactType.AvatarChanged,
        FactType.InstancePresenceObserved,
    };

    /// <summary>Fact types that mean the person is in an instance right now, or was a moment ago.</summary>
    private static readonly HashSet<string> IsPresence = new(StringComparer.Ordinal)
    {
        FactType.InstanceJoined,
        FactType.InstanceLeft,
        FactType.AvatarChanged,
        FactType.InstancePresenceObserved,
    };

    /// <summary>Whether this kind of fact names a person as its subject.</summary>
    public static bool SubjectIsUser(string type) => SubjectIsAUser.Contains(type);

    /// <summary>The people one fact mentions: its subject when the type says so, and its actor when there is one.</summary>
    public static IEnumerable<UserSighting> From(
        string type,
        FactPlatform subjectPlatform,
        string subjectId,
        FactPlatform? actorPlatform,
        string? actorId,
        DateTimeOffset occurredAt)
    {
        if (subjectPlatform == FactPlatform.VRChat
            && !string.IsNullOrWhiteSpace(subjectId)
            && SubjectIsAUser.Contains(type))
        {
            yield return new UserSighting(
                subjectId,
                occurredAt,
                IsPresence.Contains(type) ? RefreshReason.SeenInInstance : RefreshReason.SeenInFactLog);
        }

        // Whoever did the thing. A moderator banning somebody is as much a sighting of the
        // moderator as of the person banned -- and a moderator's profile is worth having too.
        if (actorPlatform == FactPlatform.VRChat
            && !string.IsNullOrWhiteSpace(actorId)
            && !string.Equals(actorId, subjectId, StringComparison.Ordinal))
        {
            yield return new UserSighting(actorId, occurredAt, RefreshReason.SeenInFactLog);
        }
    }
}
