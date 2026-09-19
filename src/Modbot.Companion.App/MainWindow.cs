using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Clips;
using Modbot.Companion.Ingest;
using Modbot.Companion.Journal;
using Modbot.Companion.Overlay;
using Modbot.Companion.Pipeline;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;
using Modbot.Companion.Voice;
using Modbot.Overlay;
using Modbot.Overlay.OpenVr;

namespace Modbot.Companion.App;

/// <summary>Which page the window is showing.</summary>
internal enum Page
{
    Servers,

    /// <summary>The pairing instructions. Reached from Servers, not from the sidebar.</summary>
    AddServer,
    Events,
    SteamVr,
    Log,
    Settings,
    Credits,

    /// <summary>Only with <c>MODBOT_DEBUG_MODE=1</c>.</summary>
    Debug,
}

/// <summary>
/// The client window: what Modbot is reading, where it reports, and every event it has handled.
/// </summary>
/// <remarks>
/// <para><strong>This window is the trust argument made visible.</strong> A volunteer moderator is
/// being asked to run background software on a personal machine that watches what they do in
/// VRChat. Suspicion is the correct response. "Read the source" answers it for people who read C#
/// and for nobody else, so the answer for everybody else is a screen: exactly what left the
/// machine, in plain English, on demand; a pause that stops transmission immediately and shows it;
/// and a program that is never invisible while it runs.</para>
/// <para><strong>What this window can record, and what it cannot.</strong> The Settings page has a
/// Clips card. Switched on — and it is off until somebody switches it on — the client keeps the last
/// two to five minutes of <strong>VRChat's window</strong>, in two files of its own, while VRChat is
/// running, and writes them out when <strong>Save a clip</strong> is pressed, here or on the overlay
/// panel. Whatever is drawn over VRChat while somebody is in it is inside that window and is in the
/// clip; the rest of their screen never is. That is the whole of it: no sound, no keyboard, no
/// clipboard, no list of other programs or of their windows, and nothing read
/// from VRChat's screenshot folder or any other folder on the machine. No clip is ever uploaded —
/// this program has no path to a server that could take one, and did not gain one. Attaching a clip
/// to a case is still what it always was: a moderator, in the web UI, in a browser, choosing a file.
/// <c>CompanionSourceGuardTests</c> holds every part of that, and fails the build if a second file
/// learns to capture anything.</para>
/// <para>Built in code rather than markup because a reader auditing this program should be able to
/// see what it displays without also learning a XAML dialect.</para>
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly StackPanel _nav = new() { Spacing = 2 };

    // The warnings sit above the page and change on their own, so they have their own panel: the
    // page below them can then be left alone on a tick that changed nothing.
    private readonly StackPanel _warnings = new() { Spacing = 14, IsVisible = false };

    // What the page was last built from. The window is drawn once a second, and everything built
    // again is a new control: a button under the pointer starts its hover from nothing and a list
    // that was open closes, because the control it belonged to no longer exists. So the page is
    // built again only when it would come out different, and refreshed where it stands otherwise.
    private CompanionAppSnapshot? _drawnFrom;
    private Page? _drawnPage;
    private int _drawnPictures;

    /// <summary>One sidebar row, kept so the sidebar is refreshed rather than built again.</summary>
    private readonly Dictionary<Page, NavRow> _navRows = [];

    // The top of the sidebar is the group this companion reports to, like the web app's; Modbot's
    // own mark moves to the foot. Both are rebuilt from the snapshot, because the group's name and
    // picture arrive with pairing and the picture arrives a moment after that.
    private readonly StackPanel _identity = new() { Spacing = 8, Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
    private readonly StackPanel _brandFoot = new() { Spacing = 8, Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _healthLine;
    private readonly Ellipse _healthDot;

    // Built once and re-attached on every render rather than rebuilt, because the window redraws
    // on a timer and a paste box that was recreated every second would lose whatever was pasted
    // into it before the button could be pressed.
    private readonly Border _pairingCard;
    private readonly TextBox _pasteBox;
    private readonly TextBlock _pairingWhere;
    private readonly TextBlock _pairingMessage;

    // Built once for the same reason: a switch rebuilt every second can lose the click on it.
    private readonly CheckBox _startupBox;
    private readonly CheckBox _overlayOnBox;
    private readonly TextBox _logFolderBox;
    private readonly TextBlock _logFolderWatching = Ui.Faint("");
    private bool _renderingSwitches;

    // The panel's size, opacity and curve: sliders, built once so a drag is not cut short by the
    // timer, and only refilled while nobody is on them.
    private readonly Slider _widthSlider;
    private readonly Slider _opacitySlider;
    private readonly Slider _curveSlider;
    // The Voice card, built once too: a slider being dragged and a list being opened both die
    // under a rebuild.
    private readonly CheckBox _voiceOn;
    private readonly Slider _voiceVolume;
    private readonly TextBlock _voiceVolumeValue;
    private readonly ComboBox _voiceDevice;
    private readonly ComboBox _voiceName;
    private readonly TextBlock _voiceLine;
    private readonly Button _voiceTest = Ui.Button("Test");
    private readonly List<string?> _voiceDeviceIds = [];
    private readonly List<string> _voiceNames = [];

    /// <summary>Set by the host once it has an HTTP client; null until then and pictures simply wait.</summary>
    internal GroupPictures? Pictures { get; set; }

    private Page _page = Page.Servers;
    private CompanionAppSnapshot _snapshot = CompanionAppSnapshot.Empty;
    private MainWindowActions _actions = MainWindowActions.None;

    public MainWindow()
    {
        Title = "Modbot";
        Icon = Brand.Icon();
        Width = 900;
        Height = 660;
        MinWidth = 720;
        MinHeight = 480;
        Background = Ui.T.BackgroundBrush;

        _healthDot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        _healthLine = Ui.Faint("");

        _pasteBox = Ui.Input("Paste the pairing token here");
        _pasteBox.FontFamily = Ui.Mono;
        _pairingWhere = Ui.Faint("");
        _pairingMessage = Ui.Dim("");
        _pairingMessage.IsVisible = false;
        _pairingCard = PairingCard();

        _startupBox = new CheckBox { Content = Ui.Text("Start Modbot Companion when my computer starts", Ui.T.Density.TextSmall, Ui.T.TextBrush) };
        _startupBox.IsCheckedChanged += (_, _) =>
        {
            if (!_renderingSwitches)
                _actions.SetStartWithWindows(_startupBox.IsChecked == true);
        };

        _overlayOnBox = new CheckBox { Content = Ui.Text("Overlay on", Ui.T.Density.TextSmall, Ui.T.TextBrush) };
        _overlayOnBox.VerticalAlignment = VerticalAlignment.Center;
        _overlayOnBox.IsCheckedChanged += (_, _) =>
        {
            if (!_renderingSwitches)
                _actions.SetOverlayOn(_overlayOnBox.IsChecked == true);
        };

        _logFolderBox = Ui.Input();
        _logFolderBox.FontFamily = Ui.Mono;

        _widthSlider = PlacementSlider(OverlayPlacement.MinWidth, OverlayPlacement.MaxWidth, 0.05, (p, v) => p with { Width = (float)v });
        _opacitySlider = PlacementSlider(OverlayPlacement.MinOpacity, 1, 0.05, (p, v) => p with { Opacity = (float)v });
        _curveSlider = PlacementSlider(0, 1, 0.05, (p, v) => p with { Curve = (float)v });
        _voiceOn = Switch("Voice on");
        _voiceVolumeValue = Ui.Text("", Ui.T.Density.TextSmall, Ui.T.TextDimBrush, wrap: false, mono: true);
        _voiceVolumeValue.VerticalAlignment = VerticalAlignment.Center;
        _voiceVolumeValue.Width = 32;
        _voiceVolume = new Slider
        {
            Minimum = 0,
            Maximum = VoiceSettings.MaxVolume,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Width = 220,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _voiceVolume.ValueChanged += (_, _) =>
        {
            _voiceVolumeValue.Text = $"{(int)_voiceVolume.Value}";
            VoiceChanged();
        };
        _voiceDevice = VoiceDropDown();
        _voiceName = VoiceDropDown();
        _voiceLine = Ui.Faint("");
        _voiceTest.Click += (_, _) => _actions.TestVoice();

        // The warnings and the page are two panels rather than one list, so a warning appearing or
        // going does not disturb the page under it.
        // The spacing is on the content, not on the ScrollViewer. A ScrollViewer's padding is
        // outside the area it scrolls, so the bottom of a long page could not be reached.
        var main = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Thickness(20),
                Children = { _warnings, _body },
            },
        };
        Grid.SetColumn(main, 1);

        SetUpKeyboard();
        SetUpEvents();
        SetUpNotifications();
        SetUpNotificationFilters();
        SetUpClips();

        // The palette and the shortcut sheet open over the page, inside this window, so the
        // window's own keys still reach them and nothing else appears in the taskbar.
        Content = new Panel
        {
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("216,*"),
                    Children = { Sidebar(), main },
                },
                _panelLayer,
            },
        };
    }

    /// <summary>A list on the Voice card. Every change goes through <see cref="VoiceChanged"/>.</summary>
    private ComboBox VoiceDropDown()
    {
        var list = new ComboBox
        {
            Height = Ui.T.Density.ControlHeight,
            MinWidth = 260,
            FontSize = Ui.T.Density.TextSmall,
            FontFamily = Ui.Sans,
            Background = Ui.T.BackgroundBrush,
            Foreground = Ui.T.TextBrush,
            BorderBrush = Ui.T.Border2Brush,
            BorderThickness = new Thickness(Ui.T.Density.Hairline),
            CornerRadius = new CornerRadius(Ui.T.Density.Radius),
        };
        list.SelectionChanged += (_, _) => VoiceChanged();
        return list;
    }

    /// <summary>A check box on the Voice card. Every change goes through <see cref="VoiceChanged"/>.</summary>
    private CheckBox Switch(string caption)
    {
        var box = new CheckBox { Content = Ui.Text(caption, Ui.T.Density.TextSmall, Ui.T.TextBrush) };
        box.IsCheckedChanged += (_, _) => VoiceChanged();
        return box;
    }

    /// <summary>What the Voice card's controls say right now, handed to the application as one settings record.</summary>
    private void VoiceChanged()
    {
        if (_renderingSwitches)
            return;

        var index = _voiceDevice.SelectedIndex;
        var device = index >= 0 && index < _voiceDeviceIds.Count ? _voiceDeviceIds[index] : null;

        var chosen = _voiceName.SelectedIndex;
        var name = chosen >= 0 && chosen < _voiceNames.Count ? _voiceNames[chosen] : VoiceModel.DefaultName;

        // Which kinds the voice says is the Notifications card's Voice column now; the three
        // fields it still keeps are carried through untouched so this card cannot undo them.
        _actions.SetVoice(_snapshot.VoiceOrNone.Settings with
        {
            On = _voiceOn.IsChecked == true,
            Volume = (int)_voiceVolume.Value,
            OutputDeviceId = device,
            VoiceName = name,
        });
    }

    /// <summary>
    /// Closing the window hides it. Avalonia cannot show a window again once it has really
    /// closed, and the tray's "Open Modbot" must bring this one back, so the close button hides
    /// it and only the application shutting down closes it for good.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            return;

        e.Cancel = true;
        Hide();

        // The client is still running and still reporting, which is the point of the tray icon and
        // is not obvious from a window that has just vanished. Said the first few times only.
        _actions.ClosedToTray();
    }

    /// <summary>
    /// Draws the window from a snapshot. Called on a timer, once a second.
    /// </summary>
    /// <remarks>
    /// A snapshot saying exactly what the last one said is drawn by doing nothing at all, because
    /// building the page again would only take the hover out from under the pointer and close
    /// whatever was open. The counts, the states and the log line all reach the screen through the
    /// snapshot, so a tick that changes any of them is not one of these.
    /// </remarks>
    public void Render(CompanionAppSnapshot snapshot, MainWindowActions actions)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(actions);

        _snapshot = snapshot;
        _actions = actions;

        if (_drawnPage == _page
            && _drawnPictures == PicturesArrived
            && _drawnFrom is { } drawn
            && drawn.LooksTheSameAs(snapshot))
        {
            return;
        }

        RenderIdentity();
        RenderHealth();
        RenderNav();
        DrawPage();
    }

    /// <summary>How many group pictures have landed, so a page drawn before one arrived is drawn again.</summary>
    private int PicturesArrived => Pictures?.Arrived ?? 0;

    private Control Sidebar()
    {
        var health = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { _healthDot, _healthLine },
        };

        var panel = new DockPanel();
        panel.Children.Add(Dock(_identity, Avalonia.Controls.Dock.Top));
        panel.Children.Add(Dock(SearchButton(), Avalonia.Controls.Dock.Top));
        panel.Children.Add(Dock(_nav, Avalonia.Controls.Dock.Top));
        panel.Children.Add(Dock(health, Avalonia.Controls.Dock.Bottom));
        panel.Children.Add(Dock(_brandFoot, Avalonia.Controls.Dock.Bottom));
        panel.Children.Add(new Panel());

        return new Border
        {
            Background = Ui.T.SurfaceBrush,
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(0, 0, Ui.T.Density.Hairline, 0),
            Padding = new Thickness(12, 16),
            Child = panel,
        };
    }

    /// <summary>The way into the command palette for a hand on the mouse, with its key beside it.</summary>
    private Control SearchButton()
    {
        var caption = Ui.Dim("Search");
        caption.VerticalAlignment = VerticalAlignment.Center;

        var row = new DockPanel { LastChildFill = false };
        row.Children.Add(Dock(caption, Avalonia.Controls.Dock.Left));
        row.Children.Add(Dock(Ui.Kbd("mod+k"), Avalonia.Controls.Dock.Right));

        var button = new Button
        {
            Content = row,
            Height = Ui.T.Density.ControlHeight,
            Padding = new Thickness(8, 0),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Ui.T.BackgroundBrush,
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(Ui.T.Density.Hairline),
            CornerRadius = new CornerRadius(Ui.T.Density.Radius),
        };

        button.Click += (_, _) => OpenPalette();
        return button;
    }

    /// <summary>
    /// The group at the top and Modbot at the foot, the way the web app's sidebar is laid out.
    /// With nothing paired there is no group to name, so Modbot takes the top and the foot is empty.
    /// </summary>
    private void RenderIdentity()
    {
        _identity.Children.Clear();
        _brandFoot.Children.Clear();

        var server = _snapshot.Servers.FirstOrDefault();
        if (server is null)
        {
            _identity.Children.Add(Brand.Mark(22));
            _identity.Children.Add(Names(Brand.Wordmark(Ui.Text("Modbot", Ui.T.Density.TextBase + 1, Ui.T.TextBrush, FontWeight.Normal, wrap: false)), "companion"));
            return;
        }

        var picture = Pictures?.For(server.GroupIconUrl);
        _identity.Children.Add(picture is null ? Brand.Mark(22) : Ui.Picture(picture, 28));
        _identity.Children.Add(Names(
            Ui.Text(server.GroupName, Ui.T.Density.TextBase + 1, Ui.T.TextBrush, FontWeight.Medium, wrap: false),
            _snapshot.Servers.Count > 1 ? $"and {_snapshot.Servers.Count - 1} more" : "companion"));

        _brandFoot.Children.Add(Brand.Mark(16));
        _brandFoot.Children.Add(Brand.Wordmark(Ui.Text("Modbot", Ui.T.Density.TextSmall, Ui.T.TextDimBrush, FontWeight.Normal, wrap: false)));
    }

    private static Control Names(TextBlock name, string under)
    {
        name.MaxWidth = 150;
        return new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, Ui.Faint(under) },
        };
    }

    private void RenderHealth()
    {
        var (colour, line) = _snapshot.LogStatus switch
        {
            LogHealthStatus.Healthy => (Ui.T.Palette.Ok, "reading VRChat's log"),
            LogHealthStatus.NotUnderstood => (Ui.T.Palette.Danger, "log not recognised"),
            _ => (Ui.T.Palette.TextFaint, "VRChat not running"),
        };

        _healthDot.Fill = DesignTokens.Brush(colour);
        _healthLine.Text = line;
    }

    private void RenderNav()
    {
        NavItem(Page.Servers, "Servers", _snapshot.Servers.Count == 0 ? null : $"{_snapshot.Servers.Count}");
        NavItem(Page.Events, "Events", _snapshot.Events.Count == 0 ? null : $"{_snapshot.Events.Count}");

        // "on" is the panel actually up in a headset; "off" is the switch. Between them sits the
        // ordinary case -- switched on, SteamVR not running -- which says nothing, because it is
        // what most of the day looks like and a badge for it would mean nothing.
        var overlay = _snapshot.OverlayOrNone;
        NavItem(Page.SteamVr, "SteamVR", !overlay.On ? "off" : overlay.Attached ? "on" : null);
        NavItem(Page.Log, "Log", null);
        NavItem(Page.Settings, "Settings", null);
        NavItem(Page.Credits, "Credits", null);

        if (_snapshot.DebugMode)
            NavItem(Page.Debug, "Debug", null);

        RegisterWindowKeys();
    }

    /// <summary>
    /// One sidebar row, drawn the way the web app's are after its Linear-style pass: labels dimmed
    /// so the page takes precedence, and the open page lit in the accent's tint rather than
    /// a heavier surface.
    /// </summary>
    /// <remarks>
    /// Built the first time its page is named and only refreshed after that. The sidebar is drawn
    /// on every tick, and a row built again is a new button: the one under the pointer would start
    /// its hover from nothing every second.
    /// </remarks>
    private void NavItem(Page page, string caption, string? badge)
    {
        if (!_navRows.TryGetValue(page, out var row))
        {
            row = BuildNavItem(page, caption);
            _navRows[page] = row;
            _nav.Children.Add(row.Button);
        }

        var selected = _page == page;
        row.Label.Foreground = selected ? Ui.T.TextBrush : Ui.T.TextDimBrush;
        row.Button.Background = selected ? Ui.T.AccentDimBrush : Brushes.Transparent;
        row.Badge.Text = badge ?? "";
        row.Badge.IsVisible = badge is not null;
    }

    private NavRow BuildNavItem(Page page, string caption)
    {
        var label = Ui.Text(caption, Ui.T.Density.TextSmall, Ui.T.TextDimBrush, FontWeight.Medium, wrap: false);
        label.VerticalAlignment = VerticalAlignment.Center;

        var badge = Ui.Faint("");
        badge.VerticalAlignment = VerticalAlignment.Center;
        badge.IsVisible = false;

        var row = new DockPanel { LastChildFill = false };
        row.Children.Add(Dock(label, Avalonia.Controls.Dock.Left));
        row.Children.Add(Dock(badge, Avalonia.Controls.Dock.Right));

        var button = new Button
        {
            Content = row,
            Height = Ui.T.Density.ControlHeight,
            Padding = new Thickness(8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Ui.T.Density.Radius),
        };

        button.Click += (_, _) => GoTo(page);

        return new NavRow(button, label, badge);
    }

    /// <summary>The three parts of a sidebar row that a refresh touches.</summary>
    private sealed record NavRow(Button Button, TextBlock Label, TextBlock Badge);

    /// <summary>Shows a page: from the sidebar, the palette, or <c>g</c> then its letter.</summary>
    private void GoTo(Page page)
    {
        if (page is Page.Debug && !_snapshot.DebugMode)
            return;

        _page = page;
        RenderNav();
        RenderPage();
    }

    /// <summary>
    /// Draws the page for a new snapshot: built again when it would come out different, and
    /// refreshed where it stands when it would not.
    /// </summary>
    private void DrawPage()
    {
        if (!PageAlreadyDrawn())
        {
            RenderPage();
            return;
        }

        RefreshPage();
        _drawnFrom = _snapshot;
    }

    /// <summary>
    /// Whether the open page is already showing exactly what this snapshot would draw.
    /// </summary>
    /// <remarks>
    /// <para>Each page names the parts of the snapshot it can ignore: the ones it never shows, and
    /// the ones it puts into controls it keeps rather than building again. Those are taken from
    /// the snapshot the page was drawn from so they cannot ask for a page nobody would see a
    /// difference in. Everything else is compared.</para>
    /// <para>The naming runs that way round on purpose. A part added to the snapshot later is
    /// named by nobody, so it is compared, so the page is built again — which is what every page
    /// did before this existed. Getting it wrong costs a rebuild; it cannot leave a page saying
    /// something that is no longer true.</para>
    /// </remarks>
    private bool PageAlreadyDrawn()
    {
        if (_drawnPage != _page || _drawnPictures != PicturesArrived || _drawnFrom is not { } drawn)
            return false;

        var ignored = _page switch
        {
            Page.Events => Parts.Log | Parts.Overlays | Parts.Settings,
            Page.SteamVr => Parts.Log | Parts.Events | Parts.Settings | Parts.Servers,
            Page.Log => Parts.Events | Parts.Overlays | Parts.Servers,
            Page.Settings => Parts.Log | Parts.Events | Parts.Overlays | Parts.Settings | Parts.Servers,
            Page.Credits => Parts.Log | Parts.Events | Parts.Overlays | Parts.Settings | Parts.Servers,
            Page.Debug => Parts.Log | Parts.Events | Parts.Settings | Parts.Servers,
            _ => Parts.Log | Parts.Events | Parts.Overlays | Parts.Settings,
        };

        return drawn.LooksTheSameAs(Ignoring(_snapshot, drawn, ignored));
    }

    /// <summary>The parts of a snapshot a page can ignore.</summary>
    [Flags]
    private enum Parts
    {
        None = 0,

        /// <summary>The log counters and the sentence made out of them. The Log page shows them.</summary>
        Log = 1,

        /// <summary>The events. The Events page shows them; every other page shows only how many, in the sidebar.</summary>
        Events = 2,

        /// <summary>The three panels. The SteamVR page shows two and the Settings page refreshes the third.</summary>
        Overlays = 4,

        /// <summary>Everything the Settings page keeps controls for, and puts into them on every tick.</summary>
        Settings = 8,

        /// <summary>The paired servers and the last pairing attempt. The Servers page shows them.</summary>
        Servers = 16,
    }

    /// <summary>
    /// <paramref name="next"/> with the named parts taken from the snapshot the page was drawn
    /// from, so a page that does not show them is not built again on their account.
    /// </summary>
    private static CompanionAppSnapshot Ignoring(CompanionAppSnapshot next, CompanionAppSnapshot drawn, Parts parts)
    {
        if (parts.HasFlag(Parts.Log))
        {
            next = next with
            {
                LogStatus = drawn.LogStatus,
                LogDetail = drawn.LogDetail,
                LinesRead = drawn.LinesRead,
                BehaviourLines = drawn.BehaviourLines,
                RecognisedEvents = drawn.RecognisedEvents,
            };
        }

        if (parts.HasFlag(Parts.Events))
            next = next with { Events = drawn.Events };

        if (parts.HasFlag(Parts.Overlays))
        {
            next = next with
            {
                Overlay = drawn.Overlay,
                NotifyOverlay = drawn.NotifyOverlay,
                DesktopOverlay = drawn.DesktopOverlay,
            };
        }

        if (parts.HasFlag(Parts.Settings))
        {
            next = next with
            {
                Startup = drawn.Startup,
                Voice = drawn.Voice,
                Notifications = drawn.Notifications,
                NotificationFilters = drawn.NotificationFilters,
                LogFolder = drawn.LogFolder,
                LogFolderConfigured = drawn.LogFolderConfigured,
            };
        }

        if (parts.HasFlag(Parts.Servers))
        {
            next = next with
            {
                Servers = drawn.Servers,
                LastPairing = drawn.LastPairing,
                PairingPage = drawn.PairingPage,
            };
        }

        return next;
    }

    /// <summary>
    /// The parts of the open page that are put into controls it keeps rather than built again.
    /// Run after a rebuild as well, so a card is filled in one way however it came to be there.
    /// </summary>
    private void RefreshPage()
    {
        switch (_page)
        {
            case Page.Settings:
                RefreshSettings();
                break;
            case Page.Events or Page.SteamVr or Page.Log or Page.Credits or Page.Debug:
                break;
            default:
                RenderPairingNotice();
                break;
        }
    }

    private void RenderWarnings()
    {
        _warnings.Children.Clear();

        foreach (var warning in _snapshot.Warnings)
            _warnings.Children.Add(Ui.Note(warning.Message, Severity(warning.Severity)));

        _warnings.IsVisible = _warnings.Children.Count > 0;
    }

    /// <summary>Builds the page again from the ground up. Every way into the page but the timer's.</summary>
    private void RenderPage()
    {
        RenderWarnings();
        _body.Children.Clear();

        switch (_page)
        {
            case Page.Events:
                RenderEvents();
                break;
            case Page.SteamVr:
                RenderSteamVr();
                break;
            case Page.Log:
                RenderLog();
                break;
            case Page.Settings:
                RenderSettings();
                break;
            case Page.AddServer:
                RenderAddServer();
                break;
            case Page.Credits:
                RenderCredits();
                break;
            case Page.Debug when _snapshot.DebugMode:
                RenderDebug();
                break;
            default:
                RenderServers();
                break;
        }

        _drawnPage = _page;
        _drawnFrom = _snapshot;
        _drawnPictures = PicturesArrived;
        RefreshPage();
    }

    private static Color Severity(WarningSeverity severity) => severity switch
    {
        WarningSeverity.Critical => Ui.T.Palette.Danger,
        WarningSeverity.Warning => Ui.T.Palette.Warn,
        _ => Ui.T.Palette.Info,
    };

