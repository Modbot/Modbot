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
/// <para><strong>It moves like every other panel</strong> (user, 2026-10-17; it used to be a locked
/// fixture). In edit mode it has the same bar, Lock and the hand, and a grip picks it up. Its place
/// setting, <see cref="ButtonPlace"/>, picks the spot it starts from, and wherever it is left is kept
/// for that place (<see cref="ButtonSpots"/>). Two quick grips send it back to that place's own
/// spot. The stick shortcut that does the same as a click is the other setting
/// (<see cref="ButtonShortcut"/>). The spot is the button's own rather than the panel's, so the two
/// cannot be set to cover each other by accident: the panel sits low and to the right by default, the
/// button low and to the left.</para>
/// </remarks>
public static class OverlayButton
{
    /// <summary>The words under the label while the shortcut is not changed: <see cref="ButtonShortcut.Default"/> in words.</summary>
    public const string DefaultShortcut = "Right stick back 5s";

    /// <summary>The button's card, in pixels: the face with the eye and the words. Square.</summary>
    public const int CardPixels = 256;

    /// <summary>
    /// The clear ground around the card on every side, in pixels. The bar takes the strip under the
    /// card (edit mode only) and the card stays in the middle of the picture, so the card has the
    /// same size and the same spot whether the bar is there or not. Room for the bar at the narrowest
    /// the button can be, where a metre is the most pixels.
    /// </summary>
    public const int EdgePixels = 96;

    /// <summary>The button's texture, in pixels. Square, like every panel: the card with its clear ground around it.</summary>
    public const int PanelPixels = CardPixels + (EdgePixels * 2);

    /// <summary>How many times wider and taller the texture's square is than the card.</summary>
    public const float CanvasScale = PanelPixels / (float)CardPixels;

    /// <summary>
    /// A placement as the room draws it. <see cref="OverlayPlacement.Width"/> is the card's, as it is
    /// saved and as a controller sizes it; the picture is <see cref="CanvasScale"/> times as wide and
    /// as tall, in the middle of which the card stands, so the card keeps its width in metres and its
    /// spot. The square is stretched with <see cref="OverlayPlacement.Wide"/> and
    /// <see cref="OverlayPlacement.Tall"/>, as a panel made bigger is.
    /// </summary>
    public static OverlayPlacement Drawn(OverlayPlacement card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card with { Wide = CanvasScale, Tall = CanvasScale };
    }

    /// <summary>The card's own placement, as it is saved, from the one the room draws (<see cref="Drawn"/>).</summary>
    public static OverlayPlacement Card(OverlayPlacement drawn)
    {
        ArgumentNullException.ThrowIfNull(drawn);
        return drawn with { Wide = 1f, Tall = 1f };
    }

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
    /// How far left of the middle of the view the corner button starts, in the same measure the
    /// pop-ups use (<see cref="NotifyOverlaySettings.AcrossFraction"/>, 0.40). The button's own
    /// number rather than theirs, so moving one does not move the other. A first guess, 2026-10-17,
    /// to be tried in the headset.
    /// </summary>
    public const float CornerAcrossFraction = 0.48f;

    /// <summary>
    /// How far below the middle of the view the corner button starts; see
    /// <see cref="CornerAcrossFraction"/> (the pop-ups' own is 0.26).
    /// </summary>
    public const float CornerDownFraction = 0.34f;

    /// <summary>
    /// The spot a place starts from, and the one two quick grips send the button back to: solid,
    /// not locked and not click-through, as the other panels start. The corner is the lower left of
    /// the view; the wrist is the left one.
    /// </summary>
    public static OverlayPlacement ToPlacement(ButtonPlace place) => place is ButtonPlace.Wrist
        ? new OverlayPlacement(OverlayAnchor.LeftHand, OverlayPlacement.WristOffset, WristWidth)
        : new OverlayPlacement(
            OverlayAnchor.Head,
            new OverlayPose(
                -CornerAcrossFraction * CornerDistance,
                -CornerDownFraction * CornerDistance,
                -CornerDistance),
            CornerWidth);
}
