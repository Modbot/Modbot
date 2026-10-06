namespace Modbot.Companion.Presentation;

/// <summary>What the desktop overlay's window does with the shortcut press it is working on.</summary>
public enum OverlayOpeningStep
{
    /// <summary>Come up now, in whatever look is known.</summary>
    ShowNow,

    /// <summary>Stay hidden and look at VRChat's palette again a moment later.</summary>
    WaitAndLookAgain,
}

/// <summary>
/// When the desktop overlay's window comes up, so it opens in VRChat's colours instead of in its own
/// and changing a moment later.
/// </summary>
/// <remarks>
/// <para><strong>Why there is a wait at all.</strong> The palette is looked for under the moderator's
/// own VRChat user id, and that id is only known once the client has read it from VRChat's log: a
/// moment after the client starts, and again a moment after VRChat does. A window opened in that gap
/// finds no palette, comes up in its usual look, and takes VRChat's colours at the next two-second
/// look. So while the id is not known yet, and VRChat is up to have one, the window stays hidden and
/// looks again, until the palette is found or <see cref="LongestWait"/> has passed. Then it comes
/// up in the look it has, and the two-second look still changes it later, as before.</para>
/// <para><strong>When there is nothing to wait for</strong>: the palette is found; the id is known
/// and the person has no palette selected (or Windows would not let the client look); VRChat is not
/// running; or this is not Windows.</para>
/// <para>Only the desktop overlay's window uses this. Modbot's bubble in VRChat's Esc menu row has
/// its own fixed look, takes nothing from a palette, and is shown by its own watch
/// (<c>EscapeBubbleHost</c>), which never asks this.</para>
/// <para>This is the plain half, with the time passed in, so every case is a test.</para>
/// </remarks>
public static class OverlayOpening
{
    /// <summary>The longest the window stays hidden waiting for a palette.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>How often a waiting window looks at the palette again.</summary>
    public static readonly TimeSpan LookEvery = TimeSpan.FromMilliseconds(125);

    /// <summary>Whether a wait that began <paramref name="waited"/> ago has run out.</summary>
    public static bool IsOver(TimeSpan waited) => waited >= LongestWait;

    /// <summary>
    /// What to do after a look at the palette.
    /// </summary>
    /// <param name="paletteFound">The look found VRChat's selected palette.</param>
    /// <param name="userKnown">The moderator's own VRChat user id is known, so the look was made under it.</param>
    /// <param name="vrchatIsUp">VRChat's window is there. With it gone no id is coming.</param>
    /// <param name="waited">How long this wait has run; zero at the first look.</param>
    public static OverlayOpeningStep Next(bool paletteFound, bool userKnown, bool vrchatIsUp, TimeSpan waited)
    {
        if (paletteFound || userKnown || !vrchatIsUp)
            return OverlayOpeningStep.ShowNow;

        return IsOver(waited) ? OverlayOpeningStep.ShowNow : OverlayOpeningStep.WaitAndLookAgain;
    }
}
