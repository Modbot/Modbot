using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.OpenXr;
using Modbot.Overlay.Rendering;

namespace Modbot.Overlay;

/// <summary>
/// OpenVR first, then OpenXR: one runtime for the companion to hold, whichever headset stack the
/// machine has.
/// </summary>
/// <remarks>
/// <para><strong>Why this order.</strong> On a machine with SteamVR — Windows, or SteamVR for
/// Linux — OpenVR is the right answer, and SteamVR's own OpenXR has no overlay extension, so
/// trying OpenXR there would only produce a second refusal. OpenXR is tried when OpenVR reports
/// <strong>no runtime</strong> (nothing installed) or <strong>InvalidApplicationType</strong>,
/// which is xrizer refusing an overlay on a WiVRn or Monado machine. Any other OpenVR answer,
/// "not running" above all, is final for that attempt: the SteamVR that is installed is the one
/// to wait for, and the companion's ten-second look tries again from the top.</para>
/// <para><strong>Whose status shows.</strong> The attached runtime's, or otherwise the more
/// telling of the two failures: the one that can still change. A not-running over a refusal,
/// because the companion's ten-second look goes on only while something is not running, and the
/// one refusal that can meet a not-running here is xrizer's, which is by design and permanent. A
/// refusal over a not-installed. OpenXR's over OpenVR's when both refused, because on the machine
/// where both are reached OpenXR is the one that could have worked (overlay-on-OpenXR spec,
/// 3.1).</para>
/// </remarks>
public sealed class FallbackOverlayRuntime : IOverlayRuntime
{
    private readonly IOverlayRuntime _openVr;
    private readonly IOverlayRuntime _openXr;
    private IOverlayRuntime? _attached;
    private OverlayPlacement _placement = OverlayPlacement.Default;

    public FallbackOverlayRuntime(IOverlayRuntime openVr, IOverlayRuntime openXr)
    {
        ArgumentNullException.ThrowIfNull(openVr);
        ArgumentNullException.ThrowIfNull(openXr);

        _openVr = openVr;
        _openXr = openXr;
    }

    /// <summary>The ordinary construction: SteamVR through OpenVR, then WiVRn or Monado through OpenXR.</summary>
    public static FallbackOverlayRuntime Create(int resolution = OverlayHost.DefaultResolution)
        => new(new OpenVrOverlayRuntime(), new OpenXrOverlayRuntime(resolution));

    /// <summary>
    /// One of Modbot's two panels, sharing what the VR runtimes insist on sharing and owning
    /// everything else.
    /// </summary>
    /// <remarks>
    /// <para>On OpenVR that is one attachment (<see cref="OpenVrSession"/>) with an overlay handle
    /// each, because OpenVR's init is process-wide but overlays are not. On OpenXR it is one
    /// session with a layer each, because a second session would mean a second Vulkan device and a
    /// second frame thread for a second quad (two overlay modes design §4.3).</para>
    /// <para>Either panel can be built, started, stopped and placed without the other existing at
    /// all, which is what lets the two switches be independent.</para>
    /// </remarks>
    /// <param name="kind">Which panel.</param>
    /// <param name="resolution">The main panel's texture, square.</param>
    /// <param name="notificationResolution">The notification panel's. Smaller: a pop-up is three lines.</param>
    public static FallbackOverlayRuntime CreateFor(
        OverlayKind kind,
        int resolution = OverlayHost.DefaultResolution,
        int notificationResolution = OverlayHost.DefaultNotificationResolution)
    {
        var openXr = OpenXrOverlayRuntime.Shared(resolution, notificationResolution);

        return new FallbackOverlayRuntime(
            new OpenVrOverlayRuntime(kind),
            kind is OverlayKind.Notification ? openXr.NotificationPanel : openXr);
    }

    public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted, Detail: "No VR runtime has been looked for yet.");

    /// <summary>The runtime the overlay is showing through, or null while it is not showing.</summary>
    public IOverlayRuntime? Attached => _attached;

    public OverlayRuntimeStatus Start()
    {
        if (_attached is { Status.State: OverlayRuntimeState.Running })
            return Status = _attached.Status;

        _attached = null;

        var openVr = _openVr.Start();
        if (openVr.State is OverlayRuntimeState.Running)
        {
            _attached = _openVr;
            _attached.Place(_placement);
            return Status = openVr;
        }

        if (!OpenXrFollows(openVr))
            return Status = openVr;

        var openXr = _openXr.Start();
        if (openXr.State is OverlayRuntimeState.Running)
        {
            _attached = _openXr;
            _attached.Place(_placement);
            return Status = openXr;
        }

        return Status = MoreTelling(openVr, openXr);
    }

    /// <summary>Whether an OpenVR answer is one that OpenXR should be tried after.</summary>
    public static bool OpenXrFollows(OverlayRuntimeStatus openVr)
        => openVr.State is OverlayRuntimeState.NoRuntime
            || (openVr.State is OverlayRuntimeState.Refused && openVr.Error is VrInitError.Init_InvalidApplicationType);

    /// <summary>Of two failures, the one worth showing.</summary>
    public static OverlayRuntimeStatus MoreTelling(OverlayRuntimeStatus openVr, OverlayRuntimeStatus openXr)
    {
        var vr = Weight(openVr.State);
        var xr = Weight(openXr.State);

        if (xr > vr)
            return openXr;
        if (vr > xr)
            return openVr;

        // A tie. Both refused: OpenXR's, the runtime that could have worked here. Both absent:
        // OpenVR's, because "SteamVR is not installed" is the answer a moderator can act on.
        return openXr.State is OverlayRuntimeState.Refused ? openXr : openVr;
    }

    private static int Weight(OverlayRuntimeState state) => state switch
    {
        OverlayRuntimeState.Running => 4,
        OverlayRuntimeState.NotStarted => 3,
        OverlayRuntimeState.Refused => 2,
        _ => 1,
    };

    public void Poll()
    {
        if (_attached is null)
            return;

        _attached.Poll();

        if (_attached.Status.State is not OverlayRuntimeState.Running)
        {
            // The runtime closed and its own Poll said so; carry that word, and let the next
            // Start begin from the top again.
            Status = _attached.Status;
            _attached = null;
        }
    }

    public bool Submit(IOverlaySurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return _attached?.Submit(surface) ?? false;
    }

    public void Show() => _attached?.Show();

    public void Hide() => _attached?.Hide();

    public OverlayTracking ReadTracking() => _attached?.ReadTracking() ?? OverlayTracking.None;

    /// <summary>Kept here too, so whichever runtime attaches next is placed the same way.</summary>
    public void Place(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        _placement = placement;
        _attached?.Place(placement);
    }

    public void Dispose()
    {
        _attached = null;
        _openVr.Dispose();
        _openXr.Dispose();
        Status = new(OverlayRuntimeState.NotStarted, Detail: "The overlay has been let go.");
    }
}
