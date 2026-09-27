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
/// <param name="On">
/// Whether the notification overlay is drawn at all. Off means none of it is built. <strong>On by
/// default</strong>, and it has been since this was built: being told is the reason somebody
/// installs this, and a panel a moderator can only see while they are wearing a headset interrupts
/// nothing they are doing. The notification overlay on a monitor is on by default too, since
/// 2026-09-19, but it reaches an updated install differently — see
/// <see cref="Presentation.DesktopNotifySettings.NotAskedFor"/>, and the asymmetry is deliberate:
/// a window over somebody's monitor is not the same kind of interruption as a panel in a headset.
/// </param>
/// <param name="Spot">Which of the six screen positions.</param>
/// <param name="Across">Fine offset in metres on top of the spot; positive is right.</param>
/// <param name="Down">Fine offset in metres on top of the spot; positive is down.</param>
/// <param name="Distance">Metres ahead of the head.</param>
/// <param name="Width">Metres across the pop-ups' box. Being square, it is as tall.</param>
/// <param name="Opacity">1 is solid.</param>
/// <param name="Seconds">How long one pop-up stays before it clears itself.</param>
/// <param name="Placed">
/// Where the panel was put by hand in the headset, relative to the head, or null while it sits on
/// its spot. Once there is one it is where the panel goes instead of the spot: the distance moves
/// it along the line from the head to it, and across and down nudge it from there. Choosing a
/// spot, or putting it back, clears it.
/// </param>
/// <param name="Locked">The panel cannot be picked up, moved or resized.</param>
/// <param name="ClickThrough">The panel lets controller rays through; only its bar answers.</param>
public sealed record NotifyOverlaySettings(
    bool On = true,
    ScreenSpot Spot = ScreenSpot.TopRight,
    float Across = 0f,
    float Down = 0f,
    float Distance = NotifyOverlaySettings.DefaultDistance,
    float Width = NotifyOverlaySettings.DefaultWidth,
    float Opacity = NotifyOverlaySettings.DefaultOpacity,
    float Seconds = NotifyOverlaySettings.DefaultSeconds,
    OverlayPose? Placed = null,
    bool Locked = false,
    bool ClickThrough = false)
{
    public const float DefaultDistance = 1.0f;

    public const float DefaultWidth = 0.35f;

    public const float DefaultOpacity = 0.95f;

    public const float DefaultSeconds = 6f;

    public const float MinWidth = 0.1f;

    /// <summary>The main panel's own limit, since two hands can now stretch this one as far.</summary>
    public const float MaxWidth = OverlayPlacement.MaxWidth;

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

    /// <summary>The square box the pop-ups stack in, in the panel's pixels.</summary>
    public const int BoxPixels = 256;

    /// <summary>
    /// The whole panel, in pixels: the box, and the bar under it. Square, like every panel, so it
    /// is a little wider than the box as well as taller.
    /// </summary>
    public const int PanelPixels = 300;

    /// <summary>
    /// How much wider the panel is than the box. <see cref="Width"/> is the box's width, as it was
    /// before there was a bar, so the pop-ups stay the size a moderator set them to; the panel the
    /// headset is told about is this much wider.
    /// </summary>
    public const float PanelPerBox = PanelPixels / (float)BoxPixels;

    public static NotifyOverlaySettings Default { get; } = new();

    /// <summary>The same settings with every number inside its bounds.</summary>
    public NotifyOverlaySettings Clamped() => this with
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
    /// angle from straight ahead with the fine offset on top, or where it was put by hand.
    /// </summary>
    public OverlayPlacement ToPlacement()
    {
        var settings = Clamped();

        if (settings.Placed is { } placed && Length(placed) > 0.001f)
        {
            // Along the line from the head to where it was left, at the distance asked for, then
            // nudged; the turn it was left at stays.
            var scale = settings.Distance / Length(placed);
            return new OverlayPlacement(
                OverlayAnchor.Head,
                placed with
                {
                    X = (placed.X * scale) + settings.Across,
                    Y = (placed.Y * scale) - settings.Down,
                    Z = placed.Z * scale,
                },
                settings.Width * PanelPerBox,
                settings.Opacity,
                Locked: settings.Locked,
                ClickThrough: settings.ClickThrough);
        }

        var x = (AcrossOf(settings.Spot) * AcrossFraction * settings.Distance) + settings.Across;
        var y = (DownOf(settings.Spot) * DownFraction * settings.Distance) - settings.Down;

        return new OverlayPlacement(
            OverlayAnchor.Head,
            new OverlayPose(x, y, -settings.Distance),
            settings.Width * PanelPerBox,
            settings.Opacity,
            Locked: settings.Locked,
            ClickThrough: settings.ClickThrough);
    }

    /// <summary>
    /// Where the spot alone puts the panel, at the default distance and ignoring where it was put
    /// by hand: where two quick grips send it home to.
    /// </summary>
    public OverlayPlacement SpotPlacement()
        => (this with { Placed = null, Across = 0f, Down = 0f, Distance = DefaultDistance }).ToPlacement();

    /// <summary>
    /// These settings with the panel where a hand left it: a head-fixed placement becomes the
    /// placed pose, its width and the bar's two switches. The fine offset is spent on the new
    /// place, so it starts again from nothing, and the distance is how far away it was left.
    /// </summary>
    /// <remarks>
    /// A placement fixed to anything but the head is not a place this panel can stay, only one it
    /// passes through while being carried; only the switches are taken from it.
    /// </remarks>
    public NotifyOverlaySettings WithPlacement(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        var switched = this with { Locked = placement.Locked, ClickThrough = placement.ClickThrough };
        if (placement.Anchor is not OverlayAnchor.Head)
            return switched;

        var offset = placement.Offset;
        return (switched with
        {
            Placed = offset,
            Across = 0f,
            Down = 0f,
            Distance = Length(offset),
            Width = placement.Width / PanelPerBox,
        }).Clamped();
    }

    private static float Length(OverlayPose pose)
        => MathF.Sqrt((pose.X * pose.X) + (pose.Y * pose.Y) + (pose.Z * pose.Z));

    /// <summary>The <c>notifyOverlay</c> object as it is written to the file.</summary>
    public JsonObject ToJson()
    {
        var settings = Clamped();

        var json = new JsonObject
        {
            ["on"] = settings.On,
            ["spot"] = settings.Spot.ToString().ToLowerInvariant(),
            ["across"] = settings.Across,
            ["down"] = settings.Down,
            ["distance"] = settings.Distance,
            ["width"] = settings.Width,
            ["opacity"] = settings.Opacity,
            ["seconds"] = settings.Seconds,
            ["locked"] = settings.Locked,
            ["clickThrough"] = settings.ClickThrough,
        };

        if (settings.Placed is { } placed)
        {
            json["placed"] = new JsonObject
            {
                ["x"] = placed.X,
                ["y"] = placed.Y,
                ["z"] = placed.Z,
                ["qx"] = placed.QX,
                ["qy"] = placed.QY,
                ["qz"] = placed.QZ,
                ["qw"] = placed.QW,
            };
        }

        return json;
    }

    /// <summary>
    /// Reads the <c>notifyOverlay</c> object. Anything missing or unreadable takes the default for
    /// that field, and a field outside its bounds is brought inside them, so a hand-edited file
    /// cannot put the panel somewhere it cannot be found.
    /// </summary>
    public static NotifyOverlaySettings FromJson(JsonNode? node)
    {
        if (node is not JsonObject json)
            return Default;

        var spot = Enum.TryParse<ScreenSpot>(Text(json, "spot"), ignoreCase: true, out var parsed)
            ? parsed
            : Default.Spot;

        return new NotifyOverlaySettings(
            Flag(json, "on", Default.On),
            spot,
            Number(json, "across", Default.Across),
            Number(json, "down", Default.Down),
            Number(json, "distance", Default.Distance),
            Number(json, "width", Default.Width),
            Number(json, "opacity", Default.Opacity),
            Number(json, "seconds", Default.Seconds),
            PlacedFrom(json["placed"]),
            Flag(json, "locked", false),
            Flag(json, "clickThrough", false)).Clamped();
    }

    /// <summary>The placed pose, or null when there is none or it is not a pose at all.</summary>
    private static OverlayPose? PlacedFrom(JsonNode? node)
    {
        if (node is not JsonObject o || o["x"] is null || o["y"] is null || o["z"] is null)
            return null;

        var pose = new OverlayPose(
            Number(o, "x", 0f),
            Number(o, "y", 0f),
            Number(o, "z", 0f),
            Number(o, "qx", 0f),
            Number(o, "qy", 0f),
            Number(o, "qz", 0f),
            Number(o, "qw", 1f));

        return Length(pose) > 0.001f ? pose : null;
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
