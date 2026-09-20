using System.Numerics;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.OpenVr;

/// <summary>
/// Turning the pose SteamVR gives for a controller into the direction the moderator feels they
/// are pointing.
/// </summary>
/// <remarks>
/// <para><strong>The fault this fixes.</strong> OpenVR hands out one pose per device, and it runs
/// along the controller's own body: the line the handle makes through the fist. Nobody points
/// along that line. A controller is held with the handle raked back, so a ray fired straight down
/// it leaves the hand climbing, and the cursor lands above whatever the moderator is aiming at.
/// The further the panel, the wider the miss. That is the skew — the panel was being pointed at
/// from an angle nobody chose.</para>
/// <para><strong>What every other runtime does about it.</strong> OpenXR names two poses for this
/// exact reason: <c>grip</c>, which runs along the controller, and <c>aim</c>, which the runtime
/// tilts down off it so that pointing works. The overlay's OpenXR path asks for <c>aim</c> and has
/// never had the problem. OpenVR has no equivalent to ask for — its one nod to it is a "tip" part
/// buried in each controller's render model, read through an interface Modbot does not open — so
/// the tilt is applied here instead.</para>
/// <para><strong>The number, and how sure it is.</strong> The tilt is 35° down about the
/// controller's own side-to-side axis. That is the middle of the range runtimes use between the
/// two poses for the controllers a moderator is likely to be wearing — a Valve Index or an Oculus
/// Touch, whose handles are raked well back. A Vive wand is a straight rod and wants far less,
/// perhaps 5°, so a wand will now read about 30° low; wands are the rarer controller and the
/// complaint that prompted this came from an Index. <strong>Nobody has checked this against a
/// headset.</strong> It is one constant in one place for exactly that reason: if it is wrong, it
/// is wrong by a number and not by a design.</para>
/// <para><strong>Position is left alone.</strong> The real ray starts a couple of centimetres
/// from the tracked point. At a panel a metre away that is under two degrees, against the
/// thirty-odd the direction was out by, and moving the start would be another guess on top of
/// this one.</para>
/// </remarks>
public static class ControllerPointing
{
    /// <summary>How far the ray is tilted down off the controller's body, in degrees.</summary>
    public const float TiltDegrees = 35f;

    /// <summary>
    /// The tilt as a turn about the controller's own X axis. Negative, because a right-handed turn
    /// about +X swings the forward direction (−Z) upwards and this one has to go down.
    /// </summary>
    private static readonly Quaternion Tilt =
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, -TiltDegrees * MathF.PI / 180f);

    /// <summary>The pointing pose for a controller at <paramref name="device"/>.</summary>
    public static Pose Aim(Pose device) => device.Then(new Pose(Vector3.Zero, Tilt));
}
