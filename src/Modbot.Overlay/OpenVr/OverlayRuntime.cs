using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Rendering;

namespace Modbot.Overlay.OpenVr;

/// <summary>Why the overlay is not showing.</summary>
public enum OverlayRuntimeState
{
    /// <summary>Attached to a runtime (SteamVR, WiVRn or Monado) with a live overlay.</summary>
    Running,

    /// <summary>
    /// No runtime is installed. Not a fault: plenty of moderators run the companion purely to
    /// report presence and never put a headset on.
    /// </summary>
    NoRuntime,

    /// <summary>
    /// A runtime is installed but not running, or it was running and has since closed. The
    /// companion keeps looking and attaches when it is.
    /// </summary>
    NotStarted,

    /// <summary>The runtime answered, and said no. The reason is carried alongside.</summary>
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

    /// <summary>
    /// Where the head and the controllers are now and what is pressed, in the room. None when
    /// not attached, or when the runtime has no way to say.
    /// </summary>
    OverlayTracking ReadTracking();

    /// <summary>Puts the panel where the placement says. Remembered, and applied on attach.</summary>
    void Place(OverlayPlacement placement);
}

/// <summary>
/// One overlay in SteamVR, through OpenVR's overlay interface.
/// </summary>
/// <remarks>
/// <para><strong>What this does.</strong> Registers one overlay with SteamVR, puts it where its
/// placement says so it travels with the headset or a hand, and hands the compositor a picture
/// whenever the content changes: a Direct3D texture on Windows, the raw pixels everywhere else.
/// That is the entire interaction: no other application's overlay is inspected, and nothing is
/// sent anywhere.</para>
/// <para><strong>One attachment, as many overlays as there are panels.</strong> The attachment to
/// SteamVR itself — the init, the function table and the tracking — is
/// <see cref="OpenVrSession"/>, shared, because OpenVR's entry points are process-wide. What this
/// type owns is one overlay: its key, its handle, its placement and its picture. Modbot has two,
/// the main panel and the notification panel, and either can be switched off without disturbing
/// the other (two overlay modes design §4.1).</para>
/// <para><strong>It never starts SteamVR.</strong> The session asks as a background application
/// first, which is only ever answered by a SteamVR that is already up. While SteamVR is down the
/// answer is a state, the companion asks again every few seconds, and the overlay appears when
/// the moderator starts SteamVR themselves. When SteamVR closes it says so, and the overlays let
/// go.</para>
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

    /// <summary>The notification panel's own key, for the same reason and with the same promise.</summary>
    public const string NotificationOverlayKey = "moe.bin.modbot.notifications";

    /// <summary>
    /// Where the panel sits relative to the headset, in metres: right of centre, below the eye
    /// line, and an arm's length forward. Out of the middle of the view, where the instance is,
    /// and inside it, where a glance finds it.
    /// </summary>
    public static readonly (float X, float Y, float Z) Placement = (0.35f, -0.28f, -1.0f);

    /// <summary>How wide the panel is in the headset. Sharpness is the texture's resolution, set separately.</summary>
    public const float DefaultWidthInMetres = 0.45f;

    /// <summary>
    /// Drawn over the main panel where the two happen to overlap: a pop-up hidden behind the
    /// roster would be a pop-up nobody sees.
    /// </summary>
    private const uint NotificationSortOrder = 100;

    private readonly OverlayKind _kind;
    private readonly OpenVrSession _session;
    private readonly string _overlayName;
    private readonly float _widthInMetres;

    private ulong _handle;
    private int _generation;
    private bool _holdsSession;
    private byte[]? _rgba;
    private OverlayPlacement _placement;

    /// <param name="kind">Which panel this is; it decides the key, the name and the sort order.</param>
    /// <param name="overlayName">What SteamVR calls it in its own lists. Null takes the kind's name.</param>
    /// <param name="widthInMetres">The starting width, until a placement says otherwise.</param>
    /// <param name="session">The attachment to share. Null takes the client's one.</param>
    public OpenVrOverlayRuntime(
        OverlayKind kind = OverlayKind.Main,
        string? overlayName = null,
        float widthInMetres = DefaultWidthInMetres,
        OpenVrSession? session = null)
    {
        _kind = kind;
        _session = session ?? OpenVrSession.Shared;
        _overlayName = overlayName ?? (kind is OverlayKind.Notification ? "Modbot notifications" : "Modbot");
        _widthInMetres = widthInMetres;
        _placement = OverlayPlacement.Default with { Width = widthInMetres };
    }

    /// <summary>Which panel this overlay draws.</summary>
    public OverlayKind Kind => _kind;

    /// <summary>The key SteamVR knows this overlay by.</summary>
    public string Key => _kind is OverlayKind.Notification ? NotificationOverlayKey : OverlayKey;

    /// <summary>The width this overlay was built with, before any placement moved it.</summary>
    public float StartingWidth => _widthInMetres;

    /// <summary>The placement last asked for, applied as soon as there is an overlay to apply it to.</summary>
    public OverlayPlacement CurrentPlacement => _placement;

    public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted, Detail: "SteamVR has not been looked for yet.");

    public OverlayRuntimeStatus Start()
    {
        if (Status.State is OverlayRuntimeState.Running && _handle != 0 && _generation == _session.Generation)
            return Status;

        var opened = _session.Open();
        if (opened.State is not OverlayRuntimeState.Running)
            return Status = opened;

        _holdsSession = true;
        _generation = _session.Generation;

        if (CreateOverlay() is { } failure)
        {
            _holdsSession = false;
            _session.Release();
            return Status = failure;
        }

        // Named, because the same page and log line serve the OpenXR runtime too.
        return Status = new(OverlayRuntimeState.Running, Detail: "Attached to SteamVR.");
    }

    public void Poll()
    {
        // Another overlay heard SteamVR close and let the whole attachment go. This handle now
        // belongs to an attachment that no longer exists, so it is dropped rather than destroyed:
        // DestroyOverlay would go through a function table that has already been shut down.
        if (_handle != 0 && _generation != _session.Generation)
        {
            _handle = 0;
            _holdsSession = false;
            Status = new(OverlayRuntimeState.NotStarted, Detail: "SteamVR closed.");
            return;
        }

        if (_handle == 0)
            return;

        unsafe
        {
            var poll = (delegate* unmanaged[Stdcall]<ulong, VrEvent*, uint, byte>)Slot(OverlaySlot.PollNextOverlayEvent);
            VrEvent vrEvent;

            // A few at a time, never "until empty": a runtime that never stops answering must not
            // hold the companion's UI thread.
            for (var i = 0; i < 16 && poll(_handle, &vrEvent, VrEvent.PlatformSize) != 0; i++)
            {
                if (vrEvent.EventType is OpenVrInterop.EventQuit or OpenVrInterop.EventProcessQuit)
                {
                    // SteamVR is closing and expects every overlay to let go. The picture, the
                    // cache and the loops all stay; the next Start() attaches again when it is back.
                    DestroyHandle();
                    _holdsSession = false;
                    _session.Close();
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

    public OverlayTracking ReadTracking()
        => _handle != 0 ? _session.ReadTracking() : OverlayTracking.None;

    public void Place(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        _placement = placement.Clamped();
        if (_handle != 0)
            Apply(_placement);
    }

    /// <summary>
    /// Width, opacity, curve and where: on the headset, on a controller, or fixed in the room
    /// (SteamVR's standing space, the one the room's poses are read in).
    /// </summary>
    private unsafe void Apply(OverlayPlacement placement)
    {
        ((delegate* unmanaged[Stdcall]<ulong, float, int>)Slot(OverlaySlot.SetOverlayWidthInMeters))(_handle, placement.Width);
        ((delegate* unmanaged[Stdcall]<ulong, float, int>)Slot(OverlaySlot.SetOverlayAlpha))(_handle, placement.Opacity);
        ((delegate* unmanaged[Stdcall]<ulong, float, int>)Slot(OverlaySlot.SetOverlayCurvature))(_handle, placement.Curve);

        var transform = HmdPoses.ToMatrix(Pose.From(placement.Offset));

        uint? device = placement.Anchor switch
        {
            OverlayAnchor.Head => OpenVrInterop.TrackedDeviceIndexHmd,
            OverlayAnchor.LeftHand => _session.DeviceIndex(Interaction.Hand.Left),
            OverlayAnchor.RightHand => _session.DeviceIndex(Interaction.Hand.Right),
            _ => null,
        };

        if (placement.Anchor is OverlayAnchor.World)
        {
            ((delegate* unmanaged[Stdcall]<ulong, int, HmdMatrix34*, int>)Slot(OverlaySlot.SetOverlayTransformAbsolute))(
                _handle, OpenVrInterop.TrackingUniverseStanding, &transform);
            return;
        }

        // A hand that is not there right now: the panel waits on the headset, at its offset, and
        // is put on the hand the next time the placement is applied.
        ((delegate* unmanaged[Stdcall]<ulong, uint, HmdMatrix34*, int>)Slot(OverlaySlot.SetOverlayTransformTrackedDeviceRelative))(
            _handle, device ?? OpenVrInterop.TrackedDeviceIndexHmd, &transform);
    }

    public void Dispose()
    {
        // A handle from an attachment that has already gone is dropped, never destroyed.
        if (_generation == _session.Generation)
            DestroyHandle();
        else
            _handle = 0;

        if (_holdsSession)
        {
            _holdsSession = false;
            _session.Release();
        }

        Status = new(OverlayRuntimeState.NotStarted, Detail: "The overlay has been let go.");
    }

    private void DestroyHandle()
    {
        if (_handle == 0)
            return;

        unsafe
        {
            ((delegate* unmanaged[Stdcall]<ulong, int>)Slot(OverlaySlot.DestroyOverlay))(_handle);
        }

        _handle = 0;
    }

    private unsafe OverlayRuntimeStatus? CreateOverlay()
    {
        ulong handle = 0;
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(Key + "\0");
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(_overlayName + "\0");

        int created;
        fixed (byte* key = keyBytes)
        fixed (byte* name = nameBytes)
        {
            var create = (delegate* unmanaged[Stdcall]<byte*, byte*, ulong*, int>)Slot(OverlaySlot.CreateOverlay);
            created = create(key, name, &handle);
        }

        if (created != 0 || handle == 0)
            return new(OverlayRuntimeState.Refused, Detail: $"SteamVR refused to create the overlay (error {created}).");

        _handle = handle;

        // Width in metres, not pixels: the texture's resolution controls sharpness and the
        // placement's width controls apparent size. The default is a panel about the width of a
        // sheet of paper at arm's length, on the headset itself so it needs no controller to be
        // found; a moderator who has moved it gets it back where they left it.
        Apply(_placement);

        if (_kind is OverlayKind.Notification)
            ((delegate* unmanaged[Stdcall]<ulong, uint, int>)Slot(OverlaySlot.SetOverlaySortOrder))(handle, NotificationSortOrder);

        // Shown from the start. The idle screen is drawn when there is nothing to say, so the
        // panel is a fixture of the headset rather than something that appears and vanishes.
        ((delegate* unmanaged[Stdcall]<ulong, int>)Slot(OverlaySlot.ShowOverlay))(handle);

        return null;
    }

    private nint Slot(int index) => _session.Slot(index);
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

    /// <summary>What <see cref="ReadTracking"/> answers; tests set it.</summary>
    public OverlayTracking Tracking { get; set; } = OverlayTracking.None;

    /// <summary>The placement last applied, for tests.</summary>
    public OverlayPlacement Placement { get; private set; } = OverlayPlacement.Default;

    public OverlayTracking ReadTracking() => Tracking;

    public void Place(OverlayPlacement placement) => Placement = placement;

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
