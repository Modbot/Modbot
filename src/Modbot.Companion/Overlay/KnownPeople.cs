using Modbot.Core.Time;

namespace Modbot.Companion.Overlay;

/// <summary>
/// The trust rank and 18+ mark the server last said for each person in the moderator's instance,
/// kept for a few minutes after they were last seen.
/// </summary>
/// <remarks>
/// <para><strong>Why it exists.</strong> A "Left" card is made the moment VRChat's log says
/// somebody went, and by then the roster read that follows may already have dropped them. Without
/// this the card about somebody leaving would never carry the marks the card about them arriving
/// did.</para>
/// <para><strong>How long.</strong> Each roster read and live event notes everyone it names again,
/// so a person who is still here never runs out. Somebody who left is kept for
/// <see cref="Keep"/> after they were last seen, and nobody past <see cref="MostKept"/>. Walking
/// into another instance forgets everyone.</para>
/// <para><strong>Nothing here is sent or asked for.</strong> It is a copy of what the server
/// already told this client, held in memory only.</para>
/// <para>Safe to use from more than one thread: a roster read finishes off the UI thread.</para>
/// </remarks>
public sealed class KnownPeople
{
    /// <summary>How long somebody is remembered after they were last seen.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(5);

    /// <summary>At most this many people are remembered at once.</summary>
    public const int MostKept = 500;

    private readonly IModbotClock _clock;
    private readonly Dictionary<string, Seen> _known = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public KnownPeople(IModbotClock clock) => _clock = clock;

    /// <summary>How many people are remembered right now, as of the last note or look.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _known.Count;
        }
    }

    /// <summary>Remembers what the server said about one person. Nothing said is nothing kept.</summary>
    public void Note(string subjectId, PersonInfo? info)
    {
        if (info is null || string.IsNullOrEmpty(subjectId))
            return;

        lock (_gate)
        {
            var now = _clock.UtcNow;
            _known[subjectId] = new Seen(info, now);
            Trim(now);
        }
    }

    /// <summary>What was last said about this person, or null when nothing was or it is too old.</summary>
    public PersonInfo? InfoOf(string subjectId)
    {
        lock (_gate)
        {
            Trim(_clock.UtcNow);
            return _known.TryGetValue(subjectId, out var seen) ? seen.Info : null;
        }
    }

    /// <summary>Forgets everyone: another instance, other people.</summary>
    public void Forget()
    {
        lock (_gate)
            _known.Clear();
    }

    /// <summary>
    /// Lets go of everyone past their time, on every look, so nobody is held longer than
    /// <see cref="Keep"/>; then of the oldest, past <see cref="MostKept"/>. A clock set back by
    /// more than <see cref="Keep"/> counts as past their time too.
    /// </summary>
    private void Trim(DateTimeOffset now)
    {
        List<string>? old = null;
        foreach (var (id, seen) in _known)
        {
            if (now - seen.At >= Keep || seen.At - now >= Keep)
                (old ??= []).Add(id);
        }

        foreach (var id in old ?? [])
            _known.Remove(id);

        if (_known.Count <= MostKept)
            return;

        foreach (var oldest in _known.OrderBy(k => k.Value.At).Take(_known.Count - MostKept).Select(k => k.Key).ToList())
            _known.Remove(oldest);
    }

    private sealed record Seen(PersonInfo Info, DateTimeOffset At);
}
