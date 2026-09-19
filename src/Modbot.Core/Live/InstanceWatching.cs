using Modbot.Core.Data.Entities;

namespace Modbot.Core.Live;

/// <summary>One presence fact, cut down to what working out an instance's people needs.</summary>
/// <param name="DeviceId">
/// Which paired client reported it, read from the fact's payload. Null for a fact no client
/// reported, which can never start a watch.
/// </param>
public sealed record PresenceMark(
    string Type,
    string SubjectId,
    DateTimeOffset At,
    string? WorldId,
    string? InstanceId,
    Guid? DeviceId = null,
    string? DisplayName = null);

/// <summary>
/// Which instance: VRChat's instance number, and the world when it is known.
/// </summary>
/// <remarks>
/// The world is optional because the overlay asks by instance number alone. A number is only
/// unique within a world, so a caller that knows the world should always give it.
/// </remarks>
public readonly record struct InstanceKey(string? WorldId, string InstanceId)
{
    public bool Holds(PresenceMark mark) =>
        string.Equals(mark.InstanceId, InstanceId, StringComparison.Ordinal)
        && (WorldId is null || string.Equals(mark.WorldId, WorldId, StringComparison.Ordinal));
}

/// <summary>A moderator whose client is in the instance right now.</summary>
/// <param name="Since">When this stretch of watching started.</param>
public sealed record Watcher(string UserId, string? DisplayName, DateTimeOffset Since);

/// <summary>Somebody in an instance.</summary>
/// <param name="Since">
/// When <paramref name="SeenArriving"/> is true, the moment they arrived. When false, they were
/// already there at this moment and arrived at some earlier time nobody saw.
/// </param>
public sealed record PersonHere(string UserId, string? DisplayName, DateTimeOffset Since, bool SeenArriving);

/// <summary>Who is watching an instance, who is in it, and who was in it when watching last stopped.</summary>
/// <param name="Here">Everyone present now. Empty whenever nobody is watching.</param>
/// <param name="LastWatchedAt">When the last moderator stopped watching. Null while somebody is.</param>
/// <param name="LastSeen">Who was there at <paramref name="LastWatchedAt"/>. Not "here now".</param>
public sealed record InstancePeople(
    IReadOnlyList<Watcher> Watching,
    IReadOnlyList<PersonHere> Here,
    DateTimeOffset? LastWatchedAt,
    IReadOnlyList<PersonHere> LastSeen)
{
    public static InstancePeople NeverWatched { get; } = new([], [], null, []);

    public bool IsWatched => Watching.Count > 0;
}

/// <summary>
/// Works out, from presence facts alone, whether a moderator is watching an instance and who is in it.
/// </summary>
/// <remarks>
/// <para><strong>Why "watching" exists.</strong> A client only reports people while its moderator
/// is in the instance. When the last moderator leaves, nobody is told that anyone else left -- VRChat
/// logs a leave for everyone still there, and the client rightly drops those as not real. A roster
/// built as "last fact per person wins" therefore kept everyone the last moderator saw "present"
/// for as long as it looked back, long after the instance had closed. So a roster is only believed
/// while somebody is watching, and only from facts reported during the current watch.</para>
/// <para><strong>When a watch starts.</strong> When a paired client reports anything at all in this
/// instance. A client only ever reports what its own moderator's VRChat log shows, and that log only
/// shows the instance they are standing in, so a fact here from a client <em>is</em> that client
/// being here. The watch is credited to the moderator the client belongs to.</para>
/// <para><strong>Why it is the client and not the subject.</strong> Until 2026-09-19 a watch needed
/// a fact whose subject was the reporting moderator themselves -- their own arrival or "already
/// here". That fact is sent exactly once per stay, in the arrival burst, and a client that starts
/// while VRChat is already in an instance never sends it: the burst is in the part of the log it
/// replays to learn where it is, and replayed lines are read but not reported. Such a client then
/// reports every later arrival and departure from inside the instance while the server holds no
/// fact about the moderator at all, and the instance read as unwatched with its own moderators
/// standing in it -- head count eight, "nobody watching". Watching answers "can the people list be
/// trusted", and the answer turns on a client reporting from inside the instance, not on whose
/// account the reports happen to be about. The team analytics coverage query has counted cover this
/// way since it was written.</para>
/// <para><strong>When it ends</strong>, whichever comes first: that client reporting its own
/// moderator's leave; that client saying VRChat's log stopped; that client reporting from a
/// different instance; the moderator's presence turning up in a different instance; the instance
/// closing.</para>
/// <para><strong>Overlapping watches are one watch.</strong> Moderator A arrives at eight, B at
/// half past, A leaves at nine: the instance has been watched without a break since eight, and the
/// roster is built from everything reported since eight.</para>
/// <para><strong>Nothing is invented.</strong> Somebody first seen in a watch's arrival burst is
/// "here before" that moment, never "arrived" at it. The facts are read, not rewritten, and
/// nothing here changes how the analytics count time.</para>
/// <para>Pure: no database and no clock, so the rule can be read and tested on its own.</para>
/// </remarks>
public static class InstanceWatching
{
    /// <summary>
    /// How far before a watch's start a fact may be and still count towards it.
    /// </summary>
    /// <remarks>
    /// VRChat logs everyone already present a moment <em>before</em> the moderator's own join, and
    /// its timestamps are whole seconds, so a burst can straddle a second. Ten seconds covers that
    /// with room to spare and is far too short to reach back into an earlier watch's facts.
    /// </remarks>
    public static readonly TimeSpan ArrivalBurstAllowance = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The moderators whose clients reported something in this instance -- the only people whose
    /// facts elsewhere can matter. Callers use it to decide what else to load.
    /// </summary>
    public static IReadOnlyList<string> PossibleWatchers(
        InstanceKey instance,
        IEnumerable<PresenceMark> marks,
        IReadOnlyDictionary<Guid, string> deviceOwners)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(deviceOwners);

