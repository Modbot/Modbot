using System.Runtime.InteropServices;

namespace Modbot.Overlay.OpenVr;

/// <summary>Why <c>VR_Init</c> refused. Only the values Modbot distinguishes are named.</summary>
public enum VrInitError
{
    None = 0,
    Unknown = 1,
    Init_InstallationNotFound = 100,
    Init_HmdNotFound = 108,
    Init_NoServerForBackgroundApp = 111,
    Init_PathRegistryNotFound = 114,
    Init_VRDashboardNotFound = 132,
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
    internal const int SetOverlayTransformTrackedDeviceRelative = 35;
    internal const int ShowOverlay = 43;
    internal const int HideOverlay = 44;
    internal const int IsOverlayVisible = 45;
    internal const int SetOverlayTexture = 60;
    internal const int ClearOverlayTexture = 61;
}