/// <summary>
    /// One small card per paired server, and the way to add another.
    /// </summary>
    /// <remarks>
    /// The card is the group: its icon, its name, and whether reporting is working. The counts
    /// that used to sit here -- recorded, already known, queued -- answered a question nobody was
    /// asking on this page; what a moderator opens it for is whether their groups are covered, and
    /// four numbers per card buried that. The Events page still has every one of them.
    /// </remarks>
    private void RenderServers()
    {
        var add = Ui.Button("Add a server", primary: true);
        add.Click += (_, _) => GoTo(Page.AddServer);

        _body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4),
            Children = { add },
        });

        if (_snapshot.Servers.Count == 0)
        {
            _body.Children.Add(Ui.Card(
                Ui.Dim("No servers paired."),
                "Not reporting anywhere"));
            return;
        }

        var cards = new WrapPanel { ItemSpacing = 12, LineSpacing = 12 };
        foreach (var server in _snapshot.Servers)
            cards.Children.Add(ServerCard(server));

        _body.Children.Add(cards);
    }

    /// <summary>The pairing instructions, on a screen of their own, with the way back.</summary>
    private void RenderAddServer()
    {
        var back = Ui.Button("Back to servers");
        back.Click += (_, _) => GoTo(Page.Servers);

        _body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4),
            Children = { back },
        });

        _body.Children.Add(_pairingCard);
    }

    /// <summary>
    /// The pairing card's changing parts: where the button goes, and what the last attempt said.
    /// </summary>
    /// <remarks>
    /// Read from the snapshot rather than kept in the window, so a link that Windows handed to
    /// the running client while this window was closed still has its answer waiting when the
    /// window is opened.
    /// </remarks>
    private void RenderPairingNotice()
    {
        _pairingWhere.Text = $"Opens {_snapshot.PairingPage} in your browser.";

        if (_snapshot.LastPairing is not { } notice)
        {
            _pairingMessage.IsVisible = false;
            return;
        }

        _pairingMessage.IsVisible = true;
        _pairingMessage.Text = notice.Message;
        _pairingMessage.Foreground = notice.Kind switch
        {
            PairingNoticeKind.Succeeded => Ui.T.OkBrush,
            PairingNoticeKind.Failed => Ui.T.DangerBrush,
            _ => Ui.T.TextDimBrush,
        };
    }

