using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.App;

/// <summary>
/// The window's keyboard: the registry, the one key handler, the command palette on
/// <c>Ctrl+K</c> and the shortcut sheet on <c>?</c>.
/// </summary>
/// <remarks>
/// <para>The rules are in <see cref="ShortcutRegistry"/>, with no Avalonia in them. This file
/// only turns a key event into a token, asks the registry, and draws the two panels.</para>
/// <para>The handler tunnels from the window, so a key the registry claims never reaches the
/// control under it; a key it does not claim goes on as normal. Nothing here reads the keyboard
/// outside this window: these are the window's own key events, delivered only while it has
/// focus, which is what any program gets.</para>
/// </remarks>
public sealed partial class MainWindow
{
    private enum PanelKind
    {
        None,
        Palette,
        Sheet,
    }

    /// <summary>Something the palette can run: a page, a key that works here, or a button on the open page.</summary>
    private sealed record PaletteItem(string Group, string Label, string? Keys, Action Run);

    private readonly ShortcutRegistry _registry = new();

    private readonly DispatcherTimer _chordTimer = new() { Interval = ShortcutRegistry.ChordTimeout };

    private string? _pendingChord;

    private PanelKind _panel;

    private readonly Border _panelLayer = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xA0, 0x0f, 0x0e, 0x15)),
        IsVisible = false,
        Padding = new Thickness(16, 64, 16, 16),
    };

    private readonly ContentControl _panelHolder = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Width = 560,
    };

    // The palette is built once: its text box keeps what was typed across the one-second render.
    private readonly TextBox _paletteBox = Ui.Input("Search pages and actions");

    private readonly StackPanel _paletteList = new();

    private Border? _palette;

    private List<PaletteItem> _paletteItems = [];

    private int _paletteCursor;

    private void SetUpKeyboard()
    {
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        _chordTimer.Tick += (_, _) =>
        {
            _pendingChord = null;
            _chordTimer.Stop();
        };

        _panelLayer.Child = _panelHolder;
        _panelLayer.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, _panelLayer))
                ClosePanel();
        };

        _paletteBox.BorderThickness = new Thickness(0);
        _paletteBox.Background = Brushes.Transparent;
        _paletteBox.Height = 44;
        _paletteBox.FontSize = Ui.T.Density.TextBase;
        _paletteBox.TextChanged += (_, _) =>
        {
            _paletteCursor = 0;
            RefreshPalette();
        };
        _paletteBox.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down:
                    _paletteCursor = Math.Min(_paletteItems.Count - 1, _paletteCursor + 1);
                    RefreshPalette();
                    e.Handled = true;
                    break;
                case Key.Up:
                    _paletteCursor = Math.Max(0, _paletteCursor - 1);
                    RefreshPalette();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (_paletteCursor >= 0 && _paletteCursor < _paletteItems.Count)
                        RunPaletteItem(_paletteItems[_paletteCursor]);
                    e.Handled = true;
                    break;
            }
        };
    }

    /// <summary>
    /// The window's keys and the open page's keys, set every time the sidebar is drawn so the
    /// sheet lists exactly what works on the screen that is open.
    /// </summary>
    private void RegisterWindowKeys()
    {
        var keys = new List<Shortcut>
        {
            new("mod+k", "Search and commands", ShortcutGroup.General, TogglePalette),
            new("?", "Keyboard shortcuts", ShortcutGroup.General, ToggleSheet),
            new("escape", "Close", ShortcutGroup.General, Escape),
            new("g s", "Servers", ShortcutGroup.GoTo, () => GoTo(Page.Servers)),
            new("g e", "Events", ShortcutGroup.GoTo, () => GoTo(Page.Events)),
            new("g v", "SteamVR", ShortcutGroup.GoTo, () => GoTo(Page.SteamVr)),
            new("g l", "Log", ShortcutGroup.GoTo, () => GoTo(Page.Log)),
            new("g t", "Settings", ShortcutGroup.GoTo, () => GoTo(Page.Settings)),
        };

        if (_snapshot.DebugMode)
            keys.Add(new("g d", "Debug", ShortcutGroup.GoTo, () => GoTo(Page.Debug)));

        _registry.Set("window", keys);
        _registry.Set("page", _page is Page.Events ? EventsPageKeys() : []);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || TokenFor(e) is not { } token)
            return;

        var outcome = _registry.Decide(token, _pendingChord, IsTyping(), _panel is not PanelKind.None);

        _pendingChord = null;
        _chordTimer.Stop();

        if (outcome.Pending is { } pending)
        {
            _pendingChord = pending;
            _chordTimer.Start();
            e.Handled = true;
            return;
        }

        if (outcome.Run is not { } shortcut)
            return;

        e.Handled = true;
        shortcut.Run();
    }

    /// <summary>
    /// One key event as the registry's token. Letters and digits come from the key itself, so
    /// <c>Ctrl+K</c> is <c>k</c> whatever the control character would have been; everything
    /// else printable comes from what the key produced, so <c>?</c> is <c>?</c> on any layout.
    /// </summary>
    private static string? TokenFor(KeyEventArgs e)
    {
        var mod = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        var key = e.Key switch
        {
            Key.Escape => "escape",
            Key.Enter => "enter",
            Key.Up => "arrowup",
            Key.Down => "arrowdown",
            Key.Left => "arrowleft",
            Key.Right => "arrowright",
            Key.Space => "space",
            Key.Tab => "tab",
            Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin => null,
            >= Key.A and <= Key.Z => ((char)('a' + (e.Key - Key.A))).ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (e.Key - Key.D0))).ToString(),
            _ => e.KeySymbol is { Length: 1 } symbol && !char.IsControl(symbol[0]) && !char.IsWhiteSpace(symbol[0])
                ? symbol
                : null,
        };

        return KeyTokens.Token(key, mod, alt, shift);
    }

    /// <summary>Whether a text box has focus, in which case a letter is a letter.</summary>
    private bool IsTyping()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    private void ClearFocus()
        => TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();

    /// <summary>
    /// <c>Esc</c>: the nearest thing that can close, closes. The palette or the sheet first, then the filter
    /// picker, then an open row, then the selection.
    /// </summary>
    private void Escape()
    {
        if (_panel is not PanelKind.None)
        {
            ClosePanel();
            return;
        }

        if (_page is Page.Events)
            EscapeEvents();
    }

    // ── The panels ────────────────────────────────────────────────────────────────────────────

    private void TogglePalette()
    {
        if (_panel is PanelKind.Palette)
            ClosePanel();
        else
            OpenPalette();
    }

    private void ToggleSheet()
    {
        if (_panel is PanelKind.Sheet)
            ClosePanel();
        else
            OpenSheet();
    }

    private void OpenPalette()
    {
        _palette ??= BuildPalette();
        _paletteBox.Text = "";
        _paletteCursor = 0;
        RefreshPalette();
        ShowPanel(PanelKind.Palette, _palette);

        Dispatcher.UIThread.Post(() =>
        {
            _paletteBox.Focus();
            _paletteBox.SelectAll();
        });
    }

    private void OpenSheet()
    {
        ShowPanel(PanelKind.Sheet, BuildSheet());
        ClearFocus();
    }

    private void ShowPanel(PanelKind kind, Control card)
    {
        _panel = kind;
        _panelHolder.Content = card;
        _panelLayer.IsVisible = true;
    }

    private void ClosePanel()
    {
        _panel = PanelKind.None;
        _panelLayer.IsVisible = false;
        _panelHolder.Content = null;

        // Focus left on a hidden text box would make every letter a letter for good.
        ClearFocus();
    }

    // ── The palette ───────────────────────────────────────────────────────────────────────────

    private Border BuildPalette()
    {
        var head = new DockPanel { LastChildFill = true };
        var esc = Ui.Kbd("escape");
        esc.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(esc, Avalonia.Controls.Dock.Right);
        head.Children.Add(esc);
        head.Children.Add(_paletteBox);

        var body = new StackPanel
        {
            Children =
            {
                head,
                Ui.Hairline(),
                new ScrollViewer
                {
                    MaxHeight = 400,
                    Padding = new Thickness(4),
                    Content = _paletteList,
                },
            },
        };

        return Ui.Sheet(body, 560);
    }

    /// <summary>
    /// What the palette can run right now: the pages, the keys that work on this screen, and the
    /// buttons on the open page.
    /// </summary>
    private IEnumerable<PaletteItem> PaletteItems()
    {
        var listed = _registry.Listed;
        var goTo = listed.Where(s => s.Group is ShortcutGroup.GoTo).ToDictionary(s => s.Label, s => s.Keys, StringComparer.Ordinal);

        foreach (var (page, label) in new[]
        {
            (Page.Servers, "Servers"),
            (Page.Events, "Events"),
            (Page.SteamVr, "SteamVR"),
            (Page.Log, "Log"),
            (Page.Settings, "Settings"),
            (Page.Debug, "Debug"),
        })
        {
            if (page is Page.Debug && !_snapshot.DebugMode)
                continue;

            var chosen = page;
            yield return new PaletteItem("Go to", label, goTo.GetValueOrDefault(label), () => GoTo(chosen));
        }

        foreach (var action in PageActions())
            yield return action;

        foreach (var shortcut in listed)
        {
            if (shortcut.Group is ShortcutGroup.GoTo || shortcut.Keys is "mod+k" or "escape")
                continue;

            var run = shortcut.Run;
            yield return new PaletteItem("Keys on this page", shortcut.Label, shortcut.Keys, run);
        }
    }

    /// <summary>The buttons on the open page, by name.</summary>
    private IEnumerable<PaletteItem> PageActions()
    {
        const string group = "On this page";

        switch (_page)
        {
            case Page.Servers:
                yield return new PaletteItem(group, "Pair with a server", null,
                    () => _ = CrashGuard.RunAsync("opening the pairing page", _actions.OpenPairingPageAsync));

                foreach (var server in _snapshot.Servers)
                {
                    var id = server.ServerId;
                    yield return new PaletteItem(
                        group,
                        server.IsPaused ? $"Resume reporting to {server.GroupName}" : $"Pause reporting to {server.GroupName}",
                        null,
                        () => _actions.TogglePause(id));
                }

                break;

            case Page.Events:
                if (!_filters.IsEmpty)
                    yield return new PaletteItem(group, "Clear filters", null, () => SetFilters(EventFilterSet.Empty));
                break;

            case Page.SteamVr:
                yield return new PaletteItem(group, "Look for SteamVR now", null, _actions.AttachSteamVr);
                yield return new PaletteItem(group, "Put it back in front of me", null, () =>
                {
                    var placement = _snapshot.OverlayOrNone.PlacementOrDefault;
                    _actions.PlaceOverlay(Modbot.Companion.Overlay.OverlayPlacement.Default with
                    {
                        Width = placement.Width,
                        Opacity = placement.Opacity,
                        Curve = placement.Curve,
                    });
                });
                break;

            case Page.Settings:
                yield return new PaletteItem(group, "Test voice", null, _actions.TestVoice);
                break;

            case Page.Debug when _snapshot.DebugMode:
                yield return new PaletteItem(group, "Show overlay window", null, _actions.ShowOverlayWindow);
                yield return new PaletteItem(group, "Look for SteamVR now", null, _actions.AttachSteamVr);
                break;
        }
    }

    /// <summary>Redraws the palette's list from what was typed and where the cursor is.</summary>
    private void RefreshPalette()
    {
        if (_palette is null)
            return;

        var words = (_paletteBox.Text ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _paletteItems = PaletteItems()
            .Where(item => words.All(w =>
                item.Label.Contains(w, StringComparison.OrdinalIgnoreCase)
                || item.Group.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        _paletteCursor = Math.Clamp(_paletteCursor, 0, Math.Max(0, _paletteItems.Count - 1));
        _paletteList.Children.Clear();

        if (_paletteItems.Count == 0)
        {
            var none = Ui.Dim("Nothing matches");
            none.Margin = new Thickness(10, 16);
            none.HorizontalAlignment = HorizontalAlignment.Center;
            _paletteList.Children.Add(none);
            return;
        }

        string? lastGroup = null;
        for (var index = 0; index < _paletteItems.Count; index++)
        {
            var item = _paletteItems[index];
            if (item.Group != lastGroup)
            {
                var heading = Ui.Label(item.Group);
                heading.Margin = new Thickness(10, lastGroup is null ? 6 : 10, 10, 4);
                _paletteList.Children.Add(heading);
                lastGroup = item.Group;
            }

            var label = Ui.Text(item.Label, Ui.T.Density.TextSmall, Ui.T.TextBrush, wrap: false);
            label.VerticalAlignment = VerticalAlignment.Center;

            var content = new DockPanel { LastChildFill = true };
            if (item.Keys is { } keys)
            {
                var cap = Ui.Kbd(keys);
                DockPanel.SetDock(cap, Avalonia.Controls.Dock.Right);
                content.Children.Add(cap);
            }

            content.Children.Add(label);

            var at = index;
            var row = Ui.ListRow(content, index == _paletteCursor, () => RunPaletteItem(item));
            row.PointerEntered += (_, _) =>
            {
                if (_paletteCursor != at)
                {
                    _paletteCursor = at;
                    RefreshPalette();
                }
            };

            _paletteList.Children.Add(row);
        }
    }

    private void RunPaletteItem(PaletteItem item)
    {
        ClosePanel();
        item.Run();
    }

    // ── The sheet ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every key that works on the screen that is open, grouped. Read from the registry rather
    /// than a fixed table, so a key a page does not register is not promised here.
    /// </summary>
    private Border BuildSheet()
    {
        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 28,
            RowSpacing = 16,
        };

        var groups = _registry.Listed
            .GroupBy(s => s.Group)
            .OrderBy(g => g.Key)
            .ToList();

        for (var index = 0; index < groups.Count; index++)
        {
            var section = new StackPanel { Spacing = 4 };
            section.Children.Add(Ui.Text(GroupName(groups[index].Key), Ui.T.Density.TextSmall, Ui.T.TextBrush, FontWeight.Medium));

            foreach (var shortcut in groups[index])
            {
                var line = new DockPanel { LastChildFill = true, Height = 24 };
                var cap = Ui.Kbd(shortcut.Keys);
                DockPanel.SetDock(cap, Avalonia.Controls.Dock.Right);
                line.Children.Add(cap);

                var label = Ui.Dim(shortcut.Label);
                label.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(label);
                section.Children.Add(line);
            }

            Grid.SetColumn(section, index % 2);
            Grid.SetRow(section, index / 2);
            while (columns.RowDefinitions.Count <= index / 2)
                columns.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            columns.Children.Add(section);
        }

        var head = new DockPanel { LastChildFill = true };
        var esc = Ui.Kbd("escape");
        DockPanel.SetDock(esc, Avalonia.Controls.Dock.Right);
        head.Children.Add(esc);
        head.Children.Add(Ui.Text("Keyboard shortcuts", Ui.T.Density.TextBase, Ui.T.TextBrush, FontWeight.SemiBold, wrap: false));

        var body = new StackPanel
        {
            Children =
            {
                new Border { Padding = new Thickness(16, 12), Child = head },
                Ui.Hairline(),
                new Border { Padding = new Thickness(16, 14), Child = columns },
            },
        };

        return Ui.Sheet(body, 560);
    }

    private static string GroupName(ShortcutGroup group) => group switch
    {
        ShortcutGroup.GoTo => "Go to",
        ShortcutGroup.Lists => "Lists",
        ShortcutGroup.Filters => "Filters",
        _ => "General",
    };
}
