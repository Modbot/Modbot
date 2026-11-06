using System.Text.Json.Nodes;
using Modbot.Companion.Overlay;

namespace Modbot.Companion.Presentation;

/// <summary>
/// Where the chat window on a monitor was last left and how big it was made, saved as the
/// <c>desktopChatSpot</c> object in <c>settings.json</c>
/// (<see cref="CompanionSettings.SaveDesktopChatSpot"/>), which leaves every other field alone.
/// </summary>
/// <remarks>
/// The size is held between one and two times the default, the same bounds the headset's chat
/// panel keeps (<see cref="OverlayPlacement.MinShape"/> and <see cref="OverlayPlacement.MaxShape"/>),
/// so a hand-edited file cannot make a window too small to read or too big to reach its corner.
/// </remarks>
/// <param name="Left">The window's left edge, in screen pixels.</param>
/// <param name="Top">The window's top edge, in screen pixels.</param>
/// <param name="Width">The window's width, from <see cref="DefaultWidth"/> to twice that.</param>
/// <param name="Height">The window's height, from <see cref="DefaultHeight"/> to twice that.</param>
public sealed record DesktopChatSpot(int Left, int Top, double Width, double Height)
{
    /// <summary>The width a fresh window opens at.</summary>
    public const double DefaultWidth = 420;

    /// <summary>The height a fresh window opens at.</summary>
    public const double DefaultHeight = 520;

    /// <summary>The width, brought inside one to two times the default.</summary>
    public static double ClampWidth(double width)
        => Clamp(width, DefaultWidth);

    /// <summary>The height, brought inside one to two times the default.</summary>
    public static double ClampHeight(double height)
        => Clamp(height, DefaultHeight);

    /// <summary>This spot with its size brought inside the bounds.</summary>
    public DesktopChatSpot Clamped() => this with { Width = ClampWidth(Width), Height = ClampHeight(Height) };

    /// <summary>The <c>desktopChatSpot</c> object as it is written to the file.</summary>
    public JsonObject ToJson()
    {
        var spot = Clamped();

        return new JsonObject
        {
            ["left"] = (double)spot.Left,
            ["top"] = (double)spot.Top,
            ["width"] = spot.Width,
            ["height"] = spot.Height,
        };
    }

    /// <summary>
    /// Reads the <c>desktopChatSpot</c> object. Null when there is none or its position is
    /// unreadable (the window then opens at its own default spot); a missing or unreadable size
    /// takes the default size, and a size outside its bounds is brought inside them.
    /// </summary>
    public static DesktopChatSpot? FromJson(JsonNode? node)
    {
        if (node is not JsonObject json || Number(json, "left") is not { } left || Number(json, "top") is not { } top)
            return null;

        return new DesktopChatSpot(
            (int)Math.Clamp(left, int.MinValue, int.MaxValue),
            (int)Math.Clamp(top, int.MinValue, int.MaxValue),
            Number(json, "width") ?? DefaultWidth,
            Number(json, "height") ?? DefaultHeight).Clamped();
    }

    private static double Clamp(double value, double baseline)
        => double.IsFinite(value)
            ? Math.Clamp(value, baseline * OverlayPlacement.MinShape, baseline * OverlayPlacement.MaxShape)
            : baseline;

    private static double? Number(JsonObject json, string field)
        => json[field] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number)
            ? number
            : null;
}
