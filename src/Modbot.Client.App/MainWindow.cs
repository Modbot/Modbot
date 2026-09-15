using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Client.Ingest;
using Modbot.Client.Journal;
using Modbot.Client.Pipeline;
using Modbot.Client.Presentation;
using Modbot.Overlay;

namespace Modbot.Client.App;

/// <summary>Which page the window is showing.</summary>
internal enum Page
{
    Servers,
    Sent,
    Log,
    Settings,
}

/// <summary>
/// The client window: what Modbot is reading, where it reports, and everything it has sent.
/// </summary>
/// <remarks>
/// <para><strong>This window is the trust argument made visible.</strong> A volunteer moderator is
/// being asked to run background software on a personal machine that watches what they do in
/// VRChat. Suspicion is the correct response. "Read the source" answers it for people who read C#
/// and for nobody else, so the answer for everybody else is a screen: exactly what left the
/// machine, in plain English, on demand; a pause that stops transmission immediately and shows it;
/// and a program that is never invisible while it runs.</para>
/// <para><strong>What this window does not have, and will not get.</strong> There is no screen
/// capture, no "attach a screenshot", and nothing that reads VRChat's screenshot folder or any
/// other folder on the machine. A moderator attaching evidence to a case does it in the web UI, in
/// a browser, by choosing a file — a human action in an application people already trust with file
/// dialogs. Putting it here instead would hand this program the one capability that would make its
/// resemblance to an infostealer complete, and <c>ClientSourceGuardTests</c> fails the build if it
/// ever appears.</para>
/// <para>Built in code rather than markup because a reader auditing this program should be able to
/// see what it displays without also learning a XAML dialect.</para>
/// </remarks>
public sealed class MainWindow : Window
{
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly StackPanel _nav = new() { Spacing = 2 };
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
    private bool _renderingSwitches;

    private Page _page = Page.Servers;
    private ClientAppSnapshot _snapshot = ClientAppSnapshot.Empty;
    private MainWindowActions _actions = MainWindowActions.None;