        return ClientsHere(instance, marks, deviceOwners)
            .Select(device => deviceOwners[device])
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The paired clients that reported anything in this instance, in the order they first did.
    /// </summary>
    /// <remarks>
    /// A client Modbot cannot name an owner for is left out: there would be nobody to credit the
    /// watch to, and nothing to compare a leave against. Pairing requires a linked VRChat account,
    /// so in practice every live client has one.
    /// </remarks>
    private static List<Guid> ClientsHere(
        InstanceKey instance,
        IEnumerable<PresenceMark> marks,
        IReadOnlyDictionary<Guid, string> deviceOwners)
        => marks
            .Where(instance.Holds)
            .Select(m => m.DeviceId)
            .OfType<Guid>()
            .Where(deviceOwners.ContainsKey)
            .Distinct()
            .ToList();

    /// <param name="instance">The instance being asked about.</param>
    /// <param name="marks">
    /// Presence facts in this instance, plus any facts placing its possible watchers in other instances.
    /// Facts in other instances about anybody else are ignored.
    /// </param>
    /// <param name="deviceOwners">Device id to the VRChat account linked to whoever it was issued to.</param>
    /// <param name="closedAt">When the instance closed, or null while it is open.</param>
    public static InstancePeople Work(
        InstanceKey instance,
        IEnumerable<PresenceMark> marks,
        IReadOnlyDictionary<Guid, string> deviceOwners,
        DateTimeOffset? closedAt)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(deviceOwners);

        // Stable, so facts sharing a timestamp keep the order they were recorded in -- which is
        // the order the client saw them.
        var ordered = marks.OrderBy(m => m.At).ToList();
        var inInstance = ordered.Where(instance.Holds).ToList();

        var stretches = new List<(string UserId, DateTimeOffset Start, DateTimeOffset? End)>();

        // One stretch per client, not per moderator: a moderator running two PCs in one instance is
        // watching it twice over, and the two stretches start and end independently.
        foreach (var client in ClientsHere(instance, inInstance, deviceOwners))
        {
            var moderator = deviceOwners[client];
            DateTimeOffset? start = null;

            foreach (var mark in ordered)
            {
                if (closedAt is { } closed && mark.At > closed)
                    break;

                var reportedByThisClient = mark.DeviceId == client;
                var aboutTheModerator = string.Equals(mark.SubjectId, moderator, StringComparison.Ordinal);

                if (!reportedByThisClient && !aboutTheModerator)
                    continue;

                if (instance.Holds(mark))
                {
                    // Somebody else's client saying the moderator is here says where the moderator
                    // is, not where this client is, and only this client's own reports can tell us
                    // that.
                    if (!reportedByThisClient)
                        continue;

                    // The moderator's own leave, and the log going dark, are this client saying it
                    // can no longer see the instance. Anybody else's leave is just news from inside
                    // it, and the client is still there to report it.
                    var stops = mark.Type is FactType.InstanceLogStopped
                        || (mark.Type is FactType.InstanceLeft && aboutTheModerator);

                    if (!stops)
                    {
                        start ??= mark.At;
                        continue;
                    }
                }
                else if (!reportedByThisClient && !IsPresent(mark.Type))
                {
                    // Elsewhere: this client reporting from another instance is it having moved,
                    // and so is the moderator's presence turning up in one. Their leave somewhere
                    // else says nothing about here.
                    continue;
                }

                if (start is { } began)
                {
                    stretches.Add((moderator, began, mark.At));
                    start = null;
                }
            }

            if (start is { } stillOpen && (closedAt is null || stillOpen <= closedAt))
                stretches.Add((moderator, stillOpen, closedAt));
        }

