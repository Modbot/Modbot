using Modbot.Core.Time;

namespace Modbot.Companion.Sounds;

/// <summary>What the client wants to tell the moderator about.</summary>
public enum NotificationKind
{
    /// <summary>The paired server raised a flagged-join alert for the instance the moderator is in.</summary>
    FlaggedJoin,

    /// <summary>Something about the companion itself: a server that rejected this device, so reporting has stopped.</summary>
    Problem,

    /// <summary>The Test button on the Notifications card.</summary>
    Test,
}

/// <summary>
/// Decides whether something the client noticed gets a sound.
/// </summary>
/// <remarks>
/// <para>Events are not paced like sounds. The same alert can be noticed twice — the overlay and the
/// reading half both see a rejected token — and ten people can walk in at once. Without a rule, one
/// event would be two sounds and a busy minute would be a fire alarm.</para>
/// <para>So: the same thing does not sound twice inside <see cref="SameThingWindow"/>, and nothing
/// sounds again until <see cref="QuietGap"/> has passed. A refused bleep is dropped rather than
/// held, unlike the voice's queue — a sound two seconds late says nothing the sound on time did
/// not. The Test button always sounds, because a person pressed it.</para>
/// <para>It holds a few strings and two times. Nothing is read and nothing is sent.</para>
/// </remarks>
public sealed class BleepRule
{
    /// <summary>
    /// How long the same thing stays "already heard". Long enough to cover two halves of the client
    /// noticing one alert; short enough that a person flagged twice in a session is heard twice.
    /// </summary>
    public static readonly TimeSpan SameThingWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long after a bleep nothing else bleeps. One sound for a rush of arrivals, and long
    /// enough that two sounds never run into each other.
    /// </summary>
    public static readonly TimeSpan QuietGap = TimeSpan.FromSeconds(2);

    private readonly IModbotClock _clock;
    private readonly Dictionary<string, DateTimeOffset> _heard = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private DateTimeOffset? _lastBleep;

    public BleepRule(IModbotClock clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Whether this one gets a sound, right now.
    /// </summary>
    /// <param name="about">Who or what it is about, so the same one is not heard twice. Null counts as its own thing.</param>
    public bool Ask(NotificationKind kind, string? about = null)
    {
        var now = _clock.UtcNow;

        lock (_gate)
        {
            if (kind is NotificationKind.Test)
            {
                _lastBleep = now;
                return true;
            }

            if (_lastBleep is { } last && now - last < QuietGap)
                return false;

            Forget(now);

            var what = $"{kind}:{about}";
            if (_heard.TryGetValue(what, out var when) && now - when < SameThingWindow)
                return false;

            _heard[what] = now;
            _lastBleep = now;
            return true;
        }
    }

    /// <summary>Drops what has gone quiet, so a long session does not keep every name it ever heard.</summary>
    private void Forget(DateTimeOffset now)
    {
        if (_heard.Count == 0)
            return;

        foreach (var (what, when) in _heard.Where(h => now - h.Value >= SameThingWindow).ToList())
            _heard.Remove(what);
    }
}
