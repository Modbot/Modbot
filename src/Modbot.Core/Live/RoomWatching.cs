using Modbot.Core.Data.Entities;

namespace Modbot.Core.Live;

/// <summary>One presence fact, cut down to what working out a room's people needs.</summary>
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
/// Which room: VRChat's instance number, and the world when it is known.
/// </summary>
/// <remarks>
/// The world is optional because the overlay asks by instance number alone. A number is only
/// unique within a world, so a caller that knows the world should always give it.
/// </remarks>
public readonly record struct RoomKey(string? WorldId, string InstanceId)
{
    public bool Holds(PresenceMark mark) =>
        string.Equals(mark.InstanceId, InstanceId, StringComparison.Ordinal)
        && (WorldId is null || string.Equals(mark.WorldId, WorldId, StringComparison.Ordinal));
}

/// <summary>A moderator whose client is in the room right now.</summary>
/// <param name="Since">When this stretch of watching started.</param>
public sealed record Watcher(string UserId, string? DisplayName, DateTimeOffset Since);

/// <summary>Somebody in a room.</summary>
/// <param name="Since">
/// When <paramref name="SeenArriving"/> is true, the moment they arrived. When false, they were
/// already there at this moment and arrived at some earlier time nobody saw.
/// </param>
public sealed record PersonHere(string UserId, string? DisplayName, DateTimeOffset Since, bool SeenArriving);

/// <summary>Who is watching a room, who is in it, and who was in it when watching last stopped.</summary>
/// <param name="Here">Everyone present now. Empty whenever nobody is watching.</param>
/// <param name="LastWatchedAt">When the last moderator stopped watching. Null while somebody is.</param>
/// <param name="LastSeen">Who was there at <paramref name="LastWatchedAt"/>. Not "here now".</param>
public sealed record RoomPeople(
    IReadOnlyList<Watcher> Watching,
    IReadOnlyList<PersonHere> Here,
    DateTimeOffset? LastWatchedAt,
    IReadOnlyList<PersonHere> LastSeen)
{
    public static RoomPeople NeverWatched { get; } = new([], [], null, []);

    public bool IsWatched => Watching.Count > 0;
}

