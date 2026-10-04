using System.Runtime.InteropServices;

namespace Modbot.Overlay.OpenVr;

/// <summary>Why <c>VR_Init</c> refused. Only the values Modbot distinguishes are named.</summary>
public enum VrInitError
{
    None = 0,
    Unknown = 1,
    Init_InstallationNotFound = 100,
    Init_HmdNotFound = 108,
    Init_NotInitialized = 109,
    Init_PathRegistryNotFound = 110,
    Init_NoServerForBackgroundApp = 121,

    /// <summary>The runtime does not take this kind of application: xrizer answers this to an overlay.</summary>
    Init_InvalidApplicationType = 130,
    Init_VRDashboardNotFound = 133,
}

/// <summary>What kind of native texture <c>Texture_t.handle</c> is.</summary>
public enum VrTextureType
{
    Invalid = -1,

    /// <summary>An <c>ID3D11Texture2D*</c>. The one Modbot uses.</summary>
    DirectX = 0,

    OpenGL = 1,
    Vulkan = 2,
    DirectX12 = 4,
    DxgiSharedHandle = 5,
}

public enum VrColorSpace
{
    /// <summary>Let the compositor decide from the texture format. Correct for BGRA_UNORM.</summary>
    Auto = 0,
    Gamma = 1,
    Linear = 2,
}

/// <summary>
/// <c>Texture_t</c>, laid out exactly as <c>openvr_capi.h</c> declares it.
/// </summary>
/// <remarks>
/// Three fields, in this order, and the layout is not negotiable — it is read by native code. The
/// enums are <c>int</c>-sized on purpose, matching the C enums.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VrTexture
{
    public nint Handle;

    public VrTextureType Type;

    public VrColorSpace ColorSpace;
}

/// <summary>
/// The raw OpenVR entry points Modbot needs, and nothing else.
/// </summary>
/// <remarks>
/// <para><strong>Why this is hand-written rather than a package.</strong> The obvious NuGet
/// binding, <c>OVRSharp</c>, drags in <c>System.Drawing.Common</c> 5.0.0, which carries a known
/// critical advisory; with warnings treated as errors that package cannot even restore here, and
/// shipping a known-vulnerable dependency inside a client whose entire argument is
/// trustworthiness would be a poor trade for saving eighty lines.</para>
/// <para><strong>The function table is an ordered array of pointers, so the order matters.</strong>
/// <c>VR_GetGenericInterface("FnTable:IVROverlay_028")</c> returns a pointer to a C struct of
/// function pointers; calling the wrong slot calls the wrong function with the wrong arguments and
/// corrupts memory rather than failing cleanly. The indices in <see cref="OverlaySlot"/> are taken
/// from Valve's own <c>openvr_capi.h</c> for the pinned interface version, and the version string
/// is pinned with them: a mismatched <c>openvr_api.dll</c> must be refused by
/// <c>VR_GetGenericInterface</c> rather than silently reinterpreted.</para>
/// <para><strong>This talks to SteamVR on the same machine and to nothing else.</strong> It opens
/// no socket and reads no file; the only data crossing it is a texture pointer and the overlay's
/// position.</para>
/// </remarks>
internal static partial class OpenVrInterop
{
    /// <summary>
    /// The interface version <see cref="OverlaySlot"/>'s indices were read from. Changing one
    /// without the other is the bug this constant exists to make obvious.
    /// </summary>
    internal const string OverlayInterfaceVersion = "FnTable:IVROverlay_028";

    internal const int ApplicationTypeOverlay = 2;

    /// <summary>
    /// A background application is only ever answered by a SteamVR that is already running; asking
    /// as one never launches it. That is what makes it the right first question.
    /// </summary>
    internal const int ApplicationTypeBackground = 3;

    /// <summary><c>k_unTrackedDeviceIndex_Hmd</c>: the headset, for a transform relative to it.</summary>
    /// <summary><c>TrackingUniverseStanding</c>: the room's own space, the one poses are read in.</summary>
    internal const int TrackingUniverseStanding = 1;

    internal const uint TrackedDeviceIndexHmd = 0;

