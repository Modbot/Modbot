using Modbot.Companion.Instances;
using Modbot.Companion.Journal;
using Modbot.Core.Time;

namespace Modbot.Companion.Voice;

/// <summary>What kind of line is waiting to be said.</summary>
public enum AnnouncementKind
{
    /// <summary>Somebody joined the instance the moderator is in. The text is their spoken name.</summary>
    Joined,

    /// <summary>Somebody left it. The text is their spoken name.</summary>
    Left,

    /// <summary>Somebody was already in the instance when the moderator arrived. The spoken name.</summary>
    AlreadyThere,

    /// <summary>Somebody changed avatar. The whole sentence, because it names the avatar too.</summary>
    ChangedAvatar,

    /// <summary>VRChat's log stopped while the moderator was in an instance. The whole sentence.</summary>
    LogStopped,

    /// <summary>The paired server raised a flagged-join alert. The text is the whole sentence.</summary>
    FlaggedJoin,

    /// <summary>Something the moderator needs to hear about the companion itself. The whole sentence.</summary>
    Problem,

    /// <summary>The Test button. The whole sentence.</summary>
    Test,
}

/// <summary>One thing waiting to be said, and when it was queued.</summary>
public sealed record Announcement(AnnouncementKind Kind, string Text, DateTimeOffset At);

/// <summary>
/// What the voice has been asked to say and has not said yet.
/// </summary>
/// <remarks>
/// <para><strong>One line at a time, and only lines that are still news.</strong> Speech is slow —
/// a sentence takes a second or two — and events are not: twenty people can arrive in the time it
/// takes to say one name. So the queue does three things the events themselves do not. It says
/// several joins as one sentence ("five people joined your world"); it drops anything that has
/// waited longer than a few seconds, because a name spoken thirty seconds late is a name spoken
/// about the wrong moment; and it puts the lines somebody is waiting on — a problem, a flagged
/// join — ahead of the ordinary ones.</para>
/// <para>Nothing here reads anything or sends anything. It holds a few strings.</para>
/// </remarks>
public sealed class AnnouncementQueue
{
    /// <summary>
    /// How long a join or a leave may wait before it is dropped unsaid.
    /// </summary>
    /// <remarks>
    /// Long enough to ride out one sentence being spoken ahead of it; short enough that the voice
    /// is never describing an instance as it was half a minute ago.
    /// </remarks>
    public static readonly TimeSpan PresenceMaxAge = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a flagged join, a problem or a test line may wait. Longer, because each is a
    /// line somebody asked for or needs, and each is said on its own.
    /// </summary>
    public static readonly TimeSpan AlertMaxAge = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How many names are said before the sentence becomes a count. "Rin and Kai joined" is
    /// useful; "Rin, Kai, Mio, Sol and Ash joined" is a list nobody can hold.
    /// </summary>
    public const int MaxNamesSpoken = 2;

    private readonly IModbotClock _clock;
    private readonly List<Announcement> _pending = [];
    private readonly Lock _gate = new();

    public AnnouncementQueue(IModbotClock clock)
    {
        _clock = clock;
    }

    /// <summary>How many lines are waiting, stale ones included until the next take.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    public void Add(AnnouncementKind kind, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        lock (_gate)
            _pending.Add(new Announcement(kind, text, _clock.UtcNow));
    }

    public void Clear()
    {
        lock (_gate)
            _pending.Clear();
    }

    /// <summary>
    /// Takes the next sentence to say, or null when nothing worth saying is waiting.
    /// </summary>
    /// <remarks>
    /// Problems first, then flagged joins, a test line and a stopped log, then joins, leaves and
    /// people who were already there, then avatar changes. Joins are taken all together and said
    /// as one sentence, and so are leaves and "already there"; the other kinds are said one at a
    /// time, in the order they arrived.
    /// </remarks>
    public string? Next()
    {
        lock (_gate)
        {
            DropStale();

            foreach (var kind in new[] { AnnouncementKind.Problem, AnnouncementKind.FlaggedJoin, AnnouncementKind.Test, AnnouncementKind.LogStopped })
            {
                var index = _pending.FindIndex(a => a.Kind == kind);
                if (index < 0)
                    continue;

                var line = _pending[index].Text;
                _pending.RemoveAt(index);
                return line;
            }

            foreach (var (kind, presence) in new[]
                     {
                         (AnnouncementKind.Joined, PresenceKind.Joined),
                         (AnnouncementKind.Left, PresenceKind.Left),
                         (AnnouncementKind.AlreadyThere, PresenceKind.PresenceObserved),
                     })
            {
                var names = _pending.Where(a => a.Kind == kind).Select(a => a.Text).ToList();
                if (names.Count == 0)
                    continue;

                _pending.RemoveAll(a => a.Kind == kind);
                return Coalesce(presence, names);
            }

            // An avatar change names the avatar, so it is a whole sentence and cannot be folded
            // into one line the way names can.
            var avatar = _pending.FindIndex(a => a.Kind == AnnouncementKind.ChangedAvatar);
            if (avatar >= 0)
            {
                var line = _pending[avatar].Text;
                _pending.RemoveAt(avatar);
                return line;
            }

            return null;
        }
    }

    /// <summary>
    /// One sentence for one or more people doing the same thing, in the Events screen's own words.
    /// </summary>
    /// <remarks>
    /// The same person twice — somebody who dropped and came straight back — is one name, not
    /// "Rin and Rin".
    /// </remarks>
    public static string Coalesce(PresenceKind kind, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var distinct = names.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
            throw new ArgumentException("At least one name is needed.", nameof(names));

        var who = distinct.Count switch
        {
            1 => distinct[0],
            <= MaxNamesSpoken => string.Join(" and ", distinct),
            _ => $"{Number(distinct.Count)} people",
        };

        return SentJournal.Sentence(kind, who);
    }

    /// <summary>Small counts as words, so the voice says "three people" rather than reading a digit.</summary>
    private static string Number(int count) => count switch
    {
        3 => "three",
        4 => "four",
        5 => "five",
        6 => "six",
        7 => "seven",
        8 => "eight",
        9 => "nine",
        _ => count.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private void DropStale()
    {
        var now = _clock.UtcNow;
        _pending.RemoveAll(a => now - a.At > MaxAge(a.Kind));
    }

    private static TimeSpan MaxAge(AnnouncementKind kind) => kind switch
    {
        AnnouncementKind.Joined
            or AnnouncementKind.Left
            or AnnouncementKind.AlreadyThere
            or AnnouncementKind.ChangedAvatar => PresenceMaxAge,
        _ => AlertMaxAge,
    };
}
