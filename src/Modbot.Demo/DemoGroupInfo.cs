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
/// <para>
/// The sync also keeps every poll's two counts as a reading (<c>group_member_count</c>), changed or
/// not, and the member count chart and the peaks read those. Facts and readings both come from
/// <see cref="CountsAt"/>, so a reading taken at the moment of a fact says what the fact says.
/// </para>
/// </remarks>
public static class DemoGroupInfo
{
    /// <summary>The group as it stood at <paramref name="at"/>, counted from the plan's own people.</summary>
    public static GroupInfoSnapshot At(DemoPlan plan, DateTimeOffset at) => AtEach(plan, [at]).Single().Group;

    /// <summary>
    /// The group as it stood at each of <paramref name="times"/>, which must run oldest first.
    /// </summary>
    /// <remarks>
    /// Counted in one pass (<see cref="CountsAt"/>): working out who was online reads the whole
    /// year, and doing that again for every moment asked about added about twenty seconds to every
    /// demo's seeding.
    /// </remarks>
    public static IEnumerable<(DateTimeOffset At, GroupInfoSnapshot Group)> AtEach(DemoPlan plan, IEnumerable<DateTimeOffset> times)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return CountsAt(plan, times).Select(c => (c.At, Snapshot(plan, c.Members, c.Online)));
    }

    private static GroupInfoSnapshot Snapshot(DemoPlan plan, int members, int online)
    {
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

    /// <summary>
    /// The member count and the online count at each of <paramref name="times"/>, which must run
    /// oldest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A member is counted from the moment they joined until the moment they left; somebody is
    /// online during one of their <see cref="DemoOnline.Stretches"/>, which is VRChat's meaning of
    /// online (anywhere in VRChat) rather than "in one of the group's instances".
    /// </para>
    /// <para>
    /// One pass over the joins, leaves and online stretches, in time order, rather than a count
    /// of everybody at every moment: a year of five-minute readings is a hundred thousand moments,
    /// and counting the whole plan at each of them would go through every person and every stretch a
    /// hundred thousand times.
    /// </para>
    /// </remarks>
    public static IEnumerable<(DateTimeOffset At, int Members, int Online)> CountsAt(DemoPlan plan, IEnumerable<DateTimeOffset> times)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(times);

        var members = Changes(plan.People.Select(p => (p.JoinedGroupAt, p.LeftGroupAt)));

        var online = Changes(DemoOnline.Stretches(plan).Select(s => (s.From, s.Until)));

        int memberCount = 0, onlineCount = 0, nextMember = 0, nextOnline = 0;
        DateTimeOffset? previous = null;

        foreach (var at in times)
        {
            if (at < previous)
                throw new ArgumentException("The times must run oldest first.", nameof(times));

            previous = at;

            for (; nextMember < members.Count && members[nextMember].At <= at; nextMember++)
                memberCount += members[nextMember].Change;

            for (; nextOnline < online.Count && online[nextOnline].At <= at; nextOnline++)
                onlineCount += online[nextOnline].Change;

            yield return (at, memberCount, onlineCount);
        }
    }

    /// <summary>
    /// Each stretch as one step up where it starts and one step down where it ends, oldest first. A
    /// stretch with no end never steps down; one that ends before it starts was never there.
    /// </summary>
    private static List<(DateTimeOffset At, int Change)> Changes(IEnumerable<(DateTimeOffset From, DateTimeOffset? Until)> stretches)
    {
        var changes = new List<(DateTimeOffset At, int Change)>();

        foreach (var (from, until) in stretches)
        {
            if (until <= from)
                continue;

            changes.Add((from, 1));

            if (until is { } end)
                changes.Add((end, -1));
        }

        changes.Sort((a, b) => a.At.CompareTo(b.At));
        return changes;
    }
}
