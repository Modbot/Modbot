using Avalonia.Controls;
using Avalonia.Layout;

namespace Modbot.Companion.App;

/// <summary>
/// The Log page's Crash details card: one button that copies what a report to VRChat about a
/// crasher needs.
/// </summary>
/// <remarks>
/// <para><strong>The one file that touches the clipboard, and it only writes.</strong> Pressing
/// <strong>Copy crash details</strong> puts one block of text on the clipboard: the instance, who
/// was in it and the avatar each was last seen wearing, and VRChat's own <c>[Behaviour]</c> log
/// lines from the last ten minutes (<c>CrashDetails</c>, in the engine). Nothing is ever read from
/// the clipboard — not here, not anywhere in the client — and <c>CompanionSourceGuardTests</c>
/// fails the build if this file learns to, or if a second file names the clipboard at all.</para>
/// <para><strong>Nothing is sent.</strong> The text is made on this PC from VRChat's log and the
/// companion's own memory of the instance, and goes onto this PC's clipboard. Where it goes after
/// that is wherever the moderator pastes it — a report to VRChat is what it is for.</para>
/// <para>Kept between renders, like the Clips card's controls, so "Copied" survives the window's
/// once-a-second redraw.</para>
/// </remarks>
public sealed partial class MainWindow
{
    private readonly Button _crashCopy = Ui.Button("Copy crash details");
    private readonly TextBlock _crashLine = Ui.Faint("");
    private bool _crashWired;

    /// <summary>The card's body: the button, and what the last press did.</summary>
    private Control CrashDetailsCard()
    {
        if (!_crashWired)
        {
            _crashWired = true;
            _crashCopy.Click += async (_, _) => await CrashGuard.RunAsync("copying crash details", CopyCrashDetailsAsync);
        }

        DetachFromParent(_crashCopy);
        DetachFromParent(_crashLine);
        _crashLine.VerticalAlignment = VerticalAlignment.Center;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { _crashCopy, _crashLine },
        };
    }

    private async Task CopyCrashDetailsAsync()
    {
        var text = _actions.CrashDetails();
        if (text is null)
        {
            _crashLine.Text = "Nothing to copy yet";
            return;
        }

        if (GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            _crashLine.Text = "Could not reach the clipboard";
            return;
        }

        await clipboard.SetTextAsync(text);
        _crashLine.Text = "Copied";
    }
}
