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

    /// <summary>
    /// Which of the five axes SteamVR's own legacy bindings put the grip on.
    /// </summary>
    /// <remarks>
    /// Axis 0 is the thumbstick or touchpad and axis 1 is the trigger on every controller; the
    /// grip, where the controller has an analogue one, is axis 2. A controller whose grip is a
    /// plain switch — a Vive wand — leaves this at zero, which is why nothing is decided on the
    /// number alone (see <see cref="GripHold"/>).
    /// </remarks>
    public const int GripAxis = 2;

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

    /// <summary>
    /// The grip button, how hard the grip is squeezed, the trigger, and the first axis (thumbstick
    /// or touchpad) out of a controller state.
    /// </summary>
    /// <remarks>
    /// The five axes follow the two button words, eight bytes each: axis 0 the thumbstick or
    /// touchpad, axis 1 the trigger, axis 2 the grip. The whole state is read, so the length check
    /// covers all five rather than only the first.
    /// </remarks>
    public static (bool Grip, float Squeeze, bool Click, Vector2 Scroll) ReadButtons(ReadOnlySpan<byte> bytes, bool windowsLayout)
    {
        var buttons = windowsLayout ? 8 : 4;
        var axes = buttons + 16;
        if (bytes.Length < axes + (5 * 8))
            return (false, 0f, false, Vector2.Zero);

        var pressed = BinaryPrimitives.ReadUInt64LittleEndian(bytes[buttons..]);

        return (
            (pressed & (1UL << GripButton)) != 0,
            F(bytes, axes + (GripAxis * 8)),
            (pressed & (1UL << TriggerButton)) != 0,
            new Vector2(F(bytes, axes), F(bytes, axes + 4)));
    }

    internal static (bool Grip, float Squeeze, bool Click, Vector2 Scroll) ReadButtons(ReadOnlySpan<byte> bytes)
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
/// <para><strong>What it does to what it reads.</strong> Two corrections, both because OpenVR
/// answers a narrower question than the panel asks. The grip is decided from how hard it is
/// squeezed rather than from SteamVR's own grip button, which on an Index only turns on under a
/// hard squeeze (<see cref="GripHold"/>); and the pointing direction is tilted off the
/// controller's body, because the pose OpenVR gives runs along the controller and nobody points
/// along that line (<see cref="ControllerPointing"/>).</para>
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

    // One per hand, because whether a light squeeze counts as a hold depends on whether that hand
    // was already holding.
    private readonly GripHold _leftGrip = new();
    private readonly GripHold _rightGrip = new();

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

    /// <summary>SteamVR's index for a hand's controller, or null when it has none right now.</summary>
    public uint? DeviceIndex(Interaction.Hand hand)
    {
        var index = ((delegate* unmanaged[Stdcall]<int, uint>)Slot(GetTrackedDeviceIndexForControllerRole))(
            hand == Interaction.Hand.Left ? RoleLeftHand : RoleRightHand);
        return index == InvalidDevice || index >= MaxDevices ? null : index;
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
        var grip = role == RoleLeftHand ? _leftGrip : _rightGrip;

        var index = ((delegate* unmanaged[Stdcall]<int, uint>)Slot(GetTrackedDeviceIndexForControllerRole))(role);
        if (index == InvalidDevice || index >= MaxDevices)
        {
            grip.Forget();
            return HandState.Missing;
        }

        // The pose SteamVR gives runs along the controller's body; the ray has to come off it at
        // an angle or the cursor sits above what the moderator is aiming at.
        if (OpenVrLayouts.ReadPose(PoseBytes((int)index)) is not { } device)
        {
            grip.Forget();
            return HandState.Missing;
        }

        var aim = ControllerPointing.Aim(device);

        bool read;
        fixed (byte* state = _state)
        {
            read = ((delegate* unmanaged[Stdcall]<uint, void*, uint, byte>)Slot(GetControllerState))(
                index, state, (uint)_state.Length) != 0;
        }

        if (!read)
        {
            grip.Forget();
            return new HandState(true, aim, device, false, false, Vector2.Zero);
        }

        var (button, squeeze, click, scroll) = OpenVrLayouts.ReadButtons(_state);
        return new HandState(true, aim, device, grip.Squeeze(button, squeeze), click, scroll);
    }

    private ReadOnlySpan<byte> PoseBytes(int device)
        => _poses.AsSpan(device * OpenVrLayouts.PoseSize, OpenVrLayouts.PoseSize);

    private void* Slot(int index) => *((void**)_table + index);
}
