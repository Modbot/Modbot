using Modbot.Overlay.Rendering;

namespace Modbot.Overlay.OpenVr;

/// <summary>What SteamVR's own laser did on the dashboard tab.</summary>
public enum DashboardPointerKind
{
    Move,
    Down,
    Up,

    /// <summary>The tab went out of sight, so whatever was being held is let go.</summary>
    Gone,
}

/// <summary>
/// One thing SteamVR's laser did on the dashboard tab, in the tab's own pixels with the top left
/// at 0,0, the way the view is laid out.
/// </summary>
public readonly record struct DashboardPointer(DashboardPointerKind Kind, double X, double Y);

/// <summary>The dashboard tab's side of SteamVR, behind an interface so the rest can be tested.</summary>
public interface IDashboardRuntime : IDisposable
{
    OverlayRuntimeStatus Status { get; }

    /// <summary>
    /// Attaches to SteamVR if it is running and makes the tab. Never starts SteamVR. Safe to call
    /// again while the answer is <see cref="OverlayRuntimeState.NotStarted"/>.
    /// </summary>
    OverlayRuntimeStatus Start();

    /// <summary>
    /// Hears what SteamVR has said since the last call: the laser's moves and presses, which are
    /// kept for <see cref="TakePointer"/>, and SteamVR closing, which lets the tab go.
    /// </summary>
    void Poll();

    /// <summary>What the laser did since the last call, oldest first.</summary>
    IReadOnlyList<DashboardPointer> TakePointer();

    /// <summary>Hands SteamVR the tab's picture. Cheap, and only called on a change.</summary>
    bool Submit(IOverlaySurface surface);

    /// <summary>Hands SteamVR the small picture on the tab's button.</summary>
    bool SubmitThumbnail(IOverlaySurface surface);
}

/// <summary>
/// Modbot's tab in the SteamVR dashboard, the menu the system button opens, through OpenVR's
/// <c>CreateDashboardOverlay</c>.
/// </summary>
/// <remarks>
/// <para><strong>Two overlays, made together.</strong> SteamVR answers <c>CreateDashboardOverlay</c>
/// with a main overlay, the page shown while the tab is chosen, and a thumbnail overlay, the
/// picture on the tab's button. SteamVR places both, shows the page only while its tab is the one
/// chosen, and destroys the thumbnail with the page.</para>
/// <para><strong>SteamVR's own laser, not Modbot's.</strong> The floating panels draw their own
/// pointer because SteamVR draws a laser only for dashboard overlays. This is one, so it asks
/// for <c>VROverlayInputMethod_Mouse</c> and is sent mouse events: a move, a press and a release,
/// at a point on the page. The mouse scale is set to the texture's size, so a point arrives in the
/// texture's own pixels; SteamVR counts up from the bottom, and the view is laid out from the
/// top, so the height is turned the right way up here.</para>
/// <para><strong>The same attachment as the floating panels.</strong> It joins
/// <see cref="OpenVrSession"/> like they do and owns only its own handles, so any of them can come
/// and go without the others.</para>
/// <para><strong>SteamVR only.</strong> WiVRn and Monado have no dashboard to put a tab in, and
/// xrizer refuses an overlay application anyway, so on those this reports the refusal and does
/// nothing else.</para>
/// <para>Like the panels, it talks to SteamVR on this machine and to nothing else.</para>
/// </remarks>
public sealed class OpenVrDashboardRuntime : IDashboardRuntime
{
    /// <summary>
    /// Stable for the life of the product: SteamVR remembers the tab by this string, and two
    /// overlays may not share one, so it is not the floating panel's key.
    /// </summary>
    public const string DashboardKey = "moe.bin.modbot.dashboard";

    /// <summary>The tab's name under its button.</summary>
    public const string DashboardName = "Modbot";

    /// <summary>
    /// How wide the page is in the dashboard. SteamVR's own pages and OVR Advanced Settings sit
    /// about this wide.
    /// </summary>
    public const float WidthInMetres = 2.5f;

    /// <summary>The most events read in one poll, so a runtime that never stops answering cannot hold the UI thread.</summary>
    private const int MostEventsAPoll = 64;

    private readonly OpenVrSession _session;
    private readonly int _width;
    private readonly int _height;
    private readonly List<DashboardPointer> _pointer = [];

    private ulong _page;
    private ulong _thumbnail;
    private int _generation;
    private bool _holdsSession;
    private byte[]? _rgba;
    private byte[]? _thumbnailRgba;

    /// <param name="width">The page's texture, across, in pixels; the mouse is scaled to it.</param>
    /// <param name="height">And down.</param>
    /// <param name="session">The attachment to share. Null takes the client's one.</param>
    public OpenVrDashboardRuntime(int width, int height, OpenVrSession? session = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        _width = width;
        _height = height;
        _session = session ?? OpenVrSession.Shared;
    }