/// <summary>
/// Works out, from presence facts alone, whether a moderator is watching a room and who is in it.
/// </summary>
/// <remarks>
/// <para><strong>Why "watching" exists.</strong> A client only reports people while its moderator
/// is in the room. When the last moderator leaves, nobody is told that anyone else left -- VRChat
/// logs a leave for everyone still there, and the client rightly drops those as not real. A roster
/// built as "last fact per person wins" therefore kept everyone the last moderator saw "present"
/// for as long as it looked back, long after the room had closed. So a roster is only believed
/// while somebody is watching, and only from facts reported during the current watch.</para>
/// <para><strong>When a watch starts.</strong> When a moderator's own arrival or "already here"
/// reaches the server for this room -- a fact whose subject is the VRChat account linked to the
/// Modbot account the reporting device belongs to. A moderator seen by somebody else's client is
/// in the room, but is not watching it.</para>
/// <para><strong>When it ends</strong>, whichever comes first: their own leave; their client
/// saying VRChat's log stopped; their presence turning up in a different room; the room closing.
/// </para>
/// <para><strong>Overlapping watches are one watch.</strong> Moderator A arrives at eight, B at
/// half past, A leaves at nine: the room has been watched without a break since eight, and the
/// roster is built from everything reported since eight.</para>
/// <para><strong>Nothing is invented.</strong> Somebody first seen in a watch's arrival burst is
/// "here before" that moment, never "arrived" at it. The facts are read, not rewritten, and
/// nothing here changes how the analytics count time.</para>
/// <para>Pure: no database and no clock, so the rule can be read and tested on its own.</para>
/// </remarks>
public static class RoomWatching
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
    /// The moderators whose own presence is among these facts for this room -- the only people
    /// whose facts elsewhere can matter. Callers use it to decide what else to load.
    /// </summary>
    public static IReadOnlyList<string> PossibleWatchers(
        RoomKey room,
        IEnumerable<PresenceMark> marks,
        IReadOnlyDictionary<Guid, string> deviceOwners)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(deviceOwners);

        return marks
            .Where(m => room.Holds(m) && IsPresent(m.Type) && IsOwn(m, deviceOwners))
            .Select(m => m.SubjectId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <param name="room">The room being asked about.</param>
    /// <param name="marks">
    /// Presence facts in this room, plus any facts placing its possible watchers in other rooms.
    /// Facts in other rooms about anybody else are ignored.
    /// </param>
    /// <param name="deviceOwners">Device id to the VRChat account linked to whoever it was issued to.</param>
    /// <param name="closedAt">When the room closed, or null while it is open.</param>
    public static RoomPeople Work(
        RoomKey room,
        IEnumerable<PresenceMark> marks,
        IReadOnlyDictionary<Guid, string> deviceOwners,
        DateTimeOffset? closedAt)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(deviceOwners);

        // Stable, so facts sharing a timestamp keep the order they were recorded in -- which is
        // the order the client saw them.
        var ordered = marks.OrderBy(m => m.At).ToList();
        var inRoom = ordered.Where(room.Holds).ToList();

        var stretches = new List<(string UserId, DateTimeOffset Start, DateTimeOffset? End)>();

        foreach (var moderator in PossibleWatchers(room, inRoom, deviceOwners))
        {
            DateTimeOffset? start = null;

            foreach (var mark in ordered)
            {
                if (!string.Equals(mark.SubjectId, moderator, StringComparison.Ordinal))
                    continue;

                if (closedAt is { } closed && mark.At > closed)
                    break;

                var here = room.Holds(mark);

                if (here && IsPresent(mark.Type) && IsOwn(mark, deviceOwners))
                {
                    start ??= mark.At;
                    continue;
                }

                if (start is not { } began)
                    continue;

                var ends = here
                    ? mark.Type is FactType.InstanceLeft or FactType.InstanceLogStopped
                    : IsPresent(mark.Type);

                if (ends)
                {
                    stretches.Add((moderator, began, mark.At));
                    start = null;
                }
            }

            if (start is { } stillOpen && (closedAt is null || stillOpen <= closedAt))
                stretches.Add((moderator, stillOpen, closedAt));
        }

        if (stretches.Count == 0)
            return RoomPeople.NeverWatched;

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
            var watching = stretches
                .Where(s => s.End is null)
                .Select(s => new Watcher(s.UserId, names.GetValueOrDefault(s.UserId), s.Start))
                .OrderBy(w => w.Since)
                .ToList();

            return new RoomPeople(watching, WhoWasThere(inRoom, current.Start, until: null), null, []);
        }

        return new RoomPeople([], [], current.End, WhoWasThere(inRoom, current.Start, until: current.End));
    }

    /// <summary>
    /// Everyone present at the end of a watch, from the facts reported during it.
    /// </summary>
    /// <remarks>
    /// A person's time is the <em>first</em> fact of their current stay. If one moderator saw them
    /// arrive and another later found them already there, both facts are stored (the duplicate
    /// check matches type), and the exact arrival is the one that comes first.
    /// </remarks>
    private static List<PersonHere> WhoWasThere(List<PresenceMark> inRoom, DateTimeOffset watchStart, DateTimeOffset? until)
    {
        var from = watchStart - ArrivalBurstAllowance;
        var here = new Dictionary<string, PersonHere>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var mark in inRoom)
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

    private static bool IsOwn(PresenceMark mark, IReadOnlyDictionary<Guid, string> deviceOwners) =>
        mark.DeviceId is { } device
        && deviceOwners.TryGetValue(device, out var owner)
        && string.Equals(owner, mark.SubjectId, StringComparison.Ordinal);

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
}
