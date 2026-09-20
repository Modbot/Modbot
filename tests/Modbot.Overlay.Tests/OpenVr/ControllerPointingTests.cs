using System.Numerics;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;

namespace Modbot.Overlay.Tests.OpenVr;

/// <summary>
/// The tilt that turns the pose SteamVR gives for a controller into the direction the moderator
/// feels they are pointing, as arithmetic: the ray comes off the controller's body downwards, by
/// the stated angle, about the controller's own side-to-side axis, and nothing else moves.
/// </summary>
public class ControllerPointingTests
{
    private const float Tolerance = 1e-4f;

    /// <summary>A controller lying flat, pointing along -Z with no turn at all.</summary>
    private static readonly Pose Flat = new(new Vector3(0.2f, 1.1f, -0.4f), Quaternion.Identity);

    [Fact]
    public void TheRayLeavesTheControllerBodyTiltedDownByTheStatedAngle()
    {
        var aim = ControllerPointing.Aim(Flat);

        var radians = ControllerPointing.TiltDegrees * MathF.PI / 180f;
        var expected = new Vector3(0f, -MathF.Sin(radians), -MathF.Cos(radians));

        Assert.True(Vector3.Distance(expected, aim.Forward) < Tolerance, $"the ray points {aim.Forward}");
    }

    /// <summary>
    /// Down, not up. The whole complaint was a cursor sitting above what was being aimed at, and
    /// the wrong sign here would double it rather than fix it.
    /// </summary>
    [Fact]
    public void TheTiltGoesDownwards()
    {
        Assert.True(ControllerPointing.Aim(Flat).Forward.Y < 0f);
    }

    /// <summary>The angle between the controller's body and the ray is exactly the stated one.</summary>
    [Fact]
    public void TheAngleBetweenTheBodyAndTheRayIsTheStatedOne()
    {
        var aim = ControllerPointing.Aim(Flat);

        var degrees = MathF.Acos(Math.Clamp(Vector3.Dot(Flat.Forward, aim.Forward), -1f, 1f)) * 180f / MathF.PI;

        Assert.Equal(ControllerPointing.TiltDegrees, degrees, 3);
    }

    /// <summary>
    /// The tilt is about the controller's own axis, not the room's: a controller turned on its
    /// side has its ray tilted sideways in the room, which is what "tilted in the hand" means.
    /// </summary>
    [Fact]
    public void TheTiltIsAboutTheControllersOwnAxis()
    {
        // Rolled 90° about its own forward axis, so its X now points down in the room.
        var rolled = Flat with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) };

        var aim = ControllerPointing.Aim(rolled);

        var radians = ControllerPointing.TiltDegrees * MathF.PI / 180f;
        var expected = new Vector3(-MathF.Sin(radians), 0f, -MathF.Cos(radians));

        Assert.True(Vector3.Distance(expected, aim.Forward) < Tolerance, $"the ray points {aim.Forward}");
    }

    /// <summary>Where the ray starts is left where the controller is; only the direction is corrected.</summary>
    [Fact]
    public void TheRayStartsWhereTheControllerIs()
    {
        Assert.Equal(Flat.Position, ControllerPointing.Aim(Flat).Position);
    }

    /// <summary>
    /// What the fault cost, in the units the complaint was made in: a panel a metre ahead was
    /// being pointed at from most of a panel's width away.
    /// </summary>
    [Fact]
    public void TheTiltIsWorthMoreThanHalfAMetreAtAMetre()
    {
        var straight = Flat.Forward;
        var corrected = ControllerPointing.Aim(Flat).Forward;

        // Both rays travelled a metre forward: how far apart they land.
        var apart = MathF.Abs((straight.Y / -straight.Z) - (corrected.Y / -corrected.Z));

        Assert.True(apart > 0.5f, $"the correction moves the cursor {apart:0.00} m at a metre");
    }
}
