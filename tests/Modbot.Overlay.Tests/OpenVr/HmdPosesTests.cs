using System.Buffers.Binary;
using System.Numerics;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;

namespace Modbot.Overlay.Tests.OpenVr;

/// <summary>
/// OpenVR's matrices and byte layouts against the panel's own poses: the two conventions are
/// transposes of each other, and the controller state's layout differs by platform.
/// </summary>
public class HmdPosesTests
{
    [Fact]
    public void ATranslationOnlyPoseIsTheTranslationMatrix()
    {
        var m = HmdPoses.ToMatrix(new Pose(new Vector3(0.35f, -0.28f, -1.0f), Quaternion.Identity));

        Assert.Equal(HmdMatrix34.Translation(0.35f, -0.28f, -1.0f), m);
    }

    [Fact]
    public void APoseSurvivesTheRoundTrip()
    {
        var pose = new Pose(
            new Vector3(1.5f, -0.2f, 2.0f),
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.9f, -0.4f, 0.25f)));

        var back = HmdPoses.ToPose(HmdPoses.ToMatrix(pose));

        Assert.True(Vector3.Distance(pose.Position, back.Position) < 1e-5f);
        Assert.True(MathF.Abs(Quaternion.Dot(pose.Rotation, back.Rotation)) > 0.99999f);
    }

    /// <summary>
    /// A quarter turn to the left about Y sends -Z (forward) to -X. OpenVR's matrix for that has
    /// the image of the X axis in its first column, and the conversion must read it as such.
    /// </summary>
    [Fact]
    public void TheMatrixIsReadColumnWise()
    {
        // cos 90 = 0, sin 90 = 1: X -> -Z... written as OpenVR's column-vector rotation about Y.
        var m = new HmdMatrix34
        {
            M00 = 0f, M01 = 0f, M02 = 1f,
            M10 = 0f, M11 = 1f, M12 = 0f,
            M20 = -1f, M21 = 0f, M22 = 0f,
        };

        var pose = HmdPoses.ToPose(m);

        // The image of X under this matrix is (M00, M10, M20) = (0, 0, -1).
        Assert.True(Vector3.Distance(new Vector3(0, 0, -1), pose.Right) < 1e-5f);
        Assert.True(Vector3.Distance(new Vector3(-1, 0, 0), pose.Forward) < 1e-5f);
    }

    [Fact]
    public void ControllerButtonsAreReadAtEachPlatformsOffset()
    {
        var pressed = (1UL << OpenVrLayouts.GripButton) | (1UL << OpenVrLayouts.TriggerButton);

        var windows = new byte[64];
        BinaryPrimitives.WriteUInt64LittleEndian(windows.AsSpan(8), pressed);
        BinaryPrimitives.WriteSingleLittleEndian(windows.AsSpan(24), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(windows.AsSpan(28), -0.25f);

        var linux = new byte[60];
        BinaryPrimitives.WriteUInt64LittleEndian(linux.AsSpan(4), pressed);
        BinaryPrimitives.WriteSingleLittleEndian(linux.AsSpan(20), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(linux.AsSpan(24), -0.25f);

        Assert.Equal((true, true, new Vector2(0.5f, -0.25f)), OpenVrLayouts.ReadButtons(windows, windowsLayout: true));
        Assert.Equal((true, true, new Vector2(0.5f, -0.25f)), OpenVrLayouts.ReadButtons(linux, windowsLayout: false));
        Assert.Equal((false, false, Vector2.Zero), OpenVrLayouts.ReadButtons(new byte[64], windowsLayout: true));
    }

    [Fact]
    public void AnInvalidPoseIsNullAndAValidOneIsRead()
    {
        var bytes = new byte[OpenVrLayouts.PoseSize];
        var m = HmdMatrix34.Translation(1f, 2f, 3f);
        float[] cells = [m.M00, m.M01, m.M02, m.M03, m.M10, m.M11, m.M12, m.M13, m.M20, m.M21, m.M22, m.M23];
        for (var i = 0; i < cells.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), cells[i]);

        Assert.Null(OpenVrLayouts.ReadPose(bytes));

        bytes[76] = 1;
        var pose = OpenVrLayouts.ReadPose(bytes);

        Assert.NotNull(pose);
        Assert.Equal(new Vector3(1f, 2f, 3f), pose.Value.Position);
    }
}
