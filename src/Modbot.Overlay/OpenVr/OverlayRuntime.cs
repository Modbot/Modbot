using Modbot.Overlay.Rendering;

namespace Modbot.Overlay.OpenVr;

/// <summary>Why the overlay is not showing.</summary>
public enum OverlayRuntimeState
{
    /// <summary>Attached to SteamVR with a live overlay.</summary>
    Running,

    /// <summary>
    /// SteamVR is not installed. Not a fault: plenty of moderators run the client purely to report
    /// presence and never put a headset on.
    /// </summary>
    NoRuntime,

    /// <summary>SteamVR is installed but not running, or no headset is connected.</summary>
    NotStarted,

    /// <summary>SteamVR answered, and said no. The reason is carried alongside.</summary>
    Refused,
}

public sealed record OverlayRuntimeStatus(OverlayRuntimeState State, VrInitError Error = VrInitError.None, string? Detail = null);

/// <summary>The headset side of the overlay, behind an interface so the rest can be tested.</summary>
public interface IOverlayRuntime : IDisposable
{
    OverlayRuntimeStatus Status { get; }

    OverlayRuntimeStatus Start();

    /// <summary>Hands the compositor the texture to draw. Cheap, and only called on a change.</summary>
    bool Submit(IOverlaySurface surface);

    void Show();

    void Hide();
}

/// <summary>
/// SteamVR, through OpenVR's overlay interface.
/// </summary>
/// <remarks>
/// <para><strong>What this does.</strong> Registers one overlay with SteamVR, attaches it to the
/// left controller so it travels with the moderator's hand rather than being pinned in the world,
/// and hands the compositor a Direct3D texture whenever the content changes. That is the entire
/// interaction: no headset tracking data is read, no other application's overlay is inspected, and
/// nothing is sent anywhere.</para>
/// <para><strong>Absence is not failure.</strong> SteamVR missing, not running, or without a
/// headset are all ordinary conditions for a moderator reporting presence from the desktop. They
/// are reported as states, never as errors, and the client goes on working without an
/// overlay.</para>
/// <para><strong>The client still cannot be commanded.</strong> An overlay is a surface, not a
/// channel: nothing SteamVR or a Modbot server says reaches this program as an instruction. A
/// moderator acting on what they see here goes through the normal authenticated API as themselves
/// — the device token is ingest-scoped and cannot ban anybody.</para>
/// </remarks>
public sealed class OpenVrOverlayRuntime : IOverlayRuntime
{
    /// <summary>
    /// Stable for the life of the product. SteamVR keys overlay settings — position, curvature,
    /// the moderator's own adjustments — on this string, and changing it discards them.
    /// </summary>
    public const string OverlayKey = "moe.bin.modbot.overlay";

    private readonly string _overlayName;
    private readonly float _widthInMetres;

    private nint _fnTable;
    private ulong _handle;

    public OpenVrOverlayRuntime(string overlayName = "Modbot", float widthInMetres = 0.45f)
    {
        _overlayName = overlayName;
        _widthInMetres = widthInMetres;
    }