    public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted, Detail: "SteamVR has not been looked for yet.");

    public OverlayRuntimeStatus Start()
    {
        if (Status.State is OverlayRuntimeState.Running && _page != 0 && _generation == _session.Generation)
            return Status;

        var opened = _session.Open();
        if (opened.State is not OverlayRuntimeState.Running)
            return Status = opened;

        _holdsSession = true;
        _generation = _session.Generation;

        if (CreateTab() is { } failure)
        {
            _holdsSession = false;
            _session.Release();
            return Status = failure;
        }

        return Status = new(OverlayRuntimeState.Running, Detail: "In the SteamVR dashboard.");
    }

    public void Poll()
    {
        // A floating panel heard SteamVR close and let the whole attachment go. These handles
        // belong to an attachment that no longer exists, so they are dropped, never destroyed.
        if (_page != 0 && _generation != _session.Generation)
        {
            _page = 0;
            _thumbnail = 0;
            _holdsSession = false;
            _pointer.Add(new DashboardPointer(DashboardPointerKind.Gone, 0, 0));
            Status = new(OverlayRuntimeState.NotStarted, Detail: "SteamVR closed.");
            return;
        }

        if (_page == 0)
            return;

        unsafe
        {
            var poll = (delegate* unmanaged[Stdcall]<ulong, VrEvent*, uint, byte>)_session.Slot(OverlaySlot.PollNextOverlayEvent);
            VrEvent vrEvent;

            for (var i = 0; i < MostEventsAPoll && poll(_page, &vrEvent, VrEvent.PlatformSize) != 0; i++)
            {
                if (vrEvent.EventType is OpenVrInterop.EventQuit or OpenVrInterop.EventProcessQuit)
                {
                    // SteamVR is closing and expects every overlay to let go; the next Start()
                    // makes the tab again when it is back.
                    DestroyTab();
                    _holdsSession = false;
                    _session.Close();
                    _pointer.Add(new DashboardPointer(DashboardPointerKind.Gone, 0, 0));
                    Status = new(OverlayRuntimeState.NotStarted, Detail: "SteamVR closed.");
                    return;
                }

                if (Read(vrEvent, _height) is { } pointer)
                    _pointer.Add(pointer);
            }

            // Nothing on the button is listened to, but its queue is emptied all the same so
            // SteamVR is not left holding events nobody will read.
            for (var i = 0; _thumbnail != 0 && i < MostEventsAPoll && poll(_thumbnail, &vrEvent, VrEvent.PlatformSize) != 0; i++)
            {
            }
        }
    }

    /// <summary>
    /// One SteamVR event as the laser's doing on the page, or null for an event that is not one.
    /// The mouse is scaled to the texture, bottom left at 0,0; the page is laid out from the top.
    /// </summary>
    public static DashboardPointer? Read(VrEvent vrEvent, int height)
    {
        var kind = vrEvent.EventType switch
        {
            OpenVrInterop.EventMouseMove => DashboardPointerKind.Move,
            OpenVrInterop.EventMouseButtonDown => DashboardPointerKind.Down,
            OpenVrInterop.EventMouseButtonUp => DashboardPointerKind.Up,
            OpenVrInterop.EventOverlayHidden => DashboardPointerKind.Gone,
            _ => (DashboardPointerKind?)null,
        };

        if (kind is not { } known)
            return null;

        if (known is DashboardPointerKind.Gone)
            return new DashboardPointer(known, 0, 0);

        var mouse = vrEvent.Mouse;

        // Only the trigger presses and lets go. Another button SteamVR maps to the mouse is not a
        // press on anything here.
        if ((known is DashboardPointerKind.Down or DashboardPointerKind.Up) && mouse.Button != OpenVrInterop.MouseButtonLeft)
            return null;

        return new DashboardPointer(known, mouse.X, height - mouse.Y);
    }

    public IReadOnlyList<DashboardPointer> TakePointer()
    {
        if (_pointer.Count == 0)
            return [];

        var taken = _pointer.ToArray();
        _pointer.Clear();
        return taken;
    }

    public bool Submit(IOverlaySurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return _page != 0 && OpenVrPicture.Hand(_session, _page, surface, ref _rgba);
    }

    public bool SubmitThumbnail(IOverlaySurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return _thumbnail != 0 && OpenVrPicture.Hand(_session, _thumbnail, surface, ref _thumbnailRgba);
    }

    public void Dispose()
    {
        if (_generation == _session.Generation)
        {
            DestroyTab();
        }
        else
        {
            _page = 0;
            _thumbnail = 0;
        }

        if (_holdsSession)
        {
            _holdsSession = false;
            _session.Release();
        }

        Status = new(OverlayRuntimeState.NotStarted, Detail: "The dashboard tab has been let go.");
    }

    private void DestroyTab()
    {
        if (_page == 0)
            return;

        // The thumbnail goes with the page; SteamVR refuses to destroy it on its own.
        unsafe
        {
            ((delegate* unmanaged[Stdcall]<ulong, int>)_session.Slot(OverlaySlot.DestroyOverlay))(_page);
        }

        _page = 0;
        _thumbnail = 0;
    }

    private unsafe OverlayRuntimeStatus? CreateTab()
    {
        ulong page = 0;
        ulong thumbnail = 0;
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(DashboardKey + "\0");
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(DashboardName + "\0");

        int created;
        fixed (byte* key = keyBytes)
        fixed (byte* name = nameBytes)
        {
            var create = (delegate* unmanaged[Stdcall]<byte*, byte*, ulong*, ulong*, int>)_session.Slot(OverlaySlot.CreateDashboardOverlay);
            created = create(key, name, &page, &thumbnail);
        }

        if (created != 0 || page == 0)
            return new(OverlayRuntimeState.Refused, Detail: $"SteamVR refused to make the dashboard tab (error {created}).");

        _page = page;
        _thumbnail = thumbnail;

        ((delegate* unmanaged[Stdcall]<ulong, float, int>)_session.Slot(OverlaySlot.SetOverlayWidthInMeters))(page, WidthInMetres);
        ((delegate* unmanaged[Stdcall]<ulong, int, int>)_session.Slot(OverlaySlot.SetOverlayInputMethod))(page, OpenVrInterop.InputMethodMouse);

        var scale = new HmdVector2 { X = _width, Y = _height };
        ((delegate* unmanaged[Stdcall]<ulong, HmdVector2*, int>)_session.Slot(OverlaySlot.SetOverlayMouseScale))(page, &scale);

        return null;
    }
}
