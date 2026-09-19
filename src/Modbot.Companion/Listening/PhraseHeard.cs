using Modbot.Core.Time;

namespace Modbot.Companion.Listening;

/// <summary>
/// Decides whether a phrase the engine matched is a fresh one or the same one again.
/// </summary>
/// <remarks>
/// <para>One sentence out of a person's mouth is not one match out of the engine. "Modbot, clip
/// that" carries the pieces of "Modbot, clip this" inside it, the engine is offered the same run of
/// sound more than once as it catches up, and a moderator who is not sure it heard them says it
/// again. Without a rule, one request would be two or three clips, each a few seconds shorter than
/// the last.</para>
/// <para>So nothing fires again until <see cref="QuietGap"/> has passed, whichever phrase it
/// was. The gap is long enough to cover a person saying it twice and short enough that two real
/// moments half a minute apart are two clips. A refused match is dropped rather than held: a clip
/// saved five seconds late is not the clip that was asked for.</para>
/// <para>It holds one time. Nothing is read and nothing is sent.</para>
/// </remarks>
public sealed class PhraseHeard
{
    /// <summary>
    /// How long after a phrase fires nothing else does.
    /// </summary>
    /// <remarks>
    /// Six seconds: longer than the couple of seconds the engine can take to offer the same
    /// utterance again, longer than a person repeating themselves once, and well short of the two
    /// to five minutes a clip covers, so a second real moment is never swallowed.
    /// </remarks>
    public static readonly TimeSpan QuietGap = TimeSpan.FromSeconds(6);

    private readonly IModbotClock _clock;
    private readonly Lock _gate = new();

    private DateTimeOffset? _lastFired;

    public PhraseHeard(IModbotClock clock)
    {
        _clock = clock;
    }

    /// <summary>Whether this match is acted on, right now.</summary>
    public bool Ask()
    {
        var now = _clock.UtcNow;

        lock (_gate)
        {
            if (_lastFired is { } last && now - last < QuietGap)
                return false;

            _lastFired = now;
            return true;
        }
    }

    /// <summary>Forgets that anything was heard, so the next match fires. Used when listening stops.</summary>
    public void Forget()
    {
        lock (_gate)
            _lastFired = null;
    }
}
