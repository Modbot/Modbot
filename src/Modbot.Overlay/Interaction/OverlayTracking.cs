using System.Numerics;
using Modbot.Companion.Overlay;

namespace Modbot.Overlay.Interaction;

/// <summary>A position and a turn in the room. Right-handed, metres, Y up, forward is -Z.</summary>
/// <remarks>
/// Both runtimes speak this: OpenVR's absolute tracking space and OpenXR's LOCAL space are the
/// same shape (OpenGL-style axes, metres), so a pose read from either goes through the maths
/// below unchanged.
/// </remarks>
public readonly record struct Pose(Vector3 Position, Quaternion Rotation)
{
    public static Pose Identity { get; } = new(Vector3.Zero, Quaternion.Identity);

    /// <summary>Where this pose points: its own -Z, turned into the room.</summary>
    public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Rotation);

    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Rotation);

    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);

    /// <summary>A pose given relative to this one, placed in the room.</summary>
    public Pose Then(Pose local) => new(
        Position + Vector3.Transform(local.Position, Rotation),
        Quaternion.Normalize(Quaternion.Concatenate(local.Rotation, Rotation)));

    /// <summary>This pose, as seen from <paramref name="parent"/>.</summary>
    public Pose RelativeTo(Pose parent) => parent.Inverse().Then(this);

    public Pose Inverse()
    {
        var rotation = Quaternion.Inverse(Rotation);
        return new Pose(Vector3.Transform(-Position, rotation), rotation);
    }

    public static Pose From(OverlayPose pose) => new(
        new Vector3(pose.X, pose.Y, pose.Z),
        Quaternion.Normalize(new Quaternion(pose.QX, pose.QY, pose.QZ, pose.QW)));

    public OverlayPose ToOverlayPose() => new(
        Position.X, Position.Y, Position.Z, Rotation.X, Rotation.Y, Rotation.Z, Rotation.W);
}

/// <summary>Which hand.</summary>
public enum Hand
{
    Left,
    Right,
}

/// <summary>
/// One controller as the runtime last saw it: where it points, and what is pressed.
/// </summary>
/// <param name="Tracked">False when the controller is off, out of view or not there; the rest is then meaningless.</param>
/// <param name="Aim">The pointing pose: the ray leaves <c>Aim.Position</c> along <c>Aim.Forward</c>.</param>
/// <param name="Device">
/// Where the controller itself is, along its own body. A panel worn on a hand hangs off this,
/// because this is what both runtimes attach it to; the ray comes out of <paramref name="Aim"/>,
/// which is tilted away from it.
/// </param>
/// <param name="Grab">The grip.</param>
/// <param name="Click">The trigger.</param>
/// <param name="Scroll">Thumbstick or touchpad, -1..1 on each axis, zero at rest.</param>
/// <remarks>
/// <para><strong>Why there are two poses and not one.</strong> Where a controller is and where a
/// person feels they are pointing are not the same direction — a controller's body sits at an
/// angle in the fist, so a ray fired straight along it lands above what the moderator is aiming
/// at. Runtimes answer this with two poses, and so does this: <paramref name="Device"/> for
/// hanging things off the controller, <paramref name="Aim"/> for the ray. OpenXR asks for both by
/// name; OpenVR hands out only the device pose and the tilt is worked out from it.</para>
/// </remarks>
public readonly record struct HandState(bool Tracked, Pose Aim, Pose Device, bool Grab, bool Click, Vector2 Scroll)
{
    public static HandState Missing { get; } = new(false, Pose.Identity, Pose.Identity, false, false, Vector2.Zero);
}

/// <summary>What both runtimes report each poll, all in the room.</summary>
public readonly record struct OverlayTracking(Pose Head, HandState Left, HandState Right)
{
    /// <summary>Nobody tracked: a desktop, or a headset that is off.</summary>
    public static OverlayTracking None { get; } = new(Pose.Identity, HandState.Missing, HandState.Missing);

    public HandState this[Hand hand] => hand == Hand.Left ? Left : Right;
}
