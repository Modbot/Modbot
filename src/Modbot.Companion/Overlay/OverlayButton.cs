namespace Modbot.Companion.Overlay;

/// <summary>Where the button that shows and hides the headset panel sits.</summary>
public enum ButtonPlace
{
    /// <summary>The lower left of the view, on the head. The default.</summary>
    Corner,

    /// <summary>On the left wrist, where a watch would be.</summary>
    Wrist,
}

/// <summary>
/// The small button, always in the headset, that shows or hides the headset panel: where it goes,
/// how big it is and what it says under its label.
/// </summary>
/// <remarks>
/// <para><strong>Its own overlay.</strong> Hiding the panel takes down only the panel; the button
/// stays, and so does Modbot's connection to SteamVR, which is what lets the button bring the panel
/// back.</para>
/// <para><strong>It is a fixture, not a workspace.</strong> It is always locked, so a grip cannot
/// pick it up, and its place is the one setting, <see cref="ButtonPlace"/>. It is the button's own
/// spot rather than the panel's, so the two cannot be set to cover each other by accident: the panel
/// sits low and to the right by default, the button low and to the left.</para>
/// </remarks>
public static class OverlayButton
{
    /// <summary>The words under the label while the shortcut is not changed.</summary>
    public const string DefaultShortcut = "Stick back 5s";

    /// <summary>The button's texture, in pixels. Square, like every panel.</summary>
    public const int PanelPixels = 256;

    /// <summary>How wide the button is in the corner, in metres.</summary>
    public const float CornerWidth = 0.16f;

    /// <summary>How wide it is on the wrist, in metres: the narrowest a panel may be.</summary>
    public const float WristWidth = OverlayPlacement.MinWidth;

    /// <summary>How far ahead of the eyes the corner button sits, in metres.</summary>
    public const float CornerDistance = 0.9f;

    /// <summary>The button's place in plain words, for the settings page.</summary>
    public static string Name(ButtonPlace place) => place switch
    {
        ButtonPlace.Wrist => "Wrist",
        _ => "Corner",
    };

    /// <summary>The place as it is written to <c>settings.json</c>.</summary>
    public static string Written(ButtonPlace place) => place.ToString().ToLowerInvariant();

    /// <summary>The place a settings file names. Anything missing or unknown is the corner.</summary>
    public static ButtonPlace Parse(string? text)
        => Enum.TryParse<ButtonPlace>(text?.Trim(), ignoreCase: true, out var place) && Enum.IsDefined(place)
            ? place
            : ButtonPlace.Corner;

    /// <summary>
    /// Where the button goes, as the runtime understands it: locked, and solid. The corner is the
    /// lower left at the same angles the pop-ups use for theirs; the wrist is the left one.
    /// </summary>
    public static OverlayPlacement ToPlacement(ButtonPlace place) => place is ButtonPlace.Wrist
        ? new OverlayPlacement(OverlayAnchor.LeftHand, OverlayPlacement.WristOffset, WristWidth, Locked: true)
        : new OverlayPlacement(
            OverlayAnchor.Head,
            new OverlayPose(
                -NotifyOverlaySettings.AcrossFraction * CornerDistance,
                -NotifyOverlaySettings.DownFraction * CornerDistance,
                -CornerDistance),
            CornerWidth,
            Locked: true);
}
