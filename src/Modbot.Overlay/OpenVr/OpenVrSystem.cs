using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.OpenVr;

/// <summary>
/// The byte layouts SteamVR hands back for a device pose and a controller's state, read by
/// offset rather than through a struct.
/// </summary>
/// <remarks>
/// <para>The C++ header SteamVR is built with packs its structs to 8 bytes on Windows and to 4
/// on Linux and macOS, so <c>VRControllerState_t</c> is 64 bytes on Windows with the button
/// bits at offset 8, and 60 bytes on Linux with them at offset 4. SteamVR checks the size it is
/// handed and answers nothing when it differs. A struct with one layout would be wrong on one
/// platform; offsets chosen at run time are right on both.</para>
/// <para><c>TrackedDevicePose_t</c> has no 8-byte member and is 80 bytes everywhere.</para>
/// </remarks>
public static class OpenVrLayouts
{
    public const int PoseSize = 80;

    public static int ControllerStateSize => OperatingSystem.IsWindows() ? 64 : 60;

    private static int ButtonsOffset => OperatingSystem.IsWindows() ? 8 : 4;

    /// <summary><c>k_EButton_Grip</c>.</summary>
    public const int GripButton = 2;

    /// <summary><c>k_EButton_SteamVR_Trigger</c>, also <c>k_EButton_Axis1</c>.</summary>
    public const int TriggerButton = 33;

    /// <summary>The device's pose in the room, or null when SteamVR says it is not valid.</summary>
    public static Pose? ReadPose(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < PoseSize)
            return null;

        // Twelve floats, then two velocity vectors, the tracking result, and two one-byte flags.
        var valid = bytes[76] != 0;
        if (!valid)
            return null;

        var m = new HmdMatrix34
        {
            M00 = F(bytes, 0), M01 = F(bytes, 4), M02 = F(bytes, 8), M03 = F(bytes, 12),
            M10 = F(bytes, 16), M11 = F(bytes, 20), M12 = F(bytes, 24), M13 = F(bytes, 28),
            M20 = F(bytes, 32), M21 = F(bytes, 36), M22 = F(bytes, 40), M23 = F(bytes, 44),
        };

        return HmdPoses.ToPose(m);
    }

    /// <summary>Grip, trigger and the first axis (thumbstick or touchpad) out of a controller state.</summary>
    public static (bool Grab, bool Click, Vector2 Scroll) ReadButtons(ReadOnlySpan<byte> bytes, bool windowsLayout)
    {
        var buttons = windowsLayout ? 8 : 4;
        if (bytes.Length < buttons + 16 + 8)
            return (false, false, Vector2.Zero);

        var pressed = BinaryPrimitives.ReadUInt64LittleEndian(bytes[buttons..]);
        var axis0 = buttons + 16;

        return (
            (pressed & (1UL << GripButton)) != 0,
            (pressed & (1UL << TriggerButton)) != 0,
            new Vector2(F(bytes, axis0), F(bytes, axis0 + 4)));
    }

    internal static (bool Grab, bool Click, Vector2 Scroll) ReadButtons(ReadOnlySpan<byte> bytes)
        => ReadButtons(bytes, OperatingSystem.IsWindows());

    private static float F(ReadOnlySpan<byte> bytes, int offset)
        => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
}

/// <summary>
/// SteamVR's tracking, through <c>IVRSystem</c>: where the head and the controllers are, and
/// what is pressed.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> The poses of the headset and the two hand
/// controllers and the state of their grip, trigger and thumbstick, from the SteamVR the
/// companion is already attached to, each time the overlay polls. It reads nothing about any
/// other program, and nothing here is reported anywhere: the poses decide where the panel's
/// cursor is and whether it is being held, and are dropped.</para>
/// <para>Legacy controller state rather than SteamVR Input actions: an overlay with no action
/// manifest is given the legacy bindings, which is one call per hand and no manifest to ship.</para>
/// </remarks>
internal sealed unsafe class OpenVrSystem
{
    /// <summary><c>IVRSystem_026</c>, the version in the pinned header.</summary>
    internal const string InterfaceVersion = "FnTable:IVRSystem_026";

    private const int MaxDevices = 64;
    private const uint InvalidDevice = 0xFFFFFFFF;
    private const int TrackingUniverseStanding = 1;
    private const int RoleLeftHand = 1;
    private const int RoleRightHand = 2;

    // Slots in VR_IVRSystem_FnTable for IVRSystem_026, counted in headers/openvr_capi.h.
    private const int GetDeviceToAbsoluteTrackingPose = 12;
    private const int GetTrackedDeviceIndexForControllerRole = 18;
    private const int GetControllerState = 37;

    private readonly nint _table;
    private readonly byte[] _poses = new byte[MaxDevices * OpenVrLayouts.PoseSize];
    private readonly byte[] _state = new byte[OpenVrLayouts.ControllerStateSize];

    private OpenVrSystem(nint table)
    {
        _table = table;
    }

    /// <summary>The system interface of the SteamVR this process is initialised against, or null.</summary>
    public static OpenVrSystem? Open()
    {
        var table = OpenVrInterop.GetGenericInterface(InterfaceVersion, out var error);
        return table == 0 || error != VrInitError.None ? null : new OpenVrSystem(table);
    }

    public OverlayTracking Read()
    {
        fixed (byte* poses = _poses)
        {
            ((delegate* unmanaged[Stdcall]<int, float, void*, uint, void>)Slot(GetDeviceToAbsoluteTrackingPose))(
                TrackingUniverseStanding, 0f, poses, MaxDevices);
        }

        var head = OpenVrLayouts.ReadPose(PoseBytes(0)) ?? Pose.Identity;

        return new OverlayTracking(head, Hand(RoleLeftHand), Hand(RoleRightHand));
    }

    private HandState Hand(int role)
    {
        var index = ((delegate* unmanaged[Stdcall]<int, uint>)Slot(GetTrackedDeviceIndexForControllerRole))(role);
        if (index == InvalidDevice || index >= MaxDevices)
            return HandState.Missing;

        if (OpenVrLayouts.ReadPose(PoseBytes((int)index)) is not { } aim)
            return HandState.Missing;

        bool read;
        fixed (byte* state = _state)
        {
            read = ((delegate* unmanaged[Stdcall]<uint, void*, uint, byte>)Slot(GetControllerState))(
                index, state, (uint)_state.Length) != 0;
        }

        if (!read)
            return new HandState(true, aim, false, false, Vector2.Zero);

        var (grab, click, scroll) = OpenVrLayouts.ReadButtons(_state);
        return new HandState(true, aim, grab, click, scroll);
    }

    private ReadOnlySpan<byte> PoseBytes(int device)
        => _poses.AsSpan(device * OpenVrLayouts.PoseSize, OpenVrLayouts.PoseSize);

    private void* Slot(int index) => *((void**)_table + index);
}
