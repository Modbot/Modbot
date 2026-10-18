using System.Text.Json.Nodes;

namespace Modbot.Companion.Overlay;

/// <summary>
/// Where the show and hide button was last left, for each place: the corner and the wrist keep a
/// spot of their own, so switching the place setting starts from that place's own spot.
/// </summary>
/// <remarks>
/// <para>Null is a place nobody has moved the button in, which is that place's default
/// (<see cref="OverlayButton.ToPlacement"/>). That is also what an older settings file reads as,
/// since it has no <c>overlayButtonSpots</c> object at all.</para>
/// <para>A spot holds the whole placement, the Lock and the hand included, as the panels' own
/// settings do, so a restart finds the button as it was left in both.</para>
/// </remarks>
public sealed record ButtonSpots(OverlayPlacement? Corner = null, OverlayPlacement? Wrist = null)
{
    /// <summary>Nothing moved anywhere: both places start from their defaults.</summary>
    public static ButtonSpots None { get; } = new();

    /// <summary>The spot to start a place from: where it was left, or its default.</summary>
    public OverlayPlacement For(ButtonPlace place)
        => (place is ButtonPlace.Wrist ? Wrist : Corner) ?? OverlayButton.ToPlacement(place);

    /// <summary>The same spots with this place's spot replaced, inside the placement's bounds.</summary>
    public ButtonSpots With(ButtonPlace place, OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        return place is ButtonPlace.Wrist
            ? this with { Wrist = placement.Clamped() }
            : this with { Corner = placement.Clamped() };
    }

    /// <summary>The <c>overlayButtonSpots</c> object as it is written to the file: only the places that have a spot.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject();

        if (Corner is { } corner)
            json[OverlayButton.Written(ButtonPlace.Corner)] = corner.ToJson();

        if (Wrist is { } wrist)
            json[OverlayButton.Written(ButtonPlace.Wrist)] = wrist.ToJson();

        return json;
    }

    /// <summary>
    /// Reads the <c>overlayButtonSpots</c> object. A missing or unreadable place has no spot, so it
    /// starts from its default; a spot is read the way the panel's own is.
    /// </summary>
    public static ButtonSpots FromJson(JsonNode? node)
    {
        if (node is not JsonObject json)
            return None;

        return new ButtonSpots(
            Read(json, ButtonPlace.Corner),
            Read(json, ButtonPlace.Wrist));
    }

    private static OverlayPlacement? Read(JsonObject json, ButtonPlace place)
        => json[OverlayButton.Written(place)] is JsonObject spot ? OverlayPlacement.FromJson(spot) : null;
}
