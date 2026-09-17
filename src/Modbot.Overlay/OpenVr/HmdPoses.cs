using System.Numerics;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.OpenVr;

/// <summary>
/// OpenVR's 3×4 matrices to and from the panel's <see cref="Pose"/>.
/// </summary>
/// <remarks>
/// OpenVR's matrix multiplies column vectors (<c>M · v</c>), so the image of the X axis is its
/// first column; <c>System.Numerics</c> multiplies row vectors (<c>v · M</c>), so the image of the
/// X axis is its first row. The two are each other's transpose, and that is the whole of the
/// conversion. Both use metres, Y up and -Z forward.
/// </remarks>
public static class HmdPoses
{
    public static Pose ToPose(in HmdMatrix34 m)
    {
        var rotation = new Matrix4x4(
            m.M00, m.M10, m.M20, 0f,
            m.M01, m.M11, m.M21, 0f,
            m.M02, m.M12, m.M22, 0f,
            0f, 0f, 0f, 1f);

        return new Pose(
            new Vector3(m.M03, m.M13, m.M23),
            Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation)));
    }

    public static HmdMatrix34 ToMatrix(Pose pose)
    {
        var n = Matrix4x4.CreateFromQuaternion(pose.Rotation);

        return new HmdMatrix34
        {
            M00 = n.M11, M01 = n.M21, M02 = n.M31, M03 = pose.Position.X,
            M10 = n.M12, M11 = n.M22, M12 = n.M32, M13 = pose.Position.Y,
            M20 = n.M13, M21 = n.M23, M22 = n.M33, M23 = pose.Position.Z,
        };
    }
}
