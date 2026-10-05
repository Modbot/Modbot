using System.Runtime.InteropServices;

namespace Modbot.VrShot;

/// <summary>
/// The few OpenVR entry points this tool calls, hand-written as in Modbot.Overlay's
/// OpenVrInterop, against the openvr_api.dll the overlay vendors (v2.15.6).
/// </summary>
internal static partial class OpenVr
{
    /// <summary>
    /// VRApplication_Background: SteamVR answers it only when it is already running, never starts
    /// for it, never gives it the headset and never makes it the scene app.
    /// </summary>
    internal const int ApplicationTypeBackground = 3;

    /// <summary>VRInitError_Init_NoServerForBackgroundApp: SteamVR is not running.</summary>
    internal const int InitNoServerForBackgroundApp = 121;

    /// <summary>VRInitError_Init_HmdNotFound.</summary>
    internal const int InitHmdNotFound = 108;

    /// <summary>
    /// The interface version the slot numbers below were read from (openvr_capi.h, v2.15.6).
    /// The table is positional: a SteamVR that does not speak this version refuses it, which is
    /// the safe answer.
    /// </summary>
    internal const string CompositorInterfaceVersion = "FnTable:IVRCompositor_029";

    /// <summary>VR_IVRCompositor_FnTable slot of GetMirrorTextureD3D11.</summary>
    internal const int SlotGetMirrorTextureD3D11 = 35;

    /// <summary>VR_IVRCompositor_FnTable slot of ReleaseMirrorTextureD3D11.</summary>
    internal const int SlotReleaseMirrorTextureD3D11 = 36;

    /// <summary>The system interface version, from the same header.</summary>
    internal const string SystemInterfaceVersion = "FnTable:IVRSystem_026";

    /// <summary>VR_IVRSystem_FnTable slot of GetOutputDevice.</summary>
    internal const int SlotGetOutputDevice = 9;

    /// <summary>
    /// IVRSystem::GetOutputDevice with TextureType_DirectX: Windows' id (LUID) of the graphics card
    /// the headset is on, or 0 when SteamVR does not say.
    /// </summary>
    internal static unsafe long GetOutputDevice(nint system)
    {
        var fn = (delegate* unmanaged[Stdcall]<ulong*, int, void*, void>)((nint*)system)[SlotGetOutputDevice];
        ulong card = 0;
        fn(&card, 0, null);
        return (long)card;
    }

    [LibraryImport("openvr_api", EntryPoint = "VR_InitInternal")]
    internal static partial nint InitInternal(out int error, int applicationType);

    [LibraryImport("openvr_api", EntryPoint = "VR_ShutdownInternal")]
    internal static partial void ShutdownInternal();

    [LibraryImport("openvr_api", EntryPoint = "VR_IsRuntimeInstalled")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool IsRuntimeInstalled();

    [LibraryImport("openvr_api", EntryPoint = "VR_GetGenericInterface", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetGenericInterface(string interfaceVersion, out int error);

    [LibraryImport("openvr_api", EntryPoint = "VR_GetVRInitErrorAsSymbol")]
    internal static partial nint InitErrorSymbol(int error);

    internal static string InitErrorName(int error) =>
        Marshal.PtrToStringUTF8(InitErrorSymbol(error)) ?? error.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// IVRCompositor::GetMirrorTextureD3D11: a shader resource view, on the given device, of the
    /// compositor's undistorted image for one eye. Returns an EVRCompositorError (0 is none).
    /// </summary>
    internal static unsafe int GetMirrorTextureD3D11(nint compositor, int eye, nint device, out nint view)
    {
        var fn = (delegate* unmanaged[Stdcall]<int, nint, nint*, int>)((nint*)compositor)[SlotGetMirrorTextureD3D11];
        nint result = 0;
        var error = fn(eye, device, &result);
        view = result;
        return error;
    }

    /// <summary>IVRCompositor::ReleaseMirrorTextureD3D11: gives the view back, as the header asks, instead of Release.</summary>
    internal static unsafe void ReleaseMirrorTextureD3D11(nint compositor, nint view)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, void>)((nint*)compositor)[SlotReleaseMirrorTextureD3D11];
        fn(view);
    }
}