    /// <summary><c>VREvent_Quit</c>: SteamVR is closing and wants the overlay to let go.</summary>
    internal const uint EventQuit = 700;

    /// <summary><c>VREvent_ProcessQuit</c>: the same request, for this process in particular.</summary>
    internal const uint EventProcessQuit = 701;

    /// <summary><c>VREvent_KeyboardDone</c>: Done was pressed on the keyboard shown for this overlay.</summary>
    internal const uint EventKeyboardDone = 1202;

    /// <summary><c>VREvent_MouseMove</c>: SteamVR's laser moved on a dashboard overlay. Data is mouse.</summary>
    internal const uint EventMouseMove = 300;

    /// <summary><c>VREvent_MouseButtonDown</c>: the trigger went down with SteamVR's laser on it. Data is mouse.</summary>
    internal const uint EventMouseButtonDown = 301;

    /// <summary><c>VREvent_MouseButtonUp</c>: the trigger came back up. Data is mouse.</summary>
    internal const uint EventMouseButtonUp = 302;

    /// <summary><c>VREvent_OverlayHidden</c>: nobody can see the overlay now, as when the dashboard closes.</summary>
    internal const uint EventOverlayHidden = 501;

    /// <summary><c>VROverlayInputMethod_Mouse</c>: SteamVR's own laser sends this overlay mouse events.</summary>
    internal const int InputMethodMouse = 1;

    /// <summary><c>VRMouseButton_Left</c>: the trigger, as SteamVR reports it on a dashboard overlay.</summary>
    internal const uint MouseButtonLeft = 0x0001;

    /// <summary><c>k_EGamepadTextInputModeNormal</c> and <c>k_EGamepadTextInputLineModeSingleLine</c>: plain text, one line.</summary>
    internal const int KeyboardNormalSingleLine = 0;

    /// <summary>
    /// <c>KeyboardFlag_Modal</c>: the keyboard takes the controllers while it is up and goes away
    /// when a ray lands off it, as Valve's header recommends for an overlay's keyboard.
    /// </summary>
    internal const uint KeyboardModal = 1 << 1;

    [LibraryImport("openvr_api", EntryPoint = "VR_InitInternal")]
    internal static partial nint InitInternal(out VrInitError error, int applicationType);

    [LibraryImport("openvr_api", EntryPoint = "VR_ShutdownInternal")]
    internal static partial void ShutdownInternal();