/// <summary>
    /// One group, small enough that several sit side by side: the icon, the name, whether
    /// reporting is working, and the two things a moderator does to it.
    /// </summary>
    private Control ServerCard(ServerRow server)
    {
        var pause = Ui.Button(server.IsPaused ? "Resume reporting" : "Pause reporting");
        pause.Click += (_, _) => _actions.TogglePause(server.ServerId);

        var unpair = Ui.Button("Unpair", danger: true);
        unpair.Click += (_, _) => _actions.Unpair(server.ServerId);

        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Text(server.Detail, Ui.T.Density.TextSmall, DetailBrush(server.State)),
                Ui.Faint($"{server.Address}  ·  {server.ManagedGroupId}"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { pause, unpair },
                },
            },
        };

        var picture = Pictures?.For(server.GroupIconUrl);
        var card = Ui.Card(body, server.GroupName, StatePill(server), picture is null ? null : Ui.Picture(picture, 28));
        card.Width = ServerCardWidth;
        return card;
    }

    /// <summary>
    /// Wide enough for a long group name and the address under it, narrow enough that two sit
    /// side by side in the window at its smallest.
    /// </summary>
    private const double ServerCardWidth = 320;

    /// <summary>
    /// The one distinction that must be legible at a glance.
    /// </summary>
    /// <remarks>
    /// "Retrying, will send later" and "stopped, will never send" feel identical to somebody
    /// looking at a screen that is not moving, and they mean opposite things: one needs nothing
    /// doing, and the other means this moderator's coverage has silently ended.
    /// </remarks>
    private static Control StatePill(ServerRow server) => server.State switch
    {
        ConnectionState.Paused => Ui.Pill("Paused", Ui.T.Palette.Warn, Ui.T.Palette.WarnDim),
        ConnectionState.Stopped => Ui.Pill("Stopped", Ui.T.Palette.Danger, Ui.T.Palette.DangerDim),
        ConnectionState.NeedsRenegotiation =>
            Ui.Pill("Needs update", Ui.T.Palette.Danger, Ui.T.Palette.DangerDim),
        ConnectionState.Waiting => Ui.Pill("Retrying", Ui.T.Palette.Info, Ui.T.Palette.InfoDim),
        _ => Ui.Pill("Reporting", Ui.T.Palette.Ok, Ui.T.Palette.OkDim),
    };

    private static IBrush DetailBrush(ConnectionState state) => state switch
    {
        ConnectionState.Stopped or ConnectionState.NeedsRenegotiation => Ui.T.DangerBrush,
        ConnectionState.Paused => Ui.T.WarnBrush,
        _ => Ui.T.TextDimBrush,
    };

    /// <summary>
    /// Pairing: one button that opens the browser, and a paste box for when the browser could not
    /// hand the link back.
    /// </summary>
    /// <remarks>
    /// There is no server address to type, no code to type and no device name to invent. The
    /// pairing page in the browser knows the address and mints the code; the client's job is to
    /// receive them. The paste box takes the same token, for a browser that would not open the
    /// link -- pasting is the moderator's action in their own window, and nothing here reads what
    /// they have copied until they press the button.
    /// </remarks>
    private Border PairingCard()
    {
        var open = Ui.Button("Pair with a server", primary: true);
        open.Click += async (_, _) =>
        {
            open.IsEnabled = false;
            try
            {
                await CrashGuard.RunAsync("opening the pairing page", _actions.OpenPairingPageAsync);
            }
            finally
            {
                open.IsEnabled = true;
            }
        };

        var use = Ui.Button("Use pairing token");
        use.Click += async (_, _) =>
        {
            use.IsEnabled = false;
            try
            {
                await CrashGuard.RunAsync("pairing with a token", async () =>
                {
                    var result = await _actions.PairAsync(_pasteBox.Text ?? "");
                    if (result.Succeeded)
                        _pasteBox.Text = "";
                });
            }
            finally
            {
                use.IsEnabled = true;
            }
        };

        var body = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Ui.Dim(
                    "Pairing happens in your browser. Sign in to your group's Modbot, press "
                    + "\"Open in Modbot\", and this window will show the server. Nothing has to "
                    + "be typed, and the link only works once, for a few minutes."),
                new StackPanel
                {
                    Spacing = 4,
                    Children = { open, _pairingWhere },
                },
                Ui.Dim(
                    "If the browser button did nothing, copy the pairing token from that page and "
                    + "paste it here instead. It is the same thing."),
                Ui.Field("Pairing token", _pasteBox),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { use },
                },
                _pairingMessage,
            },
        };

        return Ui.Card(body, "Pair with a server");
    }

    /// <summary>
    /// When, what, and how it went: the time in its own column, the sentence, and one pill, named
    /// by the group the server manages. An event no paired server was given has the time and the
    /// sentence and nothing else, which is the truth of it.
    /// </summary>
    private static Border EventRow(JournalRow row, bool first, IReadOnlyDictionary<string, string> groups)
    {
        var sent = row.State is JournalEntryKind.Sent;

        var when = Ui.Text(row.At.ToLocalTime().ToString("HH:mm:ss"), Ui.T.Density.TextSmall, Ui.T.TextFaintBrush, wrap: false, mono: true);
        when.VerticalAlignment = VerticalAlignment.Center;

        var line = Ui.Text(
            row.Summary,
            Ui.T.Density.TextSmall,
            sent || row.Seen ? Ui.T.TextBrush : Ui.T.TextDimBrush);

        line.VerticalAlignment = VerticalAlignment.Center;

        // One word per event. The row names the moderator's own group and says how that group's
        // record stands; it does not itemise the places a copy went.
        var places = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };

        if (row.IsNote)
            places.Children.Add(Place(GroupOf(row.ServerId, groups), JournalEntryKind.Note));
        else if (row.State is { } state)
            places.Children.Add(Place(GroupOf(row.ServerId, groups), state));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 12,
        };

        Grid.SetColumn(when, 0);
        Grid.SetColumn(line, 1);
        Grid.SetColumn(places, 2);
        grid.Children.Add(when);
        grid.Children.Add(line);
        grid.Children.Add(places);

        return new Border
        {
            Padding = new Thickness(14, 8),
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(0, first ? 0 : Ui.T.Density.Hairline, 0, 0),
            Child = grid,
        };
    }

    /// <summary>The group a server manages, for the pill; the server's id until its group is known.</summary>
    private static string? GroupOf(string? serverId, IReadOnlyDictionary<string, string> groups)
        => serverId is { Length: > 0 } && groups.TryGetValue(serverId, out var group) ? group : serverId;

    /// <summary>Where one event went, and how far it got there.</summary>
    private static Control Place(string? name, JournalEntryKind state)
    {
        var (colour, background, word) = state switch
        {
            JournalEntryKind.Withheld => (Ui.T.Palette.TextFaint, Ui.T.Palette.Surface2, "withheld"),
            JournalEntryKind.Waiting => (Ui.T.Palette.Info, Ui.T.Palette.InfoDim, "waiting"),
            JournalEntryKind.Failed => (Ui.T.Palette.Danger, Ui.T.Palette.DangerDim, "failed"),
            JournalEntryKind.Note => (Ui.T.Palette.Warn, Ui.T.Palette.WarnDim, "note"),
            _ => (Ui.T.Palette.Ok, Ui.T.Palette.OkDim, "sent"),
        };

        var place = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (name is { Length: > 0 })
        {
            var label = Ui.Faint(name);
            label.VerticalAlignment = VerticalAlignment.Center;
            place.Children.Add(label);
        }

        place.Children.Add(Ui.Pill(word, colour, background));
        return place;
    }

    private void RenderLog()
    {
        var stats = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            ColumnSpacing = 10,
        };

        Control[] tiles =
        [
            Ui.Stat("lines seen", $"{_snapshot.LinesRead:N0}"),
            Ui.Stat("lines Modbot looks at", $"{_snapshot.BehaviourLines:N0}"),
            Ui.Stat("recognised", $"{_snapshot.RecognisedEvents:N0}"),
        ];

        for (var index = 0; index < tiles.Length; index++)
        {
            Grid.SetColumn(tiles[index], index);
            stats.Children.Add(tiles[index]);
        }

        _body.Children.Add(Ui.Card(
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Ui.Text(_snapshot.LogDetail, Ui.T.Density.TextSmall, Ui.T.TextBrush),
                    Ui.Faint(_snapshot.LogFolder),
                    stats,

                    // The ratio is a claim a suspicious moderator can check against the file
                    // itself, which is the whole reason to state it rather than say "working".
                    Ui.Dim(
                        "Modbot reads one tag out of the dozen VRChat writes, and acts on a "
                        + "handful of line shapes within it. Open the same file in Notepad and "
                        + "the counts above are checkable."),
                },
            },
            "VRChat's log"));

        _body.Children.Add(Ui.Card(
            Ui.Dim(
                "Modbot reads VRChat's log directory and nothing else on this machine. It does "
                + "not read your screenshots folder, the clipboard or the keyboard, and it does "
                + "not look at what other programs are running. It records VRChat's window, and "
                + "whatever is drawn over it, only while Clips is switched on in Settings, only "
                + "while VRChat is "
                + "running, and never the sound. Nothing it records leaves this PC: attaching a "
                + "clip or any other evidence to a case is something you do in Modbot's web "
                + "interface, in a browser, by choosing a file."),
            "What it does not read"));
    }

    /// <summary>
    /// The Settings page: a card for each group of controls, all of them kept between renders.
    /// </summary>
    /// <remarks>
    /// Everything on this page that changes reaches the screen through <see cref="RefreshSettings"/>
    /// instead of a rebuild, which is why the page can stand while the client works: a list left
    /// open stays open, a slider being dragged is not cut short, and the pointer keeps the button
    /// it is on. Anything added to this page follows the same rule — kept control, value put into
    /// it by the refresh.
    /// </remarks>
    private void RenderSettings()
    {
        DetachFromParent(_startupBox);
        DetachFromParent(_logFolderBox);

        _body.Children.Add(Ui.Card(
            new StackPanel { Spacing = 6, Children = { _startupBox } },
            "Settings"));

        _body.Children.Add(Ui.Card(DesktopOverlayCard(), "Desktop overlay"));
        _body.Children.Add(Ui.Card(DesktopNotifyCard(), "Notification overlay"));
        _body.Children.Add(Ui.Card(NotificationsCard(), "Notifications"));
        _body.Children.Add(Ui.Card(NotificationFiltersCard(), "Tell me about"));

        _body.Children.Add(Ui.Card(VoiceSettingsCard(), "Voice"));

        _body.Children.Add(Ui.Card(ClipsCard(), "Clips"));

        _body.Children.Add(Ui.Card(LogFolderSettings(), "VRChat log folder"));

        _body.Children.Add(Ui.Card(RestartCard(), "Restart"));
    }

    /// <summary>
    /// What the Settings page says right now, put into the controls it keeps, with none of them
    /// answering back. Run on every tick, whether or not the page was built again.
    /// </summary>
    private void RefreshSettings()
    {
        _renderingSwitches = true;
        try
        {
            // Shown only in an installed copy. Turned off in Windows' own Startup apps list shows off,
            // and cannot be turned back on from here.
            var startup = _snapshot.Startup;
            _startupBox.IsVisible = startup is { Visible: true };
            _startupBox.IsChecked = startup is { On: true };
            _startupBox.IsEnabled = startup is { TurnedOffInWindows: false };

            RefreshDesktopOverlayControls();
            RefreshDesktopNotifyControls();
            RefreshVoiceControls(_snapshot.VoiceOrNone);
            RefreshNotificationControls(_snapshot.NotificationsOrDefault);
            RefreshNotificationFilterControls(_snapshot.NotificationFiltersOrDefault);
            RefreshClipControls(_snapshot.ClipsOrNone);
            RefreshLogFolderControls();
        }
        finally
        {
            _renderingSwitches = false;
        }
    }

    /// <summary>
    /// Puts the snapshot into the Voice card's controls without any of them answering back.
    /// The device list is only rebuilt when the devices changed, and never while it is open.
    /// </summary>
    private void RefreshVoiceControls(VoiceStatus voice)
    {
        var settings = voice.Settings;
        _voiceOn.IsChecked = settings.On;

        if (!_voiceVolume.IsPointerOver && !_voiceVolume.IsFocused)
            _voiceVolume.Value = VoiceSettings.ClampVolume(settings.Volume);

        _voiceVolumeValue.Text = $"{(int)_voiceVolume.Value}";

        var ids = new List<string?> { null };
        var names = new List<string> { OperatingSystem.IsWindows() ? "Windows default" : "System default" };
        foreach (var device in voice.Devices)
        {
            ids.Add(device.Id);
            names.Add(device.Name);
        }

        // A chosen device that is not plugged in right now still has to be selectable, or the
        // list would silently show the default while the file says otherwise.
        if (settings.OutputDeviceId is { } wanted && !ids.Contains(wanted))
        {
            ids.Add(wanted);
            names.Add("Not connected");
        }

        if (!_voiceDevice.IsDropDownOpen && !ids.SequenceEqual(_voiceDeviceIds))
        {
            _voiceDeviceIds.Clear();
            _voiceDeviceIds.AddRange(ids);
            _voiceDevice.ItemsSource = names;
        }

        if (!_voiceDevice.IsDropDownOpen)
            _voiceDevice.SelectedIndex = Math.Max(0, _voiceDeviceIds.IndexOf(settings.OutputDeviceId));

        // The voices come out of the one download and never change while the client runs, so the
        // list is filled once and only the selection follows the settings after that.
        var voiceNames = voice.Voices.Select(v => v.Name).ToList();
        if (!_voiceName.IsDropDownOpen && !voiceNames.SequenceEqual(_voiceNames, StringComparer.Ordinal))
        {
            _voiceNames.Clear();
            _voiceNames.AddRange(voiceNames);
            _voiceName.ItemsSource = voiceNames;
        }

        if (!_voiceName.IsDropDownOpen)
        {
            var chosen = _voiceNames.FindIndex(n => string.Equals(n, settings.VoiceName, StringComparison.OrdinalIgnoreCase));
            _voiceName.SelectedIndex = chosen >= 0
                ? chosen
                : Math.Max(0, _voiceNames.FindIndex(n => string.Equals(n, VoiceModel.DefaultName, StringComparison.OrdinalIgnoreCase)));
        }

        var enabled = voice.HasOutput;
        _voiceOn.IsEnabled = enabled;
        _voiceVolume.IsEnabled = enabled;
        _voiceDevice.IsEnabled = enabled;
        _voiceName.IsEnabled = enabled;
        _voiceTest.IsEnabled = enabled && !voice.IsDownloading;

        // The size is named beside the progress: a percentage on its own tells somebody on a slow
        // connection nothing about whether to wait.
        var megabytes = $"{voice.DownloadSize / (1024.0 * 1024):N0} MB";
        _voiceLine.Text = voice switch
        {
            { HasOutput: false } => "No output device on this PC",
            { State: VoiceState.Downloading } => $"Downloading voice… {voice.DownloadProgress:P0} of {megabytes}",
            { State: VoiceState.Replacing } => $"Getting the new voice… {voice.DownloadProgress:P0} of {megabytes}",
            { State: VoiceState.Failed, Problem: { } problem } => problem,
            { State: VoiceState.Failed } => "Voice failed",
            { State: VoiceState.Ready } => "Voice ready",
            _ => "Voice not downloaded",
        };
        _voiceLine.Foreground = voice.State is VoiceState.Failed ? Ui.T.DangerBrush : Ui.T.TextFaintBrush;
    }

    /// <summary>The Voice card: on or off, how loud, through what, and a Test button.</summary>
    private Control VoiceSettingsCard()
    {
        foreach (var control in new Control[]
                 { _voiceOn, _voiceVolume, _voiceVolumeValue, _voiceDevice, _voiceName, _voiceLine, _voiceTest })
        {
            DetachFromParent(control);
        }

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                _voiceOn,
                Ui.Field("Volume", new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _voiceVolume, _voiceVolumeValue },
                }),
                Ui.Field("Voice", _voiceName),
                Ui.Field("Output device", _voiceDevice),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { _voiceTest, _voiceLine },
                },
            },
        };
    }

    /// <summary>
    /// Where the companion looks for VRChat's log. Blank means the well-known places, and the box
    /// shows which of them it found as its watermark, so a person on Linux can see that the Proton
    /// prefix was recognised without typing anything.
    /// </summary>
    private Control LogFolderSettings()
    {
        DetachFromParent(_logFolderWatching);

        var save = Ui.Button("Save", primary: true);
        save.Click += (_, _) => _actions.SetLogFolder(_logFolderBox.Text);

        var reset = Ui.Button("Use the usual folder");
        reset.Click += (_, _) =>
        {
            _logFolderBox.Text = "";
            _actions.SetLogFolder(null);
        };

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Ui.Field("Folder", _logFolderBox),
                _logFolderWatching,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { save, reset },
                },
            },
        };
    }

    /// <summary>
    /// The folder the log reader is watching, and the box that can point it somewhere else. The
    /// box is only refilled while the person is not typing in it.
    /// </summary>
    private void RefreshLogFolderControls()
    {
        if (!_logFolderBox.IsFocused)
        {
            _logFolderBox.Text = _snapshot.LogFolderConfigured ?? "";
            _logFolderBox.Watermark = _snapshot.LogFolder;
        }

        _logFolderWatching.Text = $"Watching {_snapshot.LogFolder}";
    }

    /// <summary>The headset panel: whether it is up, what it shows, and where it sits.</summary>
    private void RenderSteamVr()
    {
        var overlay = _snapshot.OverlayOrNone;

        _renderingSwitches = true;
        try
        {
            _overlayOnBox.IsChecked = overlay.On;
        }
        finally
        {
            _renderingSwitches = false;
        }

        DetachFromParent(_overlayOnBox);

        var pill = !overlay.On
            ? Ui.Pill("Off", Ui.T.Palette.TextFaint, Ui.T.Palette.Surface2)
            : overlay.Attached
                ? Ui.Pill("Attached", Ui.T.Palette.Ok, Ui.T.Palette.OkDim)
                : overlay.State switch
                {
                    "refused" => Ui.Pill("Refused", Ui.T.Palette.Danger, Ui.T.Palette.DangerDim),
                    "SteamVR not installed" or "not set up" => Ui.Pill("No SteamVR", Ui.T.Palette.Info, Ui.T.Palette.InfoDim),
                    _ => Ui.Pill("Not running", Ui.T.Palette.Warn, Ui.T.Palette.WarnDim),
                };

        // The placement settings stay whichever way the switch is set: a moderator arranges where
        // the panel will sit and then turns it on, not the other way round. What goes away while
        // it is off is only what there is nothing to report on.
        var top = new StackPanel { Spacing = 12, Children = { _overlayOnBox } };
        Control header = pill;

        if (overlay.On)
        {
            var attach = Ui.Button("Look for SteamVR now");
            attach.Click += (_, _) => _actions.AttachSteamVr();
            header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { pill, attach } };

            var stats = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                ColumnSpacing = 10,
            };

            Control[] tiles =
            [
                Ui.Stat("attached since", Clock(overlay.AttachedAt)),
                Ui.Stat("frames drawn", $"{overlay.FramesDrawn:N0}"),
                Ui.Stat("last drawn", Clock(overlay.LastDrawnAt)),
            ];

            for (var index = 0; index < tiles.Length; index++)
            {
                Grid.SetColumn(tiles[index], index);
                stats.Children.Add(tiles[index]);
            }

            top.Children.Add(Ui.Text(overlay.Detail, Ui.T.Density.TextSmall, Ui.T.TextBrush));
            top.Children.Add(stats);
        }

        _body.Children.Add(Ui.Card(top, "SteamVR", header));

        if (overlay.On)
        {
            var showing = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Line("Screen", overlay.Showing),
                    Line("People", $"{overlay.People:N0}"),
                    Line("Roster", overlay.RosterAge),
                    Line("Alert", overlay.Alert ?? "none"),
                    Line("Problem", overlay.Problem ?? "none"),
                    Line("Reading from", overlay.FollowingServer ?? "no server"),
                },
            };

            if (overlay.PinnedSample is { } pinned)
                showing.Children.Add(Line("Pinned sample", pinned));

            _body.Children.Add(Ui.Card(showing, "Showing"));
        }

        _body.Children.Add(Ui.Card(PlacementControls(overlay.PlacementOrDefault, overlay.Holding), "Placement"));

        RenderNotificationOverlay();
    }

    /// <summary>
    /// Where the panel is and how it looks: the anchor as a row of choices, the offset as read,
    /// the size, opacity and curve as sliders, and one button that brings it back in front of
    /// the head.
    /// </summary>
    private Control PlacementControls(OverlayPlacement placement, string? holding)
    {
        _renderingSwitches = true;
        try
        {
            Refill(_widthSlider, placement.Width);
            Refill(_opacitySlider, placement.Opacity);
            Refill(_curveSlider, placement.Curve);
        }
        finally
        {
            _renderingSwitches = false;
        }

        DetachFromParent(_widthSlider);
        DetachFromParent(_opacitySlider);
        DetachFromParent(_curveSlider);

        var anchors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (anchor, caption) in new[]
        {
            (OverlayAnchor.Head, "Head"),
            (OverlayAnchor.LeftHand, "Left hand"),
            (OverlayAnchor.RightHand, "Right hand"),
            (OverlayAnchor.World, "Room"),
        })
        {
            var button = Ui.Button(caption, primary: placement.Anchor == anchor);
            var chosen = anchor;
            button.Click += (_, _) => _actions.AnchorOverlay(chosen);
            anchors.Children.Add(button);
        }

        var reset = Ui.Button("Put it back in front of me");
        reset.Click += (_, _) => _actions.PlaceOverlay(OverlayPlacement.Default with
        {
            Width = placement.Width,
            Opacity = placement.Opacity,
            Curve = placement.Curve,
        });

        var offset = placement.Offset;
        var where = placement.Anchor switch
        {
            OverlayAnchor.World => $"{offset.X:0.00} m, {offset.Y:0.00} m up, {-offset.Z:0.00} m into the room",
            _ => $"{offset.X:0.00} m right, {-offset.Y:0.00} m down, {-offset.Z:0.00} m ahead",
        };

        var lines = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Field("Fixed to", anchors),
                Line("Offset", where),
                Ui.Field($"Width {placement.Width:0.00} m", Stepper(_widthSlider, 0.05)),
                Ui.Field($"Opacity {placement.Opacity:0%}", Stepper(_opacitySlider, 0.05)),
                Ui.Field($"Curve {placement.Curve:0%}", Stepper(_curveSlider, 0.05)),
                Line("Picture", $"{OverlayHost.DefaultResolution}×{OverlayHost.DefaultResolution}"),
                reset,
            },
        };

        if (holding is not null)
            lines.Children.Insert(1, Line("Held in", holding));

        return lines;
    }

    /// <summary>
    /// A slider between a minus and a plus, for a hand in a headset that cannot land a thumb: the
    /// buttons are large, each one moves the value by a step, and the slider is there for a
    /// mouse. The buttons and slider are rebuilt around the one slider that is kept.
    /// </summary>
    private static Control Stepper(Slider slider, double step)
    {
        var minus = StepButton("−", () => slider.Value = Math.Max(slider.Minimum, slider.Value - step));
        var plus = StepButton("+", () => slider.Value = Math.Min(slider.Maximum, slider.Value + step));

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { minus, slider, plus },
        };
    }

    private static Button StepButton(string caption, Action press)
    {
        var button = Ui.Button(caption);
        button.Width = 48;
        button.Height = 40;
        button.FontSize = Ui.T.Density.TextBase * 1.3;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Click += (_, _) => press();
        return button;
    }

    private Slider PlacementSlider(double minimum, double maximum, double step, Func<OverlayPlacement, double, OverlayPlacement> change)
    {
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Width = 300,
            MinHeight = 40,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        slider.ValueChanged += (_, e) =>
        {
            if (!_renderingSwitches)
                _actions.PlaceOverlay(change(_snapshot.OverlayOrNone.PlacementOrDefault, e.NewValue));
        };

        return slider;
    }

    /// <summary>Only refilled while nobody is on the slider: the window redraws on a timer.</summary>
    private static void Refill(Slider slider, double value)
    {
        if (!slider.IsPointerOver && !slider.IsFocused && Math.Abs(slider.Value - value) > 0.001)
            slider.Value = value;
    }

    /// <summary>Only with <c>MODBOT_DEBUG_MODE=1</c>: the overlay's picture on the desktop, and sample screens to pin into it.</summary>
    private void RenderDebug()
    {
        var overlay = _snapshot.OverlayOrNone;

        var show = Ui.Button("Show overlay window", primary: true);
        show.Click += (_, _) => _actions.ShowOverlayWindow();

        var attach = Ui.Button("Look for SteamVR now");
        attach.Click += (_, _) => _actions.AttachSteamVr();

        _body.Children.Add(Ui.Card(
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { show, attach } },
            "Overlay"));

        var samples = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var sample in Enum.GetValues<OverlaySample>())
        {
            var button = Ui.Button(OverlaySamples.Name(sample));
            button.Click += (_, _) => _actions.PinOverlaySample(sample);
            samples.Children.Add(button);
        }

        var live = Ui.Button("Live", primary: overlay.PinnedSample is not null);
        live.Click += (_, _) => _actions.PinOverlaySample(null);
        samples.Children.Add(live);

        _body.Children.Add(Ui.Card(
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    samples,
                    Ui.Faint(overlay.PinnedSample is { } pinned ? $"Pinned: {pinned}" : "Showing the live screen"),
                },
            },
            "Sample screens"));
    }

    private static string Clock(DateTimeOffset? at) => at is { } time ? time.ToLocalTime().ToString("HH:mm:ss") : "—";

    private static Control Line(string label, string value)
    {
        var caption = Ui.Label(label);
        caption.Width = 120;
        caption.VerticalAlignment = VerticalAlignment.Center;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { caption, Ui.Text(value, Ui.T.Density.TextSmall, Ui.T.TextBrush) },
        };
    }

    private static void DetachFromParent(Control control)
    {
        if (control.Parent is Panel panel)
            panel.Children.Remove(control);
    }

    private static Control Dock(Control control, Dock side)
    {
        DockPanel.SetDock(control, side);
        return control;
    }
}