        if (stretches.Count == 0)
            return InstancePeople.NeverWatched;

        // Overlapping stretches are one unbroken watch.
        var watches = new List<(DateTimeOffset Start, DateTimeOffset? End)>();
        foreach (var stretch in stretches.OrderBy(s => s.Start))
        {
            if (watches.Count > 0 && (watches[^1].End is not { } lastEnd || stretch.Start <= lastEnd))
            {
                var last = watches[^1];
                watches[^1] = (last.Start, last.End is { } a && stretch.End is { } b ? Later(a, b) : null);
            }
            else
            {
                watches.Add((stretch.Start, stretch.End));
            }
        }

        var current = watches[^1];
        var names = LatestNames(ordered);

        if (current.End is null)
        {
            // One line per moderator, however many of their clients are in the instance, dated at
            // the earliest of them -- a reader wants to know who is here, not how many PCs they run.
            var watching = stretches
                .Where(s => s.End is null)
                .GroupBy(s => s.UserId, StringComparer.Ordinal)
                .Select(g => new Watcher(g.Key, names.GetValueOrDefault(g.Key), g.Min(s => s.Start)))
                .OrderBy(w => w.Since)
                .ToList();

            return new InstancePeople(watching, WhoWasThere(inInstance, current.Start, until: null), null, []);
        }

        return new InstancePeople([], [], current.End, WhoWasThere(inInstance, current.Start, until: current.End));
    }

    /// <summary>
    /// Everyone present at the end of a watch, from the facts reported during it.
    /// </summary>
    /// <remarks>
    /// A person's time is the <em>first</em> fact of their current stay. If one moderator saw them
    /// arrive and another later found them already there, both facts are stored (the duplicate
    /// check matches type), and the exact arrival is the one that comes first.
    /// </remarks>
    private static List<PersonHere> WhoWasThere(List<PresenceMark> inInstance, DateTimeOffset watchStart, DateTimeOffset? until)
    {
        var from = watchStart - ArrivalBurstAllowance;
        var here = new Dictionary<string, PersonHere>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var mark in inInstance)
        {
            if (mark.At < from)
                continue;

            if (until is { } end && mark.At > end)
                break;

            if (mark.DisplayName is { Length: > 0 } name)
                names[mark.SubjectId] = name;

            switch (mark.Type)
            {
                case FactType.InstanceJoined or FactType.InstancePresenceObserved:
                    if (!here.ContainsKey(mark.SubjectId))
                        here[mark.SubjectId] = new PersonHere(mark.SubjectId, null, mark.At, mark.Type == FactType.InstanceJoined);
                    break;

                // A stopped log is the moderator's own client going dark. Whether they are still
                // standing there is no longer known, so they are not listed as here.
                case FactType.InstanceLeft or FactType.InstanceLogStopped:
                    here.Remove(mark.SubjectId);
                    break;
            }
        }

        return here.Values
            .Select(p => p with { DisplayName = names.GetValueOrDefault(p.UserId) })
            .OrderBy(p => p.Since)
            .ThenBy(p => p.DisplayName ?? p.UserId, StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<string, string> LatestNames(IEnumerable<PresenceMark> ordered)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mark in ordered)
        {
            if (mark.DisplayName is { Length: > 0 } name)
                names[mark.SubjectId] = name;
        }

        return names;
    }

    private static bool IsPresent(string type) =>
        type is FactType.InstanceJoined or FactType.InstancePresenceObserved;

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
}
