using System.Globalization;
using System.Text.Json.Nodes;

namespace Modbot.Companion.Overlay;

/// <summary>Where on the screen the notification overlay sits.</summary>
/// <remarks>
/// Six named places rather than two numbers, because "top right" is something a moderator can
/// choose without putting the headset on. The fine offset is there for the one who wants to nudge
/// it afterwards.
/// </remarks>
public enum ScreenSpot
{
    TopLeft,

    TopMiddle,

    /// <summary>The default: out of the middle of the view, where the instance is.</summary>
    TopRight,

    BottomLeft,

    BottomMiddle,

    BottomRight,
}

/// <summary>
/// The notification overlay: whether it is drawn, where on the screen it sits, how big, and how
/// long one pop-up stays.
/// </summary>
/// <remarks>
/// <para>Saved as the <c>notifyOverlay</c> object in <c>settings.json</c> and loaded at start
/// (two overlay modes design §5). The main overlay's own settings are the separate
/// <c>overlay</c> object and <c>overlayOn</c> field, and neither half can change the other.</para>
/// <para><strong>The spot becomes an ordinary placement.</strong> A named spot, a distance and a
/// fine offset turn into a head-anchored <see cref="OverlayPlacement"/> by
/// <see cref="ToPlacement"/>, so the runtime draws this panel with exactly the same machinery as
/// the main one. There is no second placement system to keep in step.</para>
/// </remarks>
/// <param name="On">Whether the notification overlay is drawn at all. Off means none of it is built.</param>
/// <param name="Spot">Which of the six screen positions.</param>
/// <param name="Across">Fine offset in metres on top of the spot; positive is right.</param>
/// <param name="Down">Fine offset in metres on top of the spot; positive is down.</param>
/// <param name="Distance">Metres ahead of the head.</param>
/// <param name="Width">Metres across. Being square, it is as tall.</param>
/// <param name="Opacity">1 is solid.</param>
/// <param name="Seconds">How long one pop-up stays before it clears itself.</param>
public sealed record NotificationSettings(
    bool On = true,
    ScreenSpot Spot = ScreenSpot.TopRight,
    float Across = 0f,
    float Down = 0f,
    float Distance = NotificationSettings.DefaultDistance,
    float Width = NotificationSettings.DefaultWidth,
    float Opacity = NotificationSettings.DefaultOpacity,
    float Seconds = NotificationSettings.DefaultSeconds)
{
    public const float DefaultDistance = 1.0f;

    public const float DefaultWidth = 0.35f;

    public const float DefaultOpacity = 0.95f;

    public const float DefaultSeconds = 6f;

    public const float MinWidth = 0.1f;

    public const float MaxWidth = 1.0f;

    public const float MinDistance = 0.3f;

    public const float MaxDistance = 3f;

    public const float MinOpacity = 0.1f;

    /// <summary>How far the fine offset may move the panel, in metres, on either axis.</summary>
    public const float MaxFine = 1f;

    public const float MinSeconds = 2f;

    public const float MaxSeconds = 30f;

    /// <summary>
    /// How far across the view a left or right spot sits, as a fraction of the distance ahead:
    /// about 22°. An angle rather than a length, so moving the panel further away keeps it in the
    /// same place in the view instead of sliding towards the middle.
    /// </summary>
    public const float AcrossFraction = 0.40f;

    /// <summary>The same for a top or bottom spot: about 15°.</summary>
    public const float DownFraction = 0.26f;

    /// <summary>At most this many pop-ups are drawn at once; older ones fall off the bottom.</summary>
    public const int MostPopUpsAtOnce = 3;

    public static NotificationSettings Default { get; } = new();

    /// <summary>The same settings with every number inside its bounds.</summary>
    public NotificationSettings Clamped() => this with
    {
        Across = Clamp(Across, -MaxFine, MaxFine),
        Down = Clamp(Down, -MaxFine, MaxFine),
        Distance = Clamp(Distance, MinDistance, MaxDistance),
        Width = Clamp(Width, MinWidth, MaxWidth),
        Opacity = Clamp(Opacity, MinOpacity, 1f),
        Seconds = Clamp(Seconds, MinSeconds, MaxSeconds),
    };

    /// <summary>How long one pop-up stays.</summary>
    public TimeSpan Dwell => TimeSpan.FromSeconds(Clamp(Seconds, MinSeconds, MaxSeconds));

    /// <summary>-1 for a left spot, 0 for a middle one, 1 for a right one.</summary>
    public static float AcrossOf(ScreenSpot spot) => spot switch
    {
        ScreenSpot.TopLeft or ScreenSpot.BottomLeft => -1f,
        ScreenSpot.TopRight or ScreenSpot.BottomRight => 1f,
        _ => 0f,
    };

    /// <summary>1 for a top spot, -1 for a bottom one.</summary>
    public static float DownOf(ScreenSpot spot) => spot switch
    {
        ScreenSpot.BottomLeft or ScreenSpot.BottomMiddle or ScreenSpot.BottomRight => -1f,
        _ => 1f,
    };

    /// <summary>The spot in plain words, for the settings page.</summary>
    public static string Name(ScreenSpot spot) => spot switch
    {
        ScreenSpot.TopLeft => "Top left",
        ScreenSpot.TopMiddle => "Top middle",
        ScreenSpot.TopRight => "Top right",
        ScreenSpot.BottomLeft => "Bottom left",
        ScreenSpot.BottomMiddle => "Bottom middle",
        _ => "Bottom right",
    };

    /// <summary>
    /// Where the panel goes, as the runtime understands it: fixed to the head, at the spot's
    /// angle from straight ahead, with the fine offset on top.
    /// </summary>
    public OverlayPlacement ToPlacement()
    {
        var settings = Clamped();

        var x = (AcrossOf(settings.Spot) * AcrossFraction * settings.Distance) + settings.Across;
        var y = (DownOf(settings.Spot) * DownFraction * settings.Distance) - settings.Down;

        return new OverlayPlacement(
            OverlayAnchor.Head,
            new OverlayPose(x, y, -settings.Distance),
            settings.Width,
            settings.Opacity);
    }

    /// <summary>The <c>notifyOverlay</c> object as it is written to the file.</summary>
    public JsonObject ToJson()
    {
        var settings = Clamped();

        return new JsonObject
        {
            ["on"] = settings.On,
            ["spot"] = settings.Spot.ToString().ToLowerInvariant(),
            ["across"] = settings.Across,
            ["down"] = settings.Down,
            ["distance"] = settings.Distance,
            ["width"] = settings.Width,
            ["opacity"] = settings.Opacity,
            ["seconds"] = settings.Seconds,
        };
    }

    /// <summary>
    /// Reads the <c>notifyOverlay</c> object. Anything missing or unreadable takes the default for
    /// that field, and a field outside its bounds is brought inside them, so a hand-edited file
    /// cannot put the panel somewhere it cannot be found.
    /// </summary>
    public static NotificationSettings FromJson(JsonNode? node)
    {
        if (node is not JsonObject json)
            return Default;

        var spot = Enum.TryParse<ScreenSpot>(Text(json, "spot"), ignoreCase: true, out var parsed)
            ? parsed
            : Default.Spot;

        return new NotificationSettings(
            Flag(json, "on", Default.On),
            spot,
            Number(json, "across", Default.Across),
            Number(json, "down", Default.Down),
            Number(json, "distance", Default.Distance),
            Number(json, "width", Default.Width),
            Number(json, "opacity", Default.Opacity),
            Number(json, "seconds", Default.Seconds)).Clamped();
    }

    private static float Clamp(float value, float low, float high)
        => float.IsFinite(value) ? Math.Clamp(value, low, high) : Math.Clamp(0f, low, high);

    private static string? Text(JsonObject json, string field)
        => json[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Flag(JsonObject json, string field, bool fallback)
        => json[field] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    private static float Number(JsonObject json, string field, float fallback)
    {
        if (json[field] is not JsonValue value)
            return fallback;

        if (value.TryGetValue<float>(out var number) && float.IsFinite(number))
            return number;

        if (value.TryGetValue<string>(out var text)
            && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && float.IsFinite(number))
            return number;

        return fallback;
    }
}