/// <summary>What the window can ask the application to do.</summary>
/// <remarks>
/// A small surface on purpose: pause, unpair, pair from a token, open the pairing page. There is
/// nothing here that acts on VRChat or on a group — the client observes and reports, and a
/// moderator acting on what they have seen does it through the normal authenticated web interface
/// as themselves.
/// </remarks>
/// <param name="PairAsync">Pairs from a pasted pairing token or link.</param>
/// <param name="OpenPairingPageAsync">Opens the pairing page in the moderator's browser.</param>
/// <param name="SetLogFolder">Points the log reader at a folder; null or blank means the well-known places.</param>
/// <param name="AttachSteamVr">Looks for SteamVR now rather than at the next ten-second look.</param>
/// <param name="ShowOverlayWindow">Opens the window that shows the overlay's last frame. Debug page only.</param>
/// <param name="PinOverlaySample">Pins a sample screen into the overlay, or null for the live screen. Debug page only.</param>
/// <param name="PlaceOverlay">Moves the panel: a size, an opacity, a curve, or back in front of the head.</param>
/// <param name="AnchorOverlay">Fixes the panel to the head, a hand or the room, putting it where that anchor makes sense.</param>
/// <param name="SetVoice">The Voice card changed: the whole voice settings record as the controls now read.</param>
/// <param name="TestVoice">Speaks one test line.</param>
/// <param name="SetEventsFilters">The Events page's filter bar changed; the chips are remembered in settings.</param>
public sealed record MainWindowActions(
    Action<string> TogglePause,
    Action<string> Unpair,
    Func<string, Task<PairingAttemptResult>> PairAsync,
    Func<Task> OpenPairingPageAsync,
    Action<bool> SetStartWithWindows,
    Action<string?> SetLogFolder,
    Action AttachSteamVr,
    Action ShowOverlayWindow,
    Action<OverlaySample?> PinOverlaySample,
    Action<OverlayPlacement> PlaceOverlay,
    Action<OverlayAnchor> AnchorOverlay,
    Action<VoiceSettings> SetVoice,
    Action TestVoice)
{
    /// <summary>Added after the positional list so nothing that builds the record has to change.</summary>
    public Action<EventFilterSet> SetEventsFilters { get; init; } = _ => { };

    /// <summary>The SteamVR page's <strong>Overlay on</strong> switch. Added the same way.</summary>
    public Action<bool> SetOverlayOn { get; init; } = _ => { };

    /// <summary>The Notifications card changed: the sound's own switch and its own volume.</summary>
    public Action<NotificationSettings> SetNotifications { get; init; } = _ => { };

    /// <summary>Plays one bleep, whether or not the sound is switched on.</summary>
    public Action TestBleep { get; init; } = () => { };

    /// <summary>
    /// The Notifications card's filter list changed: which kinds of event raise a notification, by
    /// each of the three ways.
    /// </summary>
    public Action<NotificationFilters> SetNotificationFilters { get; init; } = _ => { };

    /// <summary>
    /// The window was closed with the X and the client is still in the tray. Shows the notice, the
    /// first few times only.
    /// </summary>
    public Action ClosedToTray { get; init; } = () => { };

    /// <summary>
    /// The Settings page's Restart button. True once a fresh copy has been started and this one is
    /// going; false when no copy could be started, in which case nothing has been stopped.
    /// </summary>
    public Func<Task<bool>> RestartAsync { get; init; } = () => Task.FromResult(false);

    /// <summary>
    /// The Settings page's Desktop overlay card changed: the whole record as the controls now
    /// read. Added the same way.
    /// </summary>
    public Action<DesktopOverlaySettings> SetDesktopOverlay { get; init; } = _ => { };

    /// <summary>
    /// Opens the desktop overlay from the settings card, which is the way in when the shortcut
    /// could not be registered. Added the same way.
    /// </summary>
    public Action ShowDesktopOverlay { get; init; } = () => { };

    /// <summary>The Notification overlay card changed: the whole settings record as it now reads.</summary>
    public Action<NotifyOverlaySettings> SetNotifyOverlay { get; init; } = _ => { };

    /// <summary>
    /// The Settings page's notification overlay card changed: the switch, the corner and the
    /// seconds as the controls now read. Added the same way.
    /// </summary>
    public Action<DesktopNotifySettings> SetDesktopNotifyOverlay { get; init; } = _ => { };

    /// <summary>
    /// The Clips card changed: the switch, the minutes and the folder as the controls now read.
    /// Added after the positional list the same way the others were.
    /// </summary>
    public Action<ClipSettings> SetClips { get; init; } = _ => { };

    /// <summary>
    /// <strong>Save a clip</strong>: write out the last few minutes that are being kept. Does
    /// nothing at all when nothing is being kept.
    /// </summary>
    public Action SaveClip { get; init; } = () => { };

    public static MainWindowActions None { get; } = new(
        _ => { },
        _ => { },
        _ => Task.FromResult(new PairingAttemptResult(false, "Not ready yet.")),
        () => Task.CompletedTask,
        _ => { },
        _ => { },
        () => { },
        () => { },
        _ => { },
        _ => { },
        _ => { },
        _ => { },
        () => { });
}