    [LibraryImport("openvr_api", EntryPoint = "VR_IsRuntimeInstalled")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool IsRuntimeInstalled();

    [LibraryImport("openvr_api", EntryPoint = "VR_IsHmdPresent")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool IsHmdPresent();

    [LibraryImport("openvr_api", EntryPoint = "VR_GetGenericInterface", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetGenericInterface(string interfaceVersion, out VrInitError error);
}

/// <summary>
/// Indices into <c>VR_IVROverlay_FnTable</c> for <c>IVROverlay_028</c>.
/// </summary>
/// <remarks>
/// Transcribed from the declaration order in Valve's <c>headers/openvr_capi.h</c>. Only the slots
/// Modbot calls are named; the gaps are real functions that are simply never used. If the pinned
/// interface version in <see cref="OpenVrInterop.OverlayInterfaceVersion"/> ever moves, every one
/// of these has to be re-read from the header for the new version — they are positions, not names,
/// and nothing at runtime will tell you they are wrong.
/// </remarks>
internal static class OverlaySlot
{
    internal const int CreateOverlay = 1;
    internal const int DestroyOverlay = 3;
    internal const int SetOverlayFlag = 11;
    internal const int SetOverlayAlpha = 16;
    internal const int SetOverlaySortOrder = 20;
    internal const int SetOverlayWidthInMeters = 22;
    internal const int SetOverlayCurvature = 24;
    internal const int SetOverlayTransformAbsolute = 33;
    internal const int SetOverlayTransformTrackedDeviceRelative = 35;
    internal const int ShowOverlay = 43;
    internal const int HideOverlay = 44;
    internal const int IsOverlayVisible = 45;
    internal const int PollNextOverlayEvent = 48;
    internal const int SetOverlayInputMethod = 50;
    internal const int SetOverlayMouseScale = 52;
    internal const int SetOverlayTexture = 60;
    internal const int ClearOverlayTexture = 61;
    internal const int SetOverlayRaw = 62;
    internal const int CreateDashboardOverlay = 67;
    internal const int ShowKeyboardForOverlay = 75;
    internal const int GetKeyboardText = 76;
}

/// <summary>
/// <c>HmdMatrix34_t</c>: a 3×4 row-major transform, the rotation in the first three columns and
/// the translation in the fourth.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct HmdMatrix34
{
    public float M00, M01, M02, M03;
    public float M10, M11, M12, M13;
    public float M20, M21, M22, M23;

    /// <summary>No rotation, moved by the given metres along each axis.</summary>
    public static HmdMatrix34 Translation(float x, float y, float z) => new()
    {
        M00 = 1, M11 = 1, M22 = 1,
        M03 = x, M13 = y, M23 = z,
    };
}

/// <summary>
/// <c>VREvent_t</c>, as far as Modbot reads it: the type, and enough room for the rest.
/// </summary>
/// <remarks>
/// SteamVR checks the size it is handed against its own <c>sizeof(VREvent_t)</c>, which is 64
/// bytes with the 8-byte packing the C++ header uses on every platform at the pinned version: three
/// 4-byte fields, 4 bytes of padding, and a 48-byte union of event data Modbot never reads.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct VrEvent
{
    public const uint Size = 64;

    /// <summary>
    /// The size SteamVR compares against: 64 on Windows, where its header packs to 8 bytes, and
    /// 60 on Linux and macOS, where it packs to 4 and the union follows the three fields with no
    /// gap. The wrong number means SteamVR answers no events at all, and a quit is never heard.
    /// </summary>
    public static uint PlatformSize => OperatingSystem.IsWindows() ? 64u : 60u;

    [FieldOffset(0)]
    public uint EventType;

    [FieldOffset(4)]
    public uint TrackedDeviceIndex;

    [FieldOffset(8)]
    public float EventAgeSeconds;

    // VREvent_Mouse_t at the start of the data union: x, y, then the button. The union starts at
    // 16 on Windows, after the 4 bytes of padding, and at 12 elsewhere, so both are laid out here
    // and Mouse picks the one this platform's SteamVR wrote.
    [FieldOffset(12)]
    private float _packed4X;

    [FieldOffset(16)]
    private float _packed4Y;

    [FieldOffset(20)]
    private uint _packed4Button;

    [FieldOffset(16)]
    private float _packed8X;

    [FieldOffset(20)]
    private float _packed8Y;

    [FieldOffset(24)]
    private uint _packed8Button;

    /// <summary>
    /// The event's <c>VREvent_Mouse_t</c>, for a mouse move, press or release on a dashboard
    /// overlay: x and y in the units of the overlay's mouse scale, with the bottom left of the
    /// texture at 0,0 (GL's way round, as the header says), and which button.
    /// </summary>
    public readonly VrMouse Mouse => OperatingSystem.IsWindows()
        ? new VrMouse(_packed8X, _packed8Y, _packed8Button)
        : new VrMouse(_packed4X, _packed4Y, _packed4Button);

    /// <summary>A mouse event as SteamVR would write it on this platform, for the tests.</summary>
    public static VrEvent MouseEvent(uint eventType, float x, float y, uint button)
    {
        var made = new VrEvent { EventType = eventType };
        if (OperatingSystem.IsWindows())
        {
            made._packed8X = x;
            made._packed8Y = y;
            made._packed8Button = button;
        }
        else
        {
            made._packed4X = x;
            made._packed4Y = y;
            made._packed4Button = button;
        }

        return made;
    }
}

/// <summary><c>VREvent_Mouse_t</c>'s first three fields: where, bottom left at 0,0, and which button.</summary>
public readonly record struct VrMouse(float X, float Y, uint Button);

/// <summary><c>HmdVector2_t</c>: two floats, for the mouse scale.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct HmdVector2
{
    public float X;
    public float Y;
}
