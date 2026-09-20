using Modbot.Core.Time;

namespace Modbot.Companion.Listening;

/// <summary>
/// The client's own name, and the few seconds after it in which it will take an instruction.
/// </summary>
/// <remarks>
/// <para><strong>Nothing is listened for but the name.</strong> While this is not waiting, the only
/// thing any sound reaches is a matcher that has been given two spellings of "Modbot" and can
/// answer nothing else. The matcher that knows the commands is handed no sound at all — not sound
/// it ignores, not sound it matches and drops: none. Saying "clip that" across a table, or "hide
/// overlay" in the middle of a sentence, reaches nothing, because there is nothing there to reach.
/// </para>
/// <para><strong>The name on its own does nothing.</strong> It starts <see cref="Window"/>, and
/// that is the whole of it. Nobody's clip is saved and no panel moves because somebody said
/// "Modbot"; if no instruction follows, the wait ends by itself and the client goes back to
/// listening for its name.</para>
/// <para><strong>One name, one instruction.</strong> <see cref="Take"/> stops the waiting as it
/// answers, so a client that has acted is not still waiting: whatever else arrives has to start
/// again with the name. <see cref="PhraseHeard"/> is the second guard, on the other side of the
/// event, and covers the same run of sound being offered twice.</para>
/// <para>It holds one time. Nothing is read, nothing is written and nothing is sent.</para>
/// </remarks>
public sealed class NameHeard
{
    /// <summary>
    /// How long after its name the client will take an instruction.
    /// </summary>
    /// <remarks>
    /// Five seconds. It has to cover the name, the beat a person leaves after it, two words said
    /// unhurriedly, the fifth of a second sound arrives in and the beat the matcher takes to catch
    /// up — which is a good deal more than the second and a half "Modbot, show overlay" takes to
    /// say. It must not cover a conversation: at ten seconds, somebody saying "hide overlay" to a
    /// person in the instance long after anybody said "Modbot" would hide a panel.
    /// </remarks>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly IModbotClock _clock;
    private readonly Lock _gate = new();

    private DateTimeOffset? _heard;

    public NameHeard(IModbotClock clock)
    {
        _clock = clock;
    }

    /// <summary>The client's name was just said: it will take an instruction for a few seconds.</summary>
    public void Heard()
    {
        lock (_gate)
            _heard = _clock.UtcNow;
    }

    /// <summary>
    /// Whether the client is waiting for an instruction right now.
    /// </summary>
    /// <remarks>
    /// Worked out from the clock every time it is asked rather than held as a flag, so the answer
    /// is never one the window has stopped being true about. That is what makes it safe for the
    /// screen to say "waiting": it cannot be stale.
    /// </remarks>
    public bool Waiting
    {
        get
        {
            var now = _clock.UtcNow;

            lock (_gate)
                return _heard is { } heard && now - heard < Window;
        }
    }

    /// <summary>
    /// An instruction arrived: whether it is acted on, and the end of the waiting either way.
    /// </summary>
    public bool Take()
    {
        var now = _clock.UtcNow;

        lock (_gate)
        {
            var take = _heard is { } heard && now - heard < Window;
            _heard = null;
            return take;
        }
    }

    /// <summary>
    /// Forgets that the name was heard, so nothing can be acted on until it is said again. Used
    /// when listening stops.
    /// </summary>
    public void Forget()
    {
        lock (_gate)
            _heard = null;
    }
}
