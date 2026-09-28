using System.Globalization;

namespace Modbot.Demo;

/// <param name="Until">Null while the person is still online.</param>
public sealed record DemoOnlineStretch(DemoPerson Person, DateTimeOffset From, DateTimeOffset? Until);

/// <summary>
/// When each member was online in VRChat, worked out from what the plan has them doing.
/// </summary>
/// <remarks>
/// <para>
/// VRChat's <c>onlineMemberCount</c> is the group's members who are online anywhere in VRChat, not the
/// ones in the group's own instances. Counting only those drew the member count chart's Online line
/// at nought for most of every day, because the group's instances run in the evenings and not every
/// evening. A group of a few hundred is never all offline, and the people in its instances are only
/// some of the ones who are on.
/// </para>
/// <para>
/// So somebody is online while the plan has them doing something in VRChat or beside it: a little
/// before walking into one of the group's instances until a little after leaving it, in the Discord
/// voice channel, around the moment a moderator acted on them or they acted on someone, and around
/// the moment they joined the group. Between those, everybody has a daily routine of their own: an
/// evening they usually come on at, in one of a few parts of the world, and a chance of coming on at
/// all that day that goes with what sort of member they are.
/// </para>
/// <para>
/// The routine is worked out from the person's id and the calendar date rather than drawn from the
/// plan's dice, the way the world head counts are (<c>DemoSeeder.WorldReadingsAsync</c>), so it is
/// the same every time and adding it moved no other number the demo makes. Only members are counted,
/// and each of them once however many reasons they have to be on, so the count is never more than
/// the member count, and not less than the people in the group's instances unless one of them left
/// the group partway through a visit.
/// </para>
/// </remarks>
public static class DemoOnline
{
    /// <summary>
    /// Every member's online stretches, joined up where they meet or overlap, and cut to the time
    /// they were in the group.
    /// </summary>
    public static List<DemoOnlineStretch> Stretches(DemoPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var visits = plan.Instances
            .SelectMany(instance => instance.Visits.Select(visit => (Instance: instance, Visit: visit)))
            .ToLookup(v => v.Visit.Person.UserId, StringComparer.Ordinal);

        var voice = DemoVoice.Sessions(plan, new Random(DemoVoice.Seed))
            .ToLookup(s => s.Person.UserId, StringComparer.Ordinal);

        var actions = plan.Actions
            .SelectMany(a => new[] { (a.Subject.UserId, a.At), (a.Moderator.UserId, a.At) })
            .ToLookup(a => a.UserId, a => a.At, StringComparer.Ordinal);

        var stretches = new List<DemoOnlineStretch>();

        foreach (var person in plan.People)
        {
            var mine = new List<(DateTimeOffset From, DateTimeOffset? Until)>();

            foreach (var (instance, visit) in visits[person.UserId])
                mine.Add(AroundVisit(person, instance, visit));

            foreach (var session in voice[person.UserId])
                mine.Add((session.Joined, session.Left));

            foreach (var at in actions[person.UserId])
                mine.Add((at.AddMinutes(-15), at.AddMinutes(15)));

            // Joining a group is something you do from inside VRChat.
            mine.Add((person.JoinedGroupAt.AddMinutes(-20), person.JoinedGroupAt.AddMinutes(25)));

            mine.AddRange(Routine(plan, person));

            stretches.AddRange(Joined(person, mine, plan.Now));
        }

        return stretches;
    }

    /// <summary>
    /// On a while before walking in, and a while after walking out: people come online, look around
    /// and then join, and linger in their home world afterwards. Somebody still in the instance is
    /// still online.
    /// </summary>
    private static (DateTimeOffset From, DateTimeOffset? Until) AroundVisit(DemoPerson person, DemoInstance instance, DemoVisit visit)
    {
        var dice = Dice($"{person.UserId}:{instance.Number}");
        var arrived = visit.Arrived > instance.OpenedAt ? visit.Arrived : instance.OpenedAt;
        var left = visit.Left is null ? instance.ClosedAt : instance.ClosedAt is null || visit.Left < instance.ClosedAt ? visit.Left : instance.ClosedAt;

        var before = 10 + Take(ref dice, 50);
        var after = Take(ref dice, 40);

        return (arrived.AddMinutes(-before), left?.AddMinutes(after));
    }

