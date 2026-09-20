using Modbot.Core.Time;

namespace Modbot.Companion.Sounds;

/// <summary>
/// What the client wants to tell the moderator about. Each of these is a row on the Notifications
/// card's filter list, except <see cref="Test"/>, which is never filtered.
/// </summary>
public enum NotificationKind
{
    /// <summary>Somebody joined the instance the moderator is in.</summary>
    Joined,

    /// <summary>Somebody was already in the instance when the moderator arrived.</summary>
    AlreadyThere,

    /// <summary>Somebody left the instance the moderator is in.</summary>
    Left,

    /// <summary>Somebody in the instance changed avatar.</summary>
    ChangedAvatar,

    /// <summary>The paired server raised a flagged-join alert for the instance the moderator is in.</summary>
    FlaggedJoin,

    /// <summary>VRChat's log stopped growing, so this client can no longer see who is in the instance.</summary>
    LogStopped,

    /// <summary>Something about the companion itself: a server that rejected this device, so reporting has stopped.</summary>
    Problem,

    /// <summary>The Test button on the Notifications card. Never filtered: a person pressed it.</summary>
    Test,
}

/// <summary>
/// Decides whether something the client noticed gets a sound, and which of the five it gets.
/// </summary>
/// <remarks>
/// <para>Events are not paced like sounds. The same alert can be noticed twice — the overlay and the
/// reading half both see a rejected token — and ten people can walk in at once. Without a rule, one
/// event would be two sounds and a busy minute would be a fire alarm.</para>
/// <para>So: the same thing does not sound twice inside <see cref="SameThingWindow"/>, and nothing
/// sounds again until <see cref="QuietGap"/> has passed since the last sound <em>finished</em>. A
/// refused bleep is dropped rather than held, unlike the voice's queue — a sound two seconds late
/// says nothing the sound on time did not. The Test button always sounds, because a person pressed
/// it.</para>
/// <para><strong>Counting from the end, not the start.</strong> The gap used to be measured from
/// the moment a sound began, which was fine while every sound was 480 milliseconds. The doubled
/// alert is more than a second long, and a gap measured from its start would have let the next
/// sound begin while it was still going. Measuring from the end is one line and it holds for
/// whatever the longest sound turns out to be.</para>
/// <para><strong>Something worse may cut into the gap.</strong> Two flagged people walking in
/// together, or a server dropping while somebody flagged is arriving, are exactly the moments the
/// gap would otherwise hide — and hiding them is the opposite of what the sound is for. So a sound
/// may begin inside the gap if it is more serious than anything already heard in that gap
/// (<see cref="Tunes.Urgency"/>). That cannot run away: seriousness only goes up, there are three
/// steps, and a sound never interrupts one that is still playing — the client refuses that before
/// it ever gets here. The worst case is a chime, then the alert, then the doubled alert, then the
/// urgent one, and then silence until the gap has run.</para>
/// <para>It holds a few strings and a few times. Nothing is read and nothing is sent.</para>
/// </remarks>
public sealed class BleepRule
{
    /// <summary>
    /// How long the same thing stays "already heard". Long enough to cover two halves of the client
    /// noticing one alert; short enough that a person flagged twice in a session is heard twice.
    /// </summary>
    public static readonly TimeSpan SameThingWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long after a sound has finished nothing else sounds. One sound for a rush of arrivals,
    /// and long enough that two sounds never run into each other.
    /// </summary>
    public static readonly TimeSpan QuietGap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How close together two flagged arrivals have to be to count as more than one.
    /// </summary>
    /// <remarks>
    /// Ten seconds: the time a group takes to come through a door one after another, and long
    /// enough that a pair who arrive a few seconds apart are still heard as a pair. Much longer and
    /// two unrelated arrivals in a quiet evening would be reported as a crowd; much shorter and
    /// two people walking in together would miss each other.
    /// </remarks>
    public static readonly TimeSpan ManyFlaggedWindow = TimeSpan.FromSeconds(10);

    /// <summary>How many different flagged people inside the window count as more than one.</summary>
    public const int ManyFlagged = 2;

    private readonly IModbotClock _clock;
    private readonly Dictionary<string, DateTimeOffset> _heard = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _flagged = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private DateTimeOffset? _quietUntil;
    private int _worstInTheGap;

    public BleepRule(IModbotClock clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Which sound this one gets, right now, or null for silence.
    /// </summary>
    /// <param name="kind">What happened.</param>
    /// <param name="about">Who or what it is about, so the same one is not heard twice. Null counts as its own thing.</param>
    public Tune? Ask(NotificationKind kind, string? about = null)
    {
        var now = _clock.UtcNow;

        lock (_gate)
        {
            var tune = Tunes.For(kind);

            if (kind is NotificationKind.Test)
            {
                Sounded(now, tune);
                return tune;
            }

            // More than one flagged person inside the window is the doubled alert. Counted by
            // person rather than by arrival, so the same person noticed twice is still one of
            // them; the same-thing rule below would drop that anyway.
            if (kind is NotificationKind.FlaggedJoin)
            {
                Forget(_flagged, now, ManyFlaggedWindow);
                _flagged[about ?? ""] = now;

                if (_flagged.Count >= ManyFlagged)
                    tune = Tune.AlertTwice;
            }

            Forget(_heard, now, SameThingWindow);

            var what = $"{kind}:{about}";
            if (_heard.TryGetValue(what, out var when) && now - when < SameThingWindow)
                return null;

            // The quiet gap, which only something worse gets through, and only by being worse than
            // everything already heard inside it.
            if (_quietUntil is { } until && now < until && Tunes.Urgency(tune) <= _worstInTheGap)
                return null;

            _heard[what] = now;
            Sounded(now, tune);
            return tune;
        }
    }

    /// <summary>
    /// A sound a person asked for by pressing something. It plays whatever the rule would have
    /// said, and still sets the quiet gap for whatever comes after it.
    /// </summary>
    public void AlwaysSounds(Tune tune)
    {
        var now = _clock.UtcNow;

        lock (_gate)
        {
            Sounded(now, tune);
        }
    }

    /// <summary>Notes that a sound is being played now, and when the next one may start.</summary>
    private void Sounded(DateTimeOffset now, Tune tune)
    {
        var urgency = Tunes.Urgency(tune);

        var running = _quietUntil is { } until && now < until;
        _worstInTheGap = running ? Math.Max(_worstInTheGap, urgency) : urgency;

        // The later of the two, so that a short sound cutting in behind a long one cannot shorten
        // the silence the long one had earned.
        var ends = now + Bleep.LengthOf(tune) + QuietGap;
        _quietUntil = running && _quietUntil > ends ? _quietUntil : ends;
    }

    /// <summary>Drops what has gone quiet, so a long session does not keep every name it ever heard.</summary>
    private static void Forget(Dictionary<string, DateTimeOffset> remembered, DateTimeOffset now, TimeSpan window)
    {
        if (remembered.Count == 0)
            return;

        foreach (var (what, when) in remembered.Where(h => now - h.Value >= window).ToList())
            remembered.Remove(what);
    }
}
