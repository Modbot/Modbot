using System.Runtime.InteropServices;
using Modbot.Overlay.Rendering;

namespace Modbot.Overlay.OpenVr;

/// <summary>Why the overlay is not showing.</summary>
public enum OverlayRuntimeState
{
    /// <summary>Attached to SteamVR with a live overlay.</summary>
    Running,

    /// <summary>
    /// SteamVR is not installed. Not a fault: plenty of moderators run the companion purely to
    /// report presence and never put a headset on.
    /// </summary>
    NoRuntime,

    /// <summary>
    /// SteamVR is installed but not running, or it was running and has since closed. The
    /// companion keeps looking and attaches when it is.
    /// </summary>
    NotStarted,

    /// <summary>SteamVR answered, and said no. The reason is carried alongside.</summary>
    Refused,
}

public sealed record OverlayRuntimeStatus(OverlayRuntimeState State, VrInitError Error = VrInitError.None, string? Detail = null);

/// <summary>The headset side of the overlay, behind an interface so the rest can be tested.</summary>
public interface IOverlayRuntime : IDisposable
{
    OverlayRuntimeStatus Status { get; }

    /// <summary>
    /// Attaches to SteamVR if it is running. Never starts SteamVR. Safe to call again while the
    /// answer is <see cref="OverlayRuntimeState.NotStarted"/>, which is how the companion picks
    /// SteamVR up whenever the moderator starts it.
    /// </summary>
    OverlayRuntimeStatus Start();

    /// <summary>
    /// Handles what SteamVR has said since the last call -- above all, that it is closing, which
    /// detaches the overlay and puts the runtime back to <see cref="OverlayRuntimeState.NotStarted"/>.
    /// </summary>
    void Poll();

    /// <summary>Hands the compositor the texture to draw. Cheap, and only called on a change.</summary>
    bool Submit(IOverlaySurface surface);

    void Show();

    void Hide();
}