    /// <summary>Where in the world somebody's evenings fall, as the UTC hour they usually come on.</summary>
    /// <remarks>
    /// Most of them share the group's own evening, which is when its instances open; the rest are
    /// spread across the Americas, Asia and Oceania, and the odd hour, which is what keeps the
    /// group's quiet hours from being empty.
    /// </remarks>
    private static (int Hour, bool AnyHour) HomeEvening(DemoPerson person)
    {
        var dice = Dice(person.UserId + ":home");
        var where = Take(ref dice, 100);

        return where switch
        {
            < 45 => (17 + Take(ref dice, 5), false),
            < 70 => (24 + Take(ref dice, 4), false),
            < 82 => (9 + Take(ref dice, 4), false),
            _ => (Take(ref dice, 24), true),
        };
    }

    /// <summary>Out of a hundred, the chance somebody comes on at all on a given day.</summary>
    private static int ChanceOfComingOn(DemoPersonKind kind) => kind switch
    {
        DemoPersonKind.Staff => 70,
        DemoPersonKind.Regular => 45,
        DemoPersonKind.Newcomer => 40,
        DemoPersonKind.Offender => 30,
        _ => 25,
    };

    /// <summary>
    /// One session a day on the days they come on, starting around their usual hour. Fridays and
    /// Saturdays are busier, as they are for the group's instances. A few sessions run long, and most
    /// of the ones at odd hours do: somebody who left VRChat running.
    /// </summary>
    private static IEnumerable<(DateTimeOffset From, DateTimeOffset? Until)> Routine(DemoPlan plan, DemoPerson person)
    {
        var (hour, anyHour) = HomeEvening(person);
        var chance = ChanceOfComingOn(person.Kind);

        var first = plan.Now.AddDays(-DemoPlan.DaysOfHistory - 1).UtcDateTime.Date;
        var last = plan.Now.UtcDateTime.Date;

        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var dice = Dice(person.UserId + ":" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var busy = day.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday;

            if (Take(ref dice, 100) >= chance + (busy ? 10 : 0))
                continue;

            var from = new DateTimeOffset(day, TimeSpan.Zero).AddHours(hour).AddMinutes(Take(ref dice, 240) - 120);

            var minutes = Take(ref dice, 100) < (anyHour ? 70 : 25)
                ? 240 + Take(ref dice, 300)
                : 45 + Take(ref dice, 195);

            yield return (from, from.AddMinutes(minutes));
        }
    }

    /// <summary>
    /// One person's stretches cut to their time in the group and to the present moment, then joined
    /// up, so somebody with two reasons to be on at once is counted once.
    /// </summary>
    private static IEnumerable<DemoOnlineStretch> Joined(DemoPerson person, List<(DateTimeOffset From, DateTimeOffset? Until)> stretches, DateTimeOffset now)
    {
        var cut = stretches
            .Select(s => (
                From: s.From > person.JoinedGroupAt ? s.From : person.JoinedGroupAt,
                Until: Earlier(s.Until, person.LeftGroupAt)))
            .Where(s => s.From <= now && (s.Until is null || s.Until > s.From))
            .OrderBy(s => s.From)
            .ToList();

        DateTimeOffset? from = null, until = null;
        var open = false;

        foreach (var s in cut)
        {
            if (open && (until is null || s.From <= until))
            {
                until = until is null || s.Until is null ? null : s.Until > until ? s.Until : until;
                continue;
            }

            if (open)
                yield return new DemoOnlineStretch(person, from!.Value, until);

            (from, until, open) = (s.From, s.Until, true);
        }

        if (open)
            yield return new DemoOnlineStretch(person, from!.Value, until);
    }

    private static DateTimeOffset? Earlier(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : a < b ? a : b;

    /// <summary>
    /// A handful of dice worked out from <paramref name="text"/>, the same every run, unlike
    /// <see cref="string.GetHashCode()"/>.
    /// </summary>
    /// <remarks>
    /// The last few steps stir the bits. A running hash on its own leaves "…:2026-03-05" and
    /// "…:2026-03-06" close together, and a person's days would come out in step with each other.
    /// </remarks>
    private static ulong Dice(string text)
    {
        var hash = 14695981039346656037UL;

        foreach (var c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        hash ^= hash >> 30;
        hash *= 0xBF58476D1CE4E5B9UL;
        hash ^= hash >> 27;
        hash *= 0x94D049BB133111EBUL;
        hash ^= hash >> 31;

        return hash;
    }

    /// <summary>A number from 0 to <paramref name="sides"/> - 1, using up that much of the dice.</summary>
    private static int Take(ref ulong dice, int sides)
    {
        var value = (int)(dice % (ulong)sides);
        dice /= (ulong)sides;
        return value;
    }
}
