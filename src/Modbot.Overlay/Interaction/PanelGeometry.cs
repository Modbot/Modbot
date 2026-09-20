using System.Numerics;
using Modbot.Companion.Overlay;

namespace Modbot.Overlay.Interaction;

/// <summary>Where a pointing ray meets the panel, as a fraction across and down it.</summary>
/// <param name="Across">0 at the left edge, 1 at the right.</param>
/// <param name="Down">0 at the top edge, 1 at the bottom.</param>
/// <param name="Distance">Metres from the ray's start to the panel.</param>
public readonly record struct PanelHit(float Across, float Down, float Distance)
{
    public bool IsOnPanel => Across is >= 0f and <= 1f && Down is >= 0f and <= 1f;
}

/// <summary>
/// The panel as a flat square in the room, and how rays meet it.
/// </summary>
/// <remarks>
/// <para>The square is centred on the placement's pose, <c>Width</c> across and, being square, as
/// tall. Its right is the pose's +X, its up is +Y, and it faces +Z: a panel put 1 m ahead of the
/// head with no turn faces back at the head. Both runtimes are told the same pose and draw the
/// picture the same way up, so a fraction across and down here is the same fraction of the
/// texture.</para>
/// <para>A curved panel is treated as flat here. The curve is slight, and a hit a few pixels off is
/// a cursor a few pixels off, not a wrong row.</para>
/// </remarks>
public static class PanelGeometry
{
    /// <summary>
    /// The panel's pose in the room, or null when its anchor is a hand that is not tracked.
    /// </summary>
    public static Pose? PanelPose(OverlayPlacement placement, OverlayTracking tracking)
    {
        ArgumentNullException.ThrowIfNull(placement);

        var offset = Pose.From(placement.Offset);

        return placement.Anchor switch
        {
            OverlayAnchor.Head => tracking.Head.Then(offset),

            // The controller's own pose, not where it points: it is what both runtimes hang a
            // hand-anchored panel off, so it has to be what the hit testing measures from too.
            OverlayAnchor.LeftHand => tracking.Left.Tracked ? tracking.Left.Device.Then(offset) : null,
            OverlayAnchor.RightHand => tracking.Right.Tracked ? tracking.Right.Device.Then(offset) : null,
            _ => offset,
        };
    }

    /// <summary>
    /// Where a ray meets the panel's plane, or null when it points away from the panel's face or
    /// along it. The hit may lie outside the square; see <see cref="PanelHit.IsOnPanel"/>.
    /// </summary>
    public static PanelHit? Hit(Pose panel, float width, Pose ray)
    {
        var normal = Vector3.Transform(Vector3.UnitZ, panel.Rotation);
        var direction = ray.Forward;
        var facing = Vector3.Dot(direction, normal);

        // The ray must travel against the normal to land on the face; a ray parallel to the
        // panel, or coming from behind it, never meets the face.
        if (facing >= -1e-6f)
            return null;

        var distance = Vector3.Dot(panel.Position - ray.Position, normal) / facing;
        if (distance <= 0f)
            return null;

        var point = ray.Position + (direction * distance) - panel.Position;
        var across = (Vector3.Dot(point, panel.Right) / width) + 0.5f;
        var down = 0.5f - (Vector3.Dot(point, panel.Up) / width);

        return new PanelHit(across, down, distance);
    }
}
