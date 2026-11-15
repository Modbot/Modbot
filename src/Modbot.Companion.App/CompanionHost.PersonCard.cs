using Avalonia.Threading;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// What the person card's Open in Modbot and Copy name do on this PC, from either panel.
/// </summary>
/// <remarks>
/// The overlay's loop only says that one was pressed (<c>OpenPageAsked</c>, <c>CopyTextAsked</c>);
/// the browser and the clipboard are this half's. Neither changes anything about the person, and
/// nothing is sent to any server: Open in Modbot hands an address to Windows the way clicking a link
/// does, and the address was built from the pairing's own base address and the person's id.
/// </remarks>
internal sealed partial class CompanionHost
{
    /// <summary>Opens a person's page on the paired server in the PC's default browser.</summary>
    private void OpenPersonPage(Uri page)
        => Dispatcher.UIThread.Post(() => _ = CrashGuard.RunAsync("opening a person's page", async () =>
        {
            if (!await Window.Launcher.LaunchUriAsync(page))
                Log.Warning("Could not open a person's page in the browser");
        }));

    /// <summary>Puts text on the clipboard exactly as it was handed over.</summary>
    private void CopyText(string text)
        => Dispatcher.UIThread.Post(() => _ = CrashGuard.RunAsync("copying a name", async () =>
        {
            if (Window.Clipboard is not { } clipboard)
            {
                Log.Warning("Could not reach the clipboard to copy a name");
                return;
            }

            await clipboard.SetTextAsync(text);
        }));
}