/// <summary>
/// SteamVR, through OpenVR's overlay interface.
/// </summary>
/// <remarks>
/// <para><strong>What this does.</strong> Registers one overlay with SteamVR, places it a little
/// below and to the right of where the moderator is looking so it travels with the headset, and
/// hands the compositor a picture whenever the content changes: a Direct3D texture on Windows,
/// the raw pixels everywhere else. That is the entire interaction: no headset tracking data is
/// read, no other application's overlay is inspected, and nothing is sent anywhere.</para>
/// <para><strong>It never starts SteamVR.</strong> OpenVR's overlay mode launches SteamVR when it
/// is not running, which is the last thing a program that starts with the computer should do. So
/// the companion first asks as a background application, which is only ever answered by a SteamVR
/// that is already up; only then does it attach as an overlay. While SteamVR is down the answer is
/// a state, the companion asks again every few seconds, and the overlay appears when the moderator
/// starts SteamVR themselves. When SteamVR closes it says so, and the overlay lets go.</para>
/// <para><strong>Absence is not failure.</strong> SteamVR missing, not running, or without a
/// headset are all ordinary conditions for a moderator reporting presence from the desktop. They
/// are reported as states, never as errors, and the companion goes on working without an
/// overlay.</para>
/// <para><strong>The companion still cannot be commanded.</strong> An overlay is a surface, not a
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

    /// <summary>
    /// Where the panel sits relative to the headset, in metres: right of centre, below the eye
    /// line, and an arm's length forward. Out of the middle of the view, where the instance is,
    /// and inside it, where a glance finds it.
    /// </summary>
    public static readonly (float X, float Y, float Z) Placement = (0.35f, -0.28f, -1.0f);

    private readonly string _overlayName;
    private readonly float _widthInMetres;

    private nint _fnTable;
    private ulong _handle;
    private byte[]? _rgba;

    /// <summary>How wide the panel is in the headset. Sharpness is the texture's resolution, set separately.</summary>
    public const float DefaultWidthInMetres = 0.45f;

    public OpenVrOverlayRuntime(string overlayName = "Modbot", float widthInMetres = DefaultWidthInMetres)
    {
        _overlayName = overlayName;
        _widthInMetres = widthInMetres;
    }

    public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted, Detail: "SteamVR has not been looked for yet.");

    public OverlayRuntimeStatus Start()
    {
        if (Status.State is OverlayRuntimeState.Running)
            return Status;

        try
        {
            if (!OpenVrInterop.IsRuntimeInstalled())
                return Status = new(OverlayRuntimeState.NoRuntime, Detail: "SteamVR is not installed on this PC.");

            // The knock on the door. A background application is refused unless SteamVR is already
            // running, and refusing is all it does: nothing is launched.
            OpenVrInterop.InitInternal(out var probeError, OpenVrInterop.ApplicationTypeBackground);
            if (probeError != VrInitError.None)
            {
                var state = probeError is VrInitError.Init_NoServerForBackgroundApp
                    or VrInitError.Init_HmdNotFound
                    or VrInitError.Init_PathRegistryNotFound
                    or VrInitError.Init_NotInitialized
                    ? OverlayRuntimeState.NotStarted
                    : OverlayRuntimeState.Refused;

                return Status = new(
                    state,
                    probeError,
                    state is OverlayRuntimeState.NotStarted ? "SteamVR is not running." : $"SteamVR answered {probeError}.");
            }

            OpenVrInterop.ShutdownInternal();

            OpenVrInterop.InitInternal(out var initError, OpenVrInterop.ApplicationTypeOverlay);
            if (initError != VrInitError.None)
                return Status = new(OverlayRuntimeState.Refused, initError, $"SteamVR answered {initError}.");

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
            // openvr_api is shipped beside the overlay; its absence means a broken install, not a
            // missing headset, and saying so saves somebody a long wrong search.
            return Status = new(
                OverlayRuntimeState.NoRuntime,
                Detail: "The OpenVR library is missing from the Modbot installation.");
        }
    }

    public void Poll()
    {
        if (_handle == 0)
            return;

        unsafe
        {
            var poll = (delegate* unmanaged[Stdcall]<ulong, VrEvent*, uint, byte>)Slot(OverlaySlot.PollNextOverlayEvent);
            VrEvent vrEvent;

            // A few at a time, never "until empty": a runtime that never stops answering must not
            // hold the companion's UI thread.
            for (var i = 0; i < 16 && poll(_handle, &vrEvent, VrEvent.Size) != 0; i++)
            {
                if (vrEvent.EventType is OpenVrInterop.EventQuit or OpenVrInterop.EventProcessQuit)
                {
                    // SteamVR is closing and expects the overlay to let go. The picture, the cache
                    // and the loop all stay; the next Start() attaches again when it is back.
                    Dispose();
                    Status = new(OverlayRuntimeState.NotStarted, Detail: "SteamVR closed.");
                    return;
                }
            }
        }
    }

    public bool Submit(IOverlaySurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (_handle == 0)
            return false;

        unsafe
        {
            if (surface.TextureHandle != 0)
            {
                var texture = new VrTexture
                {
                    Handle = surface.TextureHandle,
                    Type = VrTextureType.DirectX,
                    ColorSpace = VrColorSpace.Auto,
                };

                var setTexture = (delegate* unmanaged[Stdcall]<ulong, VrTexture*, int>)Slot(OverlaySlot.SetOverlayTexture);
                return setTexture(_handle, &texture) == 0;
            }

            // No graphics device to share: the picture goes over as bytes. SteamVR reads them as
            // RGBA, and Avalonia hands them out as BGRA, so the two colour channels swap on the way.
            var pixels = surface.Pixels.Span;
            if (pixels.Length != surface.Width * surface.Height * 4)
                return false;

            _rgba ??= new byte[pixels.Length];
            RawPixels.BgraToRgba(pixels, _rgba);

            fixed (byte* raw = _rgba)
            {
                var setRaw = (delegate* unmanaged[Stdcall]<ulong, void*, uint, uint, uint, int>)Slot(OverlaySlot.SetOverlayRaw);
                return setRaw(_handle, raw, (uint)surface.Width, (uint)surface.Height, 4) == 0;
            }
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

        Status = new(OverlayRuntimeState.NotStarted, Detail: "The overlay has been let go.");
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

        // Attached to the headset itself, so it needs no controller to be found and follows the
        // moderator wherever they look.
        var transform = HmdMatrix34.Translation(Placement.X, Placement.Y, Placement.Z);
        ((delegate* unmanaged[Stdcall]<ulong, uint, HmdMatrix34*, int>)Slot(OverlaySlot.SetOverlayTransformTrackedDeviceRelative))(
            handle, OpenVrInterop.TrackedDeviceIndexHmd, &transform);

        // Shown from the start. The idle screen is drawn when there is nothing to say, so the
        // panel is a fixture of the headset rather than something that appears and vanishes.
        ((delegate* unmanaged[Stdcall]<ulong, int>)Slot(OverlaySlot.ShowOverlay))(handle);

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

/// <summary>Turning Avalonia's pixels into what SteamVR reads.</summary>
public static class RawPixels
{
    /// <summary>BGRA to RGBA, one pixel at a time. Both buffers must be the same length, a multiple of four.</summary>
    public static void BgraToRgba(ReadOnlySpan<byte> bgra, Span<byte> rgba)
    {
        if (rgba.Length != bgra.Length || bgra.Length % 4 != 0)
            throw new ArgumentException("The two buffers must be the same length and hold whole pixels.", nameof(rgba));

        for (var i = 0; i < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }
    }
}

/// <summary>
/// The overlay runtime on a machine with no SteamVR: every call succeeds and nothing is drawn.
/// </summary>
/// <remarks>
/// Used when the moderator has no headset, and by the tests. It exists so the rest of the overlay
/// — the cache, the compositor, the views — has somewhere to run that is not "SteamVR is
/// installed", which most machines running this companion will not be.
/// </remarks>
public sealed class HeadlessOverlayRuntime : IOverlayRuntime
{
    public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted);

    public int Submissions { get; private set; }

    public bool IsShowing { get; private set; }

    public OverlayRuntimeStatus Start() => Status = new(OverlayRuntimeState.NoRuntime, Detail: "No headset.");

    public void Poll()
    {
    }

    public bool Submit(IOverlaySurface surface)
    {
        Submissions++;
        return true;
    }

    public void Show() => IsShowing = true;

    public void Hide() => IsShowing = false;

    public void Dispose() => IsShowing = false;
}