    public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted);

    public OverlayRuntimeStatus Start()
    {
        try
        {
            if (!OpenVrInterop.IsRuntimeInstalled())
                return Status = new(OverlayRuntimeState.NoRuntime, Detail: "SteamVR is not installed on this PC.");

            OpenVrInterop.InitInternal(out var initError, OpenVrInterop.ApplicationTypeOverlay);
            if (initError != VrInitError.None)
            {
                // Not started and no headset are the same thing to a moderator, and neither is
                // worth an alarm: the client keeps reporting presence either way.
                var state = initError is VrInitError.Init_HmdNotFound
                    or VrInitError.Init_NoServerForBackgroundApp
                    or VrInitError.Init_PathRegistryNotFound
                    ? OverlayRuntimeState.NotStarted
                    : OverlayRuntimeState.Refused;

                return Status = new(state, initError, $"SteamVR answered {initError}.");
            }

            _fnTable = OpenVrInterop.GetGenericInterface(OpenVrInterop.OverlayInterfaceVersion, out var interfaceError);
            if (_fnTable == 0 || interfaceError != VrInitError.None)
            {
                // A SteamVR whose IVROverlay is a version this build was not written against.
                // Refusing is the only safe answer: the function table is positional, so guessing
                // would call the wrong function rather than fail.
                OpenVrInterop.ShutdownInternal();
                return Status = new(
                    OverlayRuntimeState.Refused,
                    interfaceError,
                    $"This build speaks {OpenVrInterop.OverlayInterfaceVersion}; SteamVR does not.");
            }

            if (CreateOverlay() is { } failure)
                return Status = failure;

            return Status = new(OverlayRuntimeState.Running);
        }
        catch (DllNotFoundException)
        {
            // openvr_api.dll is shipped beside the overlay; its absence means a broken install,
            // not a missing headset, and saying so saves somebody a long wrong search.
            return Status = new(
                OverlayRuntimeState.NoRuntime,
                Detail: "openvr_api.dll is missing from the Modbot installation.");
        }
    }

    public bool Submit(IOverlaySurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (_handle == 0 || surface.TextureHandle == 0)
            return false;

        var texture = new VrTexture
        {
            Handle = surface.TextureHandle,
            Type = VrTextureType.DirectX,
            ColorSpace = VrColorSpace.Auto,
        };

        unsafe
        {
            var setTexture = (delegate* unmanaged[Stdcall]<ulong, VrTexture*, int>)Slot(OverlaySlot.SetOverlayTexture);
            return setTexture(_handle, &texture) == 0;
        }
    }

    public void Show()
    {
        if (_handle == 0)
            return;

        unsafe
        {
            ((delegate* unmanaged[Stdcall]<ulong, int>)Slot(OverlaySlot.ShowOverlay))(_handle);
        }
    }

    public void Hide()
    {
        if (_handle == 0)
            return;

        unsafe
        {
            ((delegate* unmanaged[Stdcall]<ulong, int>)Slot(OverlaySlot.HideOverlay))(_handle);
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            unsafe
            {
                ((delegate* unmanaged[Stdcall]<ulong, int>)Slot(OverlaySlot.DestroyOverlay))(_handle);
            }

            _handle = 0;
        }

        if (_fnTable != 0)
        {
            OpenVrInterop.ShutdownInternal();
            _fnTable = 0;
        }

        Status = new(OverlayRuntimeState.NotStarted);
    }

    private unsafe OverlayRuntimeStatus? CreateOverlay()
    {
        ulong handle = 0;
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(OverlayKey + "\0");
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(_overlayName + "\0");

        int created;
        fixed (byte* key = keyBytes)
        fixed (byte* name = nameBytes)
        {
            var create = (delegate* unmanaged[Stdcall]<byte*, byte*, ulong*, int>)Slot(OverlaySlot.CreateOverlay);
            created = create(key, name, &handle);
        }

        if (created != 0 || handle == 0)
        {
            OpenVrInterop.ShutdownInternal();
            _fnTable = 0;
            return new(OverlayRuntimeState.Refused, Detail: $"SteamVR refused to create the overlay (error {created}).");
        }

        _handle = handle;

        // Width in metres, not pixels: the texture's resolution controls sharpness and this
        // controls apparent size. A panel about the width of a sheet of paper at arm's length is
        // readable without filling the moderator's view of the instance they are moderating.
        ((delegate* unmanaged[Stdcall]<ulong, float, int>)Slot(OverlaySlot.SetOverlayWidthInMeters))(handle, _widthInMetres);

        return null;
    }

    private nint Slot(int index)
    {
        if (_fnTable == 0)
            throw new InvalidOperationException("The overlay runtime has not been started.");

        unsafe
        {
            return ((nint*)_fnTable)[index];
        }
    }
}

/// <summary>
/// The overlay runtime on a machine with no SteamVR: every call succeeds and nothing is drawn.
/// </summary>
/// <remarks>
/// Used when the moderator has no headset, and by the tests. It exists so the rest of the overlay
/// — the cache, the compositor, the views — has somewhere to run that is not "SteamVR is
/// installed", which most machines running this client will not be.
/// </remarks>
public sealed class HeadlessOverlayRuntime : IOverlayRuntime
{
    public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted);

    public int Submissions { get; private set; }

    public bool IsShowing { get; private set; }

    public OverlayRuntimeStatus Start() => Status = new(OverlayRuntimeState.NoRuntime, Detail: "No headset.");

    public bool Submit(IOverlaySurface surface)
    {
        Submissions++;
        return true;
    }

    public void Show() => IsShowing = true;

    public void Hide() => IsShowing = false;

    public void Dispose() => IsShowing = false;
}
