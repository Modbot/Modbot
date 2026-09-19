namespace Modbot.Companion.Overlay;

/// <summary>
/// Whether the headset panel runs at all, and the two things that change it.
/// </summary>
/// <remarks>
/// <para><strong>Off means nothing is built.</strong> The panel is real work — a texture, a
/// drawing loop, the controllers read thirty times a second, and a connection to SteamVR — so a
/// moderator who turns it off gets none of that, rather than a panel drawn where nobody can see
/// it. This holds the rule: start is never called while it is off, stop is never called for
/// something that was not started, and neither is called twice in a row.</para>
/// <para>The setting itself lives in <c>settings.json</c> as <c>overlayOn</c>
/// (<see cref="Presentation.CompanionSettings.OverlayOn"/>); this is what the client does about it.</para>
/// </remarks>
/// <param name="start">Builds the panel and starts its loops.</param>
/// <param name="stop">Takes all of that down again.</param>
/// <param name="on">What the settings file said, so a client that starts switched off never starts it.</param>
public sealed class OverlaySwitch(Action start, Action stop, bool on = true)
{
    private readonly Action _start = start ?? throw new ArgumentNullException(nameof(start));
    private readonly Action _stop = stop ?? throw new ArgumentNullException(nameof(stop));

    /// <summary>Whether the moderator wants the panel.</summary>
    public bool On { get; private set; } = on;

    /// <summary>Whether the panel has been started and not yet stopped.</summary>
    public bool Running { get; private set; }

    /// <summary>Starts the panel, unless it is switched off. Called once, when the client starts.</summary>
    public void StartIfOn()
    {
        if (!On || Running)
            return;

        Running = true;
        _start();
    }

    /// <summary>
    /// The switch being flipped: the panel comes up or goes down there and then, rather than at
    /// the next restart.
    /// </summary>
    public void Set(bool on)
    {
        if (On == on)
            return;

        On = on;

        if (on)
            StartIfOn();
        else if (Running)
        {
            Running = false;
            _stop();
        }
    }
}
