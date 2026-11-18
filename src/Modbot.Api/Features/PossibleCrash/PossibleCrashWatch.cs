using Modbot.Analytics.Facts;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.PossibleCrash;

/// <summary>One possible crash the watch found: the burst of leaves and who to look at first.</summary>
/// <param name="Left">How many people left within the window.</param>
/// <param name="People">Who joined or changed avatar just before, newest first.</param>
public sealed record PossibleCrashFound(
    string InstanceId,
    string? WorldId,
    int Left,
    IReadOnlyList<CrashPersonDto> People);

/// <summary>
/// Counts the people who leave each instance, as the paired companions' logs report them, and says
/// when enough of them left at once to be a crash (problem user alerts design §2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Time is the fact's own.</strong> A leave is counted where the log says it happened, so
/// two companions in the same instance, or one batch sent late, land in the same window. Only a
/// leave older than <see cref="Fresh"/> on the clock is dropped, so a batch backdated by a day of
/// offline buffering cannot raise an alert about something nobody can act on.
/// </para>
/// <para>
/// <strong>One alert per crash.</strong> After it raises, the instance is quiet until the window
/// has passed the last leave counted, so the trickle of leaves after a crash, and a second
/// companion's report of the same leaves, do not raise it again. In memory, on purpose, like
/// <see cref="Modbot.Api.Features.ProblemAvatars.ProblemAvatarSeen"/>: a restart that lets one
/// repeat once is cheaper than a table.
/// </para>
/// <para>
/// <strong>The numbers are settings.</strong> They are passed in on every call, never held here,
/// because a group's capacity is its own.
/// </para>
/// </remarks>
public sealed class PossibleCrashWatch
{
    /// <summary>How old a leave may be, on the server's clock, and still count.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How much older than the window an arrival may be and still be named, as a multiple of the
    /// window: the cause is about the same length of time again before the first leave.
    /// </summary>
    public const int LookbackWindows = 2;

    /// <summary>The most people an alert names.</summary>
    public const int MostNamed = 5;

    private const int MostKept = 500;

    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// Takes the facts of one batch that were new, and returns the crashes they complete.
    /// </summary>
    /// <param name="facts">Facts written for the first time; a duplicate is somebody else's report of one already counted.</param>
    /// <param name="leaves">How many people must leave.</param>
    /// <param name="seconds">Within how many seconds.</param>
    /// <param name="now">The server's clock.</param>
    public IReadOnlyList<PossibleCrashFound> Observe(IEnumerable<FactRecord> facts, int leaves, int seconds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var window = TimeSpan.FromSeconds(seconds);
        var found = new List<PossibleCrashFound>();
        var touched = new HashSet<string>(StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (var instance in _instances.Where(p => p.Value.IsOver(now)).Select(p => p.Key).ToList())
                _instances.Remove(instance);

            foreach (var fact in facts)
            {
                if (fact.InstanceId is not { Length: > 0 } instanceId)
                    continue;

                var key = $"{fact.WorldId}\u0001{instanceId}";

                if (fact.Type == FactType.InstanceLeft)
                {
                    if (now - fact.OccurredAt > Fresh)
                        continue;

                    Get(key, fact.WorldId, instanceId).Leaves.Add(new Leave(fact.SubjectId, fact.OccurredAt));
                    touched.Add(key);
                }
                else if (fact.Type is FactType.InstanceJoined or FactType.AvatarChanged)
                {
                    if (now - fact.OccurredAt > Fresh)
                        continue;

                    var what = fact.Type == FactType.InstanceJoined ? "joined" : "changed avatar";
                    Get(key, fact.WorldId, instanceId).Arrivals.Add(
                        new Arrival(fact.SubjectId, AuditJson.Text(fact.Data, "displayName"), what, fact.OccurredAt));
                }
            }

            foreach (var key in touched)
            {
                var instance = _instances[key];

                if (instance.Find(leaves, window) is { } burst)
                    found.Add(burst);
            }
        }

        return found;
    }

    private Instance Get(string key, string? worldId, string instanceId)
    {
        if (!_instances.TryGetValue(key, out var instance))
            _instances[key] = instance = new Instance(worldId, instanceId);

        return instance;
    }

    private sealed record Leave(string SubjectId, DateTimeOffset At);

    private sealed record Arrival(string SubjectId, string? DisplayName, string What, DateTimeOffset At);

    private sealed class Instance(string? worldId, string instanceId)
    {
        public List<Leave> Leaves { get; } = [];

        public List<Arrival> Arrivals { get; } = [];

        private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;

        /// <summary>Nothing here is fresh any more, so the entry can go.</summary>
        public bool IsOver(DateTimeOffset now)
            => Leaves.All(l => now - l.At > Fresh) && Arrivals.All(a => now - a.At > Fresh) && now - _quietUntil > Fresh;

        public PossibleCrashFound? Find(int needed, TimeSpan window)
        {
            // One leave per person, earliest report: a person cannot leave twice, so a second
            // companion's report of the same leave must not count as a second person.
            var counted = Leaves
                .Where(l => l.At > _quietUntil)
                .GroupBy(l => l.SubjectId, StringComparer.Ordinal)
                .Select(g => g.MinBy(l => l.At)!)
                .OrderBy(l => l.At)
                .ToList();

            for (var start = 0; start < counted.Count; start++)
            {
                var first = counted[start].At;
                var burst = counted.Skip(start).TakeWhile(l => l.At - first <= window).ToList();

                if (burst.Count < needed)
                    continue;

                var last = burst[^1].At;
                _quietUntil = last + window;

                var people = Arrivals
                    .Where(a => a.At <= last && first - a.At <= window * LookbackWindows)
                    .GroupBy(a => a.SubjectId, StringComparer.Ordinal)
                    .Select(g => g.MaxBy(a => a.At)!)
                    .OrderByDescending(a => a.At)
                    .Take(MostNamed)
                    .Select(a => new CrashPersonDto(a.SubjectId, a.DisplayName, a.What))
                    .ToList();

                Trim();

                return new PossibleCrashFound(instanceId, worldId, burst.Count, people);
            }

            Trim();

            return null;
        }

        private void Trim()
        {
            if (Leaves.Count > MostKept)
                Leaves.RemoveRange(0, Leaves.Count - MostKept);

            if (Arrivals.Count > MostKept)
                Arrivals.RemoveRange(0, Arrivals.Count - MostKept);
        }
    }
}
