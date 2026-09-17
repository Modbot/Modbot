using System.Numerics;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>Controllers made up in code: a hand somewhere, pointing at something.</summary>
internal static class Hands
{
    /// <summary>A turn that points -Z along <paramref name="direction"/>.</summary>
    public static Quaternion Facing(Vector3 direction)
    {
        direction = Vector3.Normalize(direction);
        var forward = -Vector3.UnitZ;
        var dot = Vector3.Dot(forward, direction);

        if (dot > 0.9999f)
            return Quaternion.Identity;

        if (dot < -0.9999f)
            return Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);

        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(forward, direction)), MathF.Acos(dot));
    }

    public static Pose AimingAt(Vector3 from, Vector3 target) => new(from, Facing(target - from));

    public static HandState Hand(Pose aim, bool grab = false, bool click = false, Vector2 scroll = default)
        => new(true, aim, grab, click, scroll);

    public static OverlayTracking RightOnly(HandState right) => new(Pose.Identity, HandState.Missing, right);

    public static OverlayTracking Both(HandState left, HandState right) => new(Pose.Identity, left, right);
}
