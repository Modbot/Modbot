using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Core.Live;

namespace Modbot.Api.Features.Places;

/// <summary>
/// One person's stays in one instance, worked out from every moderator's reports and mending the
/// stretch a moderator's own client was gone.
/// </summary>
/// <remarks>
/// <para>
/// The rule is the one <see cref="PresenceCounts"/> has always used -- an arrival makes a person
/// present, a leave makes them absent, a repeat changes nothing, and a stay nobody saw end closes at
/// the last report from the instance -- plus one thing. A moderator who crashed (or whose log
/// stopped) cannot see anybody leave while they are gone. When their client comes back it states who
/// is in the instance, and everyone it had listed who is not in that statement left some time in the
/// gap. Nobody knows when, so they count as having left at the start of the gap, the last moment this
/// moderator vouched for them. Without this a person who left during the gap stays present until the
/// instance's last report, and the head count never drops.
/// </para>
/// <para>
/// <strong>Another moderator who kept watching overrules the drop.</strong> A person counts as present
/// for as long as any moderator saw them, so the drop only happens when no other moderator's client
/// was vouching for them at the start of the gap.
/// </para>
/// <para>
/// A moderator's client states who is present as a run of "already here" reports, each within
/// <see cref="InstanceWatching.ArrivalBurstAllowance"/> of the one before it (a roster of forty-odd
/// can take longer than that in all), and then its own join. That run is what marks the start of a
/// new stint. The stint also starts at the first report after the client said its log stopped, since
/// a client that comes back to an empty instance states nobody at all.
/// </para>
/// <para>
/// <strong>A restatement drops only the people no other moderator reported alongside it.</strong>
/// When two moderators restate together, the fact writer keeps only the first report of each person
/// within its dedup window and throws the second moderator's copy away, so that moderator's
/// restatement reaches the database as a few stragglers. Someone another moderator reported within
/// that window of the restatement is therefore still here, and stays on this moderator's list.
/// </para>
/// <para>
/// Pure: no database and no clock, so the rule can be read and tested on its own.
/// </para>
/// </remarks>
public static class CompanionGaps
{
    /// <summary>
    /// Every stay in the instance, oldest first, with whether the arrival and the leave were seen.
    /// </summary>
    /// <param name="reports">
    /// The instance's presence reports and log-stopped reports, from any moderator, in any order.
    /// </param>
    /// <param name="dedupWindow">
    /// How close together the fact writer treats two reports of the same person as one; the default is
    /// its default (<see cref="FactWriter.DefaultWindowSeconds"/>).
    /// </param>
    public static IReadOnlyList<PresenceStay> Stays(
        IReadOnlyList<PresenceReport> reports,
        TimeSpan? dedupWindow = null)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var window = dedupWindow ?? TimeSpan.FromSeconds(FactWriter.DefaultWindowSeconds);

        var real = reports.Where(IsAboutAPerson).ToList();

        if (real.Count == 0)
            return [];

