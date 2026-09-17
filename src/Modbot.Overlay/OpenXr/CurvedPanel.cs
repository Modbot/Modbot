using System.Numerics;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.OpenXr;

/// <summary>
/// The panel bent round a cylinder, in the terms OpenXR's cylinder layer takes
/// (<c>XR_KHR_composition_layer_cylinder</c>).
/// </summary>
/// <remarks>
/// <para>The placement's <c>Curve</c> reads as OpenVR's curvature does: 0 is flat and 1 wraps the
/// panel's width round a full circle. So the radius is the width over the circumference fraction,
/// <c>width / (2π · curve)</c>, and the visible arc is <c>width / radius</c> radians. The layer's
/// aspect ratio is 1, because the panel is square, so its height on the cylinder equals the arc's
/// length: the same size the flat quad has.</para>
/// <para>The layer's pose is the cylinder's centre line, not the panel's face: the panel's centre
/// is one radius from it along the pose's -Z. The panel faces +Z, towards whoever looks at it, so
/// the centre sits one radius out in front of the panel and the panel bends round the viewer,
/// the way a curved screen does.</para>
/// </remarks>
/// <param name="Radius">Metres from the centre line to the panel.</param>
/// <param name="CentralAngle">The visible arc, in radians.</param>
public readonly record struct CurvedPanel(float Radius, float CentralAngle)
{
    /// <summary>Just short of a full turn: the layer's central angle must stay below 2π.</summary>
    public const float MaxCentralAngle = MathF.Tau - 0.001f;

    /// <summary>The cylinder for a width and a curve, or null when the curve is 0 and the panel is flat.</summary>
    public static CurvedPanel? For(float width, float curve)
    {
        if (!(curve > 0f) || !(width > 0f))
            return null;

        curve = MathF.Min(curve, 1f);
        var radius = width / (MathF.Tau * curve);
        var angle = MathF.Min(width / radius, MaxCentralAngle);
        return new CurvedPanel(radius, angle);
    }

    /// <summary>The cylinder's centre line for a panel at this pose: one radius out along the panel's own +Z.</summary>
    public Pose CentreOf(Pose panel) => panel.Then(new Pose(new Vector3(0f, 0f, Radius), Quaternion.Identity));
}
