using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Silk.NET.OpenXR;

namespace Modbot.Overlay.OpenXr;

/// <summary>The small things every OpenXR call here needs: a result check, fixed strings, and poses both ways.</summary>
internal static unsafe class OpenXrCalls
{
    /// <summary>A negative result is a failure; the positive ones (session not focused, and the like) are answers.</summary>
    public static void Check(Result result, string runtimeName, string call)
    {
        if (result < 0)
            throw new OverlayStartFailure(OverlayRuntimeState.Refused, $"{runtimeName} answered {result} to {call}.");
    }

    public static string FixedString(byte* bytes) => Marshal.PtrToStringUTF8((nint)bytes) ?? "";

    public static void WriteFixedString(byte* destination, int capacity, string value)
    {
        var span = new Span<byte>(destination, capacity);
        span.Clear();
        Encoding.UTF8.GetBytes(value.AsSpan(), span[..(capacity - 1)]);
    }

    public static readonly Posef IdentityPose = new(new Quaternionf(0, 0, 0, 1), new Vector3f(0, 0, 0));

    /// <summary>
    /// OpenXR's pose to the panel's. The same axes on both sides (metres, Y up, -Z forward, a
    /// right-handed quaternion), so nothing is turned; the numbers are only carried across.
    /// </summary>
    public static Pose ToPose(in Posef pose) => new(
        new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z),
        Quaternion.Normalize(new Quaternion(pose.Orientation.X, pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W)));

    public static Posef ToPosef(in Pose pose) => new(
        new Quaternionf(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W),
        new Vector3f(pose.Position.X, pose.Position.Y, pose.Position.Z));
}
