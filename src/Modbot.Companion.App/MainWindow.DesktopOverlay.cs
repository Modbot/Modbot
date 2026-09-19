using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.App;

/// <summary>
/// The Settings page's desktop overlay card: whether it runs, the shortcut that brings it up over
/// VRChat, and how solid its background is.
/// </summary>
/// <remarks>
/// <para>The shortcut is recorded by pressing it. Storing it spelled the way the window's own keys
/// are spelled (<c>mod+alt+m</c>) is right; asking a moderator to type that is not.</para>
/// <para>Registering it can be refused — another program may already hold the combination — and
/// when it is, the line under the control says so. That is an error saying what failed, which is
/// all the text this card gets.</para>
/// </remarks>
public sealed partial class MainWindow
{
    // Built once, like the other settings controls: a switch or a slider rebuilt by the
    // one-second render loses the click or the drag on it.
    private readonly CheckBox _desktopOverlayOn = new();
    private readonly Button _desktopOverlayShortcut = Ui.Button("");

    /// <summary>The button's own caption, so setting it keeps the button's styling.</summary>
    private readonly TextBlock _desktopOverlayShortcutText = Ui.Text(
        "", Ui.T.Density.TextSmall, Ui.T.TextBrush, FontWeight.Medium, wrap: false);
    private readonly Slider _desktopOverlayOpacity = new()
    {
        Minimum = DesktopOverlaySettings.MinimumOpacity,
        Maximum = DesktopOverlaySettings.MaximumOpacity,
        TickFrequency = 1,
        IsSnapToTickEnabled = true,
        Width = 220,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBlock _desktopOverlayOpacityValue = Ui.Text(
        "", Ui.T.Density.TextSmall, Ui.T.TextDimBrush, wrap: false, mono: true);

    private readonly TextBlock _desktopOverlayLine = Ui.Text("", Ui.T.Density.TextTiny, Ui.T.DangerBrush);

    /// <summary>The way in when the shortcut could not be registered.</summary>
    private readonly Button _desktopOverlayOpen = Ui.Button("Open");

    /// <summary>The card's controls answer back only once they have been wired, which is once.</summary>
    private bool _desktopOverlayWired;

    /// <summary>True while the next key press is the new shortcut rather than a shortcut.</summary>
    private bool _capturingShortcut;

    private void WireDesktopOverlay()
    {
        if (_desktopOverlayWired)
            return;

        _desktopOverlayWired = true;

        _desktopOverlayOn.Content = Ui.Text("Desktop overlay", Ui.T.Density.TextSmall, Ui.T.TextBrush);
        _desktopOverlayShortcut.Content = _desktopOverlayShortcutText;
        _desktopOverlayShortcut.MinWidth = 140;
        _desktopOverlayOpacityValue.VerticalAlignment = VerticalAlignment.Center;
        _desktopOverlayOpacityValue.Width = 32;

        _desktopOverlayOn.IsCheckedChanged += (_, _) => DesktopOverlayChanged(
            s => s with { On = _desktopOverlayOn.IsChecked == true });

        _desktopOverlayOpacity.ValueChanged += (_, _) =>
        {
            _desktopOverlayOpacityValue.Text = $"{(int)_desktopOverlayOpacity.Value}";
            DesktopOverlayChanged(s => s with { Opacity = (int)_desktopOverlayOpacity.Value });
        };

        _desktopOverlayOpen.Click += (_, _) => _actions.ShowDesktopOverlay();

        _desktopOverlayShortcut.Click += (_, _) =>
        {
            _capturingShortcut = !_capturingShortcut;
            RenderPage();
        };
    }

    /// <summary>What the card's controls say right now, handed to the application as one record.</summary>
    private void DesktopOverlayChanged(Func<DesktopOverlaySettings, DesktopOverlaySettings> change)
    {
        if (_renderingSwitches)
            return;

        _actions.SetDesktopOverlay(change(_snapshot.DesktopOverlayOrNone.Settings));
    }

    /// <summary>
    /// One key press while the shortcut button is waiting for it. Escape gives up; anything the
    /// client could actually register is saved and the button stops waiting.
    /// </summary>
    /// <remarks>
    /// The press is spelled by <see cref="KeyTokens.Token"/>, the same way the window's own keys
    /// are, so a key combination has one spelling in this client rather than two.
    /// </remarks>
    private void CaptureShortcut(KeyEventArgs e)
    {
        e.Handled = true;

        if (e.Key is Key.Escape)
        {
            _capturingShortcut = false;
            RenderPage();
            return;
        }

        // A press that is only modifiers, or that nothing could be registered for, leaves the
        // button waiting: the moderator is still holding keys down.
        if (TokenFor(e) is not { } token || !DesktopOverlayKeys.CanBeUsed(token))
            return;

        _capturingShortcut = false;
        DesktopOverlayChanged(s => s with { Shortcut = token });
        RenderPage();
    }

    /// <summary>
    /// What the card says right now, put into its controls. Called from the Settings page's own
    /// refresh, which has already stopped the controls answering back, so this one does not.
    /// </summary>
    private void RefreshDesktopOverlayControls()
    {
        WireDesktopOverlay();

        var desktop = _snapshot.DesktopOverlayOrNone;

        _desktopOverlayOn.IsChecked = desktop.Settings.On;

        if (!_desktopOverlayOpacity.IsPointerOver && !_desktopOverlayOpacity.IsFocused)
            _desktopOverlayOpacity.Value = DesktopOverlaySettings.ClampOpacity(desktop.Settings.Opacity);

        _desktopOverlayOpacityValue.Text = $"{(int)_desktopOverlayOpacity.Value}";
        _desktopOverlayShortcutText.Text = _capturingShortcut
            ? "Press the keys"
            : KeyTokens.Describe(desktop.Settings.ShortcutOrDefault);

        _desktopOverlayLine.Text = desktop.Problem ?? "";
        _desktopOverlayLine.IsVisible = desktop.Problem is not null;

        _desktopOverlayOpacity.IsEnabled = desktop.Settings.On;
        _desktopOverlayShortcut.IsEnabled = desktop.Settings.On;
        _desktopOverlayOpen.IsEnabled = desktop.Settings is { On: true } && !desktop.Showing;
    }

    /// <summary>The card: the switch, the shortcut, the opacity, and what failed if anything did.</summary>
    private Control DesktopOverlayCard()
    {
        WireDesktopOverlay();

        foreach (var control in new Control[]
                 {
                     _desktopOverlayOn,
                     _desktopOverlayShortcut,
                     _desktopOverlayOpacity,
                     _desktopOverlayOpacityValue,
                     _desktopOverlayLine,
                     _desktopOverlayOpen,
                 })
        {
            DetachFromParent(control);
        }

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                _desktopOverlayOn,
                Ui.Field("Shortcut", _desktopOverlayShortcut),
                Ui.Field("Opacity", new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _desktopOverlayOpacity, _desktopOverlayOpacityValue },
                }),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { _desktopOverlayOpen, _desktopOverlayLine },
                },
            },
        };
    }
}
