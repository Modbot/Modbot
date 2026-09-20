using Modbot.Core.Time;

namespace Modbot.Companion.Voice;

/// <summary>
/// When the voice engine should be let go of again.
/// </summary>
/// <remarks>
/// <para><strong>Why it is let go of at all.</strong> The voice is a 330 MB model, and loading it
/// costs the client about 400 MB of memory for as long as it is held — most of everything the
/// client uses. Measured on the pinned voice: 7.7 MB before loading, 400 MB once loaded, about 505
/// MB while a line is being made, and back to 29 MB within 20 milliseconds of letting go. The
/// memory really is given back, so it is worth giving back.</para>
/// <para><strong>Why it is kept for a while first.</strong> Loading it again costs about 650
/// milliseconds when the file is still in the machine's own file cache, and longer on a machine
/// short of memory — which is exactly the machine this is for. Making the line costs another 670
/// either way. So an announcement after a let-go takes about 1.3 seconds instead of 0.7, and doing
/// that between every two names would be worse than holding the memory.</para>
/// <para><strong>The window.</strong> Ninety seconds after the last line was said. Announcements
/// come in bursts — several people walk into an instance at once, and more follow over the next
/// minute — so the window has to be long enough that one busy instance is one load, not twenty.
/// Ninety seconds is well past the twenty the queue itself will hold an alert for, and still short
/// enough that a quiet stretch gives the memory back rather than holding it through an evening in
/// an empty instance. Getting it wrong costs one extra two-thirds of a second, once.</para>
/// <para>Nothing here reads anything, writes anything or sends anything. It holds one moment in
/// time.</para>
/// </remarks>
public sealed class VoiceUnloadRule
{
    /// <summary>How long the engine is kept after the last line, before it is let go of.</summary>
    public static readonly TimeSpan KeepLoadedFor = TimeSpan.FromSeconds(90);

    private readonly IModbotClock _clock;

    /// <summary>
    /// When the engine was last wanted, or null to let go of it as soon as nothing is being said.
    /// </summary>
    private DateTimeOffset? _lastWanted;

    public VoiceUnloadRule(IModbotClock clock)
    {
        _clock = clock;
        _lastWanted = clock.UtcNow;
    }

    /// <summary>The engine was just loaded or just said a line. Starts the window again.</summary>
    public void Used() => _lastWanted = _clock.UtcNow;

    /// <summary>
    /// Let go of it as soon as nothing is being said, without waiting out the window. What turning
    /// the voice off does: a switched-off feature holds nothing.
    /// </summary>
    public void DropWhenQuiet() => _lastWanted = null;

    /// <summary>
    /// Whether the engine should be let go of now.
    /// </summary>
    /// <param name="loaded">Whether there is an engine to let go of.</param>
    /// <param name="busy">
    /// True while a line is waiting or being said, or while the engine is still loading. Never let
    /// go of it then: the line would be lost and the load would have been for nothing.
    /// </param>
    public bool ShouldUnload(bool loaded, bool busy)
    {
        if (!loaded || busy)
            return false;

        return _lastWanted is not { } wanted || _clock.UtcNow - wanted >= KeepLoadedFor;
    }
}