        // Every "here" report by person, so a restatement can ask who else reported someone near it.
        var reported = real
            .Where(r => r.Type is FactType.InstanceJoined or FactType.InstancePresenceObserved)
            .GroupBy(r => r.SubjectId!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => (r.At, Device: r.Device ?? "")).ToList(),
                StringComparer.Ordinal);

        var perDevice = reports
            .Where(r => r.Type is FactType.InstanceLogStopped || IsAboutAPerson(r))
            .GroupBy(r => r.Device ?? "", StringComparer.Ordinal)
            .Select(g => Watch(g.Key, g.OrderBy(r => r.At).ThenBy(r => r.Id).ToList(), reported, window))
            .ToList();

        var events = new List<Step>(real.Count);
        foreach (var r in real)
            events.Add(new Step(r.At, r.Id, false, r.SubjectId!, r.Type, r.Type != FactType.InstanceLeft));

        foreach (var (watch, index) in perDevice.Select((w, i) => (w, i)))
        {
            foreach (var drop in watch.Drops)
            {
                var vouched = perDevice
                    .Where((_, position) => position != index)
                    .Any(others => others.Vouched.TryGetValue(drop.UserId, out var spans)
                        && spans.Any(s => s.From <= drop.At && drop.At < s.Until));

                if (!vouched)
                    events.Add(new Step(drop.At, drop.AfterId, true, drop.UserId, FactType.InstanceLeft, false));
            }
        }

        events.Sort();

        var lastReport = real.Max(r => r.At);
        var open = new Dictionary<string, (DateTimeOffset Started, bool ArrivalSeen)>(StringComparer.Ordinal);
        var stays = new List<PresenceStay>();

        foreach (var step in events)
        {
            var here = open.TryGetValue(step.UserId, out var current);

            if (step.Present && !here)
            {
                open[step.UserId] = (step.At, step.Type == FactType.InstanceJoined);
            }
            else if (!step.Present && here)
            {
                stays.Add(new PresenceStay(
                    step.UserId, current.Started, step.At, current.ArrivalSeen, LeaveSeen: !step.Synthetic));
                open.Remove(step.UserId);
            }
        }

        foreach (var (userId, current) in open)
            stays.Add(new PresenceStay(userId, current.Started, lastReport, current.ArrivalSeen, LeaveSeen: false));

        stays.Sort((a, b) =>
        {
            var byStart = a.Started.CompareTo(b.Started);
            if (byStart != 0)
                return byStart;

            var byEnd = a.Ended.CompareTo(b.Ended);
            return byEnd != 0 ? byEnd : string.CompareOrdinal(a.UserId, b.UserId);
        });

        return stays;
    }

    private static bool IsAboutAPerson(PresenceReport report)
        => report.Type is FactType.InstanceJoined or FactType.InstancePresenceObserved or FactType.InstanceLeft
            && report.SubjectId is { Length: > 0 };

    /// <summary>One moderator's client, start to finish: who it vouched for, and who it lost track of.</summary>
    private static DeviceWatch Watch(
        string device,
        List<PresenceReport> reports,
        Dictionary<string, List<(DateTimeOffset At, string Device)>> reported,
        TimeSpan window)
    {
        var allowance = InstanceWatching.ArrivalBurstAllowance;
        var roster = new HashSet<string>(StringComparer.Ordinal);
        var open = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var vouched = new Dictionary<string, List<(DateTimeOffset From, DateTimeOffset Until)>>(StringComparer.Ordinal);
        var drops = new List<Drop>();

        void Close(string userId, DateTimeOffset at)
        {
            if (!open.Remove(userId, out var from))
                return;

            if (!vouched.TryGetValue(userId, out var spans))
                vouched[userId] = spans = [];

            spans.Add((from, at));
        }

        void CloseAll(DateTimeOffset at)
        {
            foreach (var userId in open.Keys.ToList())
                Close(userId, at);
        }

        PresenceReport? previous = null;
        PresenceReport? stopped = null;
        var index = 0;

        while (index < reports.Count)
        {
            var report = reports[index];

            if (report.Type == FactType.InstanceLogStopped)
            {
                CloseAll(report.At);
                stopped = report;
                previous = report;
                index++;
                continue;
            }

            var startsStint = report.Type == FactType.InstancePresenceObserved
                || (stopped is not null && report.Type == FactType.InstanceJoined);

            if (!startsStint)
            {
                Apply(report);
                previous = report;
                index++;
                continue;
            }

            var burst = new List<PresenceReport> { report };
            var next = index + 1;

            if (report.Type == FactType.InstancePresenceObserved)
            {
                // Each report is timed from the one before it, not from the first: a big roster is
                // logged over longer than the allowance in all.
                while (next < reports.Count
                    && reports[next].Type == FactType.InstancePresenceObserved
                    && reports[next].At - burst[^1].At <= allowance)
                {
                    burst.Add(reports[next]);
                    next++;
                }

                if (next < reports.Count
                    && reports[next].Type == FactType.InstanceJoined
                    && reports[next].At - burst[^1].At <= allowance)
                {
                    burst.Add(reports[next]);
                    next++;
                }
            }

            var gap = stopped ?? previous;
            var inBurst = burst.Select(r => r.SubjectId!).ToHashSet(StringComparer.Ordinal);

            var keptByOthers = new List<string>();

            if (gap is { } start)
            {
                var from = burst[0].At - window;
                var to = burst[^1].At + window;

                foreach (var userId in roster.Where(u => !inBurst.Contains(u)).Order(StringComparer.Ordinal))
                {
                    // Reported by another moderator around this restatement: this one's copy was
                    // most likely thrown away as a duplicate, not left out.
                    if (reported.TryGetValue(userId, out var by)
                        && by.Any(b => b.Device != device && b.At >= from && b.At <= to))
                        keptByOthers.Add(userId);
                    else
                        drops.Add(new Drop(userId, start.At, start.Id));
                }

                CloseAll(start.At);
            }

            roster.Clear();
            foreach (var member in burst)
            {
                roster.Add(member.SubjectId!);
                open.TryAdd(member.SubjectId!, member.At);
            }

            foreach (var userId in keptByOthers)
            {
                roster.Add(userId);
                open.TryAdd(userId, burst[0].At);
            }

            previous = burst[^1];
            stopped = null;
            index = next;
        }

        if (previous is { } last)
            CloseAll(last.At);

        return new DeviceWatch(drops, vouched);

        void Apply(PresenceReport report)
        {
            var userId = report.SubjectId!;

            if (report.Type == FactType.InstanceLeft)
            {
                roster.Remove(userId);
                Close(userId, report.At);
            }
            else
            {
                roster.Add(userId);
                open.TryAdd(userId, report.At);
            }
        }
    }

    private sealed record Drop(string UserId, DateTimeOffset At, long AfterId);

    private sealed record DeviceWatch(
        List<Drop> Drops,
        Dictionary<string, List<(DateTimeOffset From, DateTimeOffset Until)>> Vouched);

    /// <summary>One change of a person's state, real or inferred, in the order they happened.</summary>
    private readonly record struct Step(
        DateTimeOffset At,
        long Id,
        bool Synthetic,
        string UserId,
        string Type,
        bool Present) : IComparable<Step>
    {
        public int CompareTo(Step other)
        {
            var byTime = At.CompareTo(other.At);
            if (byTime != 0)
                return byTime;

            var byId = Id.CompareTo(other.Id);
            if (byId != 0)
                return byId;

            var byKind = Synthetic.CompareTo(other.Synthetic);
            return byKind != 0 ? byKind : string.CompareOrdinal(UserId, other.UserId);
        }
    }
}

/// <summary>One report about who is in an instance, or that a moderator's log stopped.</summary>
/// <param name="Device">The moderator's client that sent it, or null when the fact names none.</param>
public readonly record struct PresenceReport(long Id, string Type, string? SubjectId, DateTimeOffset At, string? Device);

/// <summary>One person present from <paramref name="Started"/> until <paramref name="Ended"/>.</summary>
/// <param name="ArrivalSeen">A moderator saw them walk in, as opposed to finding them already there.</param>
/// <param name="LeaveSeen">A moderator saw them walk out, as opposed to the stay being cut off.</param>
public readonly record struct PresenceStay(
    string UserId,
    DateTimeOffset Started,
    DateTimeOffset Ended,
    bool ArrivalSeen,
    bool LeaveSeen);
