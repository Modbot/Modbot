using System.Globalization;
using System.Text.Json.Nodes;

namespace Modbot.Companion.Overlay;

/// <summary>What the panel is fixed to.</summary>
public enum OverlayAnchor
{
    /// <summary>In front of the head, moving with it. The default.</summary>
    Head,

    LeftHand,

    RightHand,

    /// <summary>Left where it was put, in the room.</summary>
    World,
}

/// <summary>
/// A position and a turn, in metres and as a quaternion, plain enough for <c>settings.json</c>.
/// </summary>
/// <remarks>
/// The turn is <c>(QX, QY, QZ, QW)</c> with <c>(0, 0, 0, 1)</c> meaning none. Kept as floats
/// rather than <c>System.Numerics</c> types so the settings library needs no vector maths and the
/// file stays readable.
/// </remarks>
public sealed record OverlayPose(float X, float Y, float Z, float QX = 0f, float QY = 0f, float QZ = 0f, float QW = 1f)
{
    public static OverlayPose Identity { get; } = new(0f, 0f, 0f);
}

/// <summary>
/// Where the panel is and how big: an anchor, an offset from it, a width, an opacity and a curve.
/// </summary>
/// <remarks>
/// <para>Saved under <c>overlay</c> in <c>settings.json</c> and loaded at start, so the panel is
/// where it was left. Every change a controller makes goes through here (overlay OpenXR and
/// interaction design §4.2).</para>
/// <para>The default is a panel 0.35 m to the right, 0.28 m down and 1 m ahead of the head,
/// 0.45 m wide, which is where the OpenVR runtime always put it before placement was a setting.</para>
/// </remarks>
public sealed record OverlayPlacement(
    OverlayAnchor Anchor,
    OverlayPose Offset,
    float Width,
    float Opacity = 1f,
    float Curve = 0f)
{
    /// <summary>
    /// Narrow enough for the wrist. It used to be 0.2 m, which is wider than a panel worn on the
    /// arm has any business being.
    /// </summary>
    public const float MinWidth = 0.12f;

    public const float MaxWidth = 1.5f;
    public const float MinOpacity = 0.1f;
    public const float MinDistance = 0.3f;
    public const float MaxDistance = 3f;

    public static OverlayPlacement Default { get; } = new(OverlayAnchor.Head, new OverlayPose(0.35f, -0.28f, -1.0f), 0.45f);

    /// <summary>
    /// How wide the panel is on a wrist: 0.16 m, about 22° across at the 40 cm a raised wrist sits
    /// from the eyes. Wide enough for four lines of readable text, narrow enough that the arm it
    /// is on does not become a wall in front of the instance.
    /// </summary>
    public const float WristWidth = 0.16f;

    /// <summary>
    /// Where a panel worn on a wrist sits: ten centimetres out of the back of the hand, two
    /// centimetres back towards the elbow, and turned so its face looks out of the back of the
    /// hand rather than along the controller.
    /// </summary>
    /// <remarks>
    /// <para>The offset is read in the controller's own axes, where +Y comes out of the face a
    /// wand's touchpad is on — the back of the hand holding it — and +Z runs back towards the
    /// elbow. The turn is 70° about X, which swings the panel's face from "along the controller"
    /// (where it used to be, and where it was edge-on to the moderator) round to "out of the back
    /// of the hand", leaning the last 20° towards the elbow so that a raised forearm points it at
    /// the eyes. That is where a watch face is.</para>
    /// <para>The quaternion is the half-angle pair for −70° about X: sin(−35°) and cos(−35°).</para>
    /// </remarks>
    public static OverlayPose WristOffset { get; } = new(0f, 0.10f, 0.02f, -0.573576f, 0f, 0f, 0.819152f);

    /// <summary>
    /// How wide the panel is once it is fixed to <paramref name="anchor"/>: the wrist size on a
    /// hand, and off the wrist the width it had, or the ordinary one if it was still wrist-sized.
    /// </summary>
    public static float WidthFor(OverlayAnchor anchor, float current) => anchor switch
    {
        OverlayAnchor.LeftHand or OverlayAnchor.RightHand => WristWidth,
        _ => current <= WristWidth + 0.001f ? Default.Width : current,
    };

    /// <summary>The same placement with every number inside its bounds.</summary>
    public OverlayPlacement Clamped() => this with
    {
        Width = Math.Clamp(Width, MinWidth, MaxWidth),
        Opacity = Math.Clamp(Opacity, MinOpacity, 1f),
        Curve = Math.Clamp(Curve, 0f, 1f),
    };

    /// <summary>The <c>overlay</c> object as it is written to the file.</summary>
    public JsonObject ToJson() => new()
    {
        ["anchor"] = Anchor.ToString().ToLowerInvariant(),
        ["offset"] = new JsonObject
        {
            ["x"] = Offset.X,
            ["y"] = Offset.Y,
            ["z"] = Offset.Z,
            ["qx"] = Offset.QX,
            ["qy"] = Offset.QY,
            ["qz"] = Offset.QZ,
            ["qw"] = Offset.QW,
        },
        ["width"] = Width,
        ["opacity"] = Opacity,
        ["curve"] = Curve,
    };

    /// <summary>
    /// Reads the <c>overlay</c> object. Anything missing or unreadable takes the default for that
    /// field, and a field outside its bounds is brought inside them, so a hand-edited file cannot
    /// put the panel somewhere it cannot be found.
    /// </summary>
    public static OverlayPlacement FromJson(JsonNode? node)
    {
        if (node is not JsonObject json)
            return Default;

        var anchor = Enum.TryParse<OverlayAnchor>(Text(json, "anchor"), ignoreCase: true, out var parsed) ? parsed : Default.Anchor;
        var offset = json["offset"] is JsonObject o
            ? new OverlayPose(
                Number(o, "x", Default.Offset.X),
                Number(o, "y", Default.Offset.Y),
                Number(o, "z", Default.Offset.Z),
                Number(o, "qx", 0f),
                Number(o, "qy", 0f),
                Number(o, "qz", 0f),
                Number(o, "qw", 1f))
            : Default.Offset;

        return new OverlayPlacement(
            anchor,
            offset,
            Number(json, "width", Default.Width),
            Number(json, "opacity", Default.Opacity),
            Number(json, "curve", Default.Curve)).Clamped();
    }

    private static string? Text(JsonObject json, string field)
        => json[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

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