    public MainWindow()
    {
        Title = "Modbot";
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

        _startupBox = new CheckBox { Content = Ui.Text("Start Modbot Client when my computer starts", Ui.T.Density.TextSmall, Ui.T.TextBrush) };
        _startupBox.IsCheckedChanged += (_, _) =>
        {
            if (!_renderingSwitches)
                _actions.SetStartWithWindows(_startupBox.IsChecked == true);
        };

        var main = new ScrollViewer { Padding = new Thickness(20), Content = _body };
        Grid.SetColumn(main, 1);

        Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("216,*"),
            Children = { Sidebar(), main },
        };
    }

    /// <summary>Rebuilds the window from a snapshot. Cheap enough to call on a timer.</summary>
    public void Render(ClientAppSnapshot snapshot, MainWindowActions actions)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(actions);

        _snapshot = snapshot;
        _actions = actions;

        RenderHealth();
        RenderNav();
        RenderPage();
    }

    private Control Sidebar()
    {
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 20),
            Children =
            {
                new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(5),
                    Background = Ui.T.AccentBrush,
                    Child = Ui.Text("M", 12, Ui.T.AccentForegroundBrush, FontWeight.SemiBold, wrap: false),
                },
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        Ui.Text("Modbot", Ui.T.Density.TextBase, Ui.T.TextBrush, FontWeight.SemiBold, wrap: false),
                        Ui.Faint("client"),
                    },
                },
            },
        };

        var health = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { _healthDot, _healthLine },
        };

        var panel = new DockPanel();
        panel.Children.Add(Dock(brand, Avalonia.Controls.Dock.Top));
        panel.Children.Add(Dock(_nav, Avalonia.Controls.Dock.Top));
        panel.Children.Add(Dock(health, Avalonia.Controls.Dock.Bottom));
        panel.Children.Add(new Panel());

        return new Border
        {
            Background = Ui.T.SurfaceBrush,
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(0, 0, Ui.T.Density.Hairline, 0),
            Padding = new Thickness(14, 16),
            Child = panel,
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
        _nav.Children.Clear();

        _nav.Children.Add(NavItem(
            Page.Servers, "Servers", _snapshot.Servers.Count == 0 ? null : $"{_snapshot.Servers.Count}"));
        _nav.Children.Add(NavItem(
            Page.Sent, "What I've sent", _snapshot.Journal.Count == 0 ? null : $"{_snapshot.Journal.Count}"));
        _nav.Children.Add(NavItem(Page.Log, "Log", null));
        _nav.Children.Add(NavItem(Page.Settings, "Settings", null));
    }

    private Control NavItem(Page page, string caption, string? badge)
    {
        var selected = _page == page;

        var row = new DockPanel { LastChildFill = false };
        var label = Ui.Text(
            caption,
            Ui.T.Density.TextSmall,
            selected ? Ui.T.TextBrush : Ui.T.TextDimBrush,
            selected ? FontWeight.Medium : FontWeight.Normal,
            wrap: false);

        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(Dock(label, Avalonia.Controls.Dock.Left));

        if (badge is not null)
        {
            var count = Ui.Faint(badge);
            count.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(Dock(count, Avalonia.Controls.Dock.Right));
        }

        var button = new Button
        {
            Content = row,
            Height = Ui.T.Density.RowHeight,
            Padding = new Thickness(10, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = selected ? Ui.T.Surface3Brush : Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Ui.T.Density.Radius),
        };

        button.Click += (_, _) =>
        {
            _page = page;
            RenderNav();
            RenderPage();
        };

        return button;
    }

    private void RenderPage()
    {
        _body.Children.Clear();

        foreach (var warning in _snapshot.Warnings)
            _body.Children.Add(Ui.Note(warning.Message, Severity(warning.Severity)));

        switch (_page)
        {
            case Page.Sent:
                RenderSent();
                break;
            case Page.Log:
                RenderLog();
                break;
            case Page.Settings:
                RenderSettings();
                break;
            default:
                RenderServers();
                break;
        }
    }

    private static Color Severity(WarningSeverity severity) => severity switch
    {
        WarningSeverity.Critical => Ui.T.Palette.Danger,
        WarningSeverity.Warning => Ui.T.Palette.Warn,
        _ => Ui.T.Palette.Info,
    };

    private void RenderServers()
    {
        if (_snapshot.Servers.Count == 0)
        {
            _body.Children.Add(Ui.Card(
                Ui.Dim(
                    "No servers paired. Press \"Pair with a server\" below to add the group you moderate."),
                "Not reporting anywhere"));
        }

        foreach (var server in _snapshot.Servers)
            _body.Children.Add(ServerCard(server));

        RenderPairingNotice();
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

    private Control ServerCard(ServerRow server)
    {
        var pause = Ui.Button(server.IsPaused ? "Resume reporting" : "Pause reporting");
        pause.Click += (_, _) => _actions.TogglePause(server.ServerId);

        var unpair = Ui.Button("Unpair", danger: true);
        unpair.Click += (_, _) => _actions.Unpair(server.ServerId);

        var stats = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            ColumnSpacing = 10,
        };

        // "Already known" rather than "deduplicated": the technical word sounds like a loss and is
        // not. Several moderators in one instance all report the same join, and the server keeping
        // one of them is the system working exactly as designed.
        Control[] tiles =
        [
            Ui.Stat("recorded", $"{server.AcceptedTotal:N0}"),
            Ui.Stat("already known", $"{server.DeduplicatedTotal:N0}"),
            Ui.Stat("queued", $"{server.Pending:N0}", server.Pending > 0 ? Ui.T.WarnBrush : Ui.T.TextBrush),
        ];

        for (var index = 0; index < tiles.Length; index++)
        {
            Grid.SetColumn(tiles[index], index);
            stats.Children.Add(tiles[index]);
        }

        var body = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Ui.Dim($"{server.Address}  ·  {server.ManagedGroupId}"),
                Ui.Text(server.Detail, Ui.T.Density.TextSmall, DetailBrush(server.State)),
                stats,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { pause, unpair },
                },
            },
        };

        return Ui.Card(body, server.ServerId, StatePill(server));
    }

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

    private void RenderSent()
    {
        _body.Children.Add(Ui.Card(
            Ui.Dim(
                "Every line here is something this program disclosed about you to a Modbot server, "
                + "newest first, and the list is kept on your disk so it is still here tomorrow."),
            "What Modbot has sent"));

        if (_snapshot.Journal.Count == 0)
        {
            _body.Children.Add(Ui.Card(Ui.Dim("Nothing has been sent yet.")));
            return;
        }

        var rows = new StackPanel { Spacing = 0 };
        var first = true;

        foreach (var entry in _snapshot.Journal)
        {
            rows.Children.Add(JournalRow(entry, first));
            first = false;
        }

        _body.Children.Add(new Border
        {
            Background = Ui.T.SurfaceBrush,
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(Ui.T.Density.Hairline),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = rows,
        });
    }

    private static Control JournalRow(JournalEntry entry, bool first)
    {
        var (colour, background, word) = entry.Kind switch
        {
            JournalEntryKind.Withheld => (Ui.T.Palette.TextFaint, Ui.T.Palette.Surface2, "withheld"),
            JournalEntryKind.Note => (Ui.T.Palette.Warn, Ui.T.Palette.WarnDim, "note"),
            _ => (Ui.T.Palette.Ok, Ui.T.Palette.OkDim, "sent"),
        };

        var pill = Ui.Pill(word, colour, background);
        pill.HorizontalAlignment = HorizontalAlignment.Left;

        var line = Ui.Text(
            entry.Summary,
            Ui.T.Density.TextSmall,
            entry.Kind == JournalEntryKind.Sent ? Ui.T.TextBrush : Ui.T.TextDimBrush);

        line.VerticalAlignment = VerticalAlignment.Center;

        var server = Ui.Faint(entry.ServerId);
        server.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("96,*,Auto"),
            ColumnSpacing = 12,
        };

        Grid.SetColumn(pill, 0);
        Grid.SetColumn(line, 1);
        Grid.SetColumn(server, 2);
        grid.Children.Add(pill);
        grid.Children.Add(line);
        grid.Children.Add(server);

        return new Border
        {
            Padding = new Thickness(14, 9),
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(0, first ? 0 : Ui.T.Density.Hairline, 0, 0),
            Child = grid,
        };
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
                + "not capture the screen, read your screenshots folder, read the clipboard or "
                + "the keyboard, or look at what other programs are running. Attaching evidence "
                + "to a case is something you do in Modbot's web interface, in a browser, by "
                + "choosing a file."),
            "What it does not read"));
    }

    private void RenderSettings()
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
        }
        finally
        {
            _renderingSwitches = false;
        }

        DetachFromParent(_startupBox);

        _body.Children.Add(Ui.Card(
            new StackPanel { Spacing = 6, Children = { _startupBox } },
            "Settings"));
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
public sealed record MainWindowActions(
    Action<string> TogglePause,
    Action<string> Unpair,
    Func<string, Task<PairingAttemptResult>> PairAsync,
    Func<Task> OpenPairingPageAsync,
    Action<bool> SetStartWithWindows)
{
    public static MainWindowActions None { get; } = new(
        _ => { },
        _ => { },
        _ => Task.FromResult(new PairingAttemptResult(false, "Not ready yet.")),
        () => Task.CompletedTask,
        _ => { });
}
