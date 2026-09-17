using Modbot.VRChat.Sync;

namespace Modbot.Demo;

/// <summary>
/// The group as VRChat would have described it at a given moment.
/// </summary>
/// <remarks>
/// <para>
/// The member count is a <em>level</em>, not a change, so nothing in the fact log adds up to it:
/// joins and leaves say how it moved, never where it started. On a real deployment the group-info
/// sync answers that by writing a baseline fact carrying the whole group and then one fact per
/// observed change, and My Group reads the count straight out of those (<c>GroupAnalyticsQuery</c>).
/// </para>
/// <para>
/// A demo that never wrote them had a full joins chart, a full leaves chart, and no headcount at
/// all — which reads as a broken page rather than as a gap. So the demo writes the same facts the
/// sync would have written, through the same snapshot type, rather than inventing a shape of its
/// own that the page would not know how to read.
/// </para>
/// </remarks>
public static class DemoGroupInfo
{
    /// <summary>The group as it stood at <paramref name="at"/>, counted from the plan's own people.</summary>
    public static GroupInfoSnapshot At(DemoPlan plan, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var members = plan.People.Count(p => p.JoinedGroupAt <= at && (p.LeftGroupAt is null || p.LeftGroupAt > at));

        var online = plan.Instances
            .Where(r => r.OpenedAt <= at && (r.ClosedAt is null || r.ClosedAt > at))
            .Sum(r => r.Visits.Count(v => v.Arrived <= at && (v.Left is null || v.Left > at)));

        return new GroupInfoSnapshot(
            plan.GroupName,
            "LONGPORCH",
            "4417",
            "A quiet place to sit and talk. Everyone welcome; read the rules.",
            "1. Be kind. 2. No harassment. 3. No recruiting for other groups.",
            plan.Staff[0].UserId,
            "open",
            "default",
            IsVerified: false,
            members,
            online,

            // The ids are the role names, because that is what the demo's member rows hold: a
            // demo whose roles read grol_… everywhere would be showing ids for no reason.
            [.. DemoWords.GroupRoles.Select((name, index) => new GroupRoleSnapshot(
                name,
                name,
                Description: string.Empty,
                Order: index,
                IsManagementRole: name is "Moderator" or "Admin",
                IsSelfAssignable: false,
                IsAddedOnJoin: name == "Member",
                IsDefault: name == "Member",
                Permissions: []))]);
    }
}
