using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Modbot.Companion.App;
using Modbot.Companion.Clips;
using Modbot.Companion.Credits;
using Modbot.Companion.Ingest;
using Modbot.Companion.Journal;
using Modbot.Companion.Listening;
using Modbot.Companion.Pipeline;
using Modbot.Companion.Presentation;
using Modbot.Companion.Startup;

namespace Modbot.UiBoard;

internal static partial class Surfaces
{
    /// <summary>How tall the main window is made for a Settings card, so the whole page is laid out and every card can be cut out.</summary>
    private const double SettingsWindowHeight = 3000;

    /// <summary>How tall the main window is made for the SteamVR page, which scrolls at the usual height.</summary>
    private const double SteamVrWindowHeight = 2300;

    /// <summary>
    /// Adds what draws the companion app's tiles of <see cref="Names"/>: the real main window, shown
    /// offscreen on a made-up snapshot with every action doing nothing, and photographed. A page is
    /// the window as it opens (900 by 660); a Settings card is cut out of the whole Settings page.
    /// </summary>
    private static void AddCompanion(Dictionary<string, Func<RenderTargetBitmap>> draws)
    {
        draws["companion-servers"] = () => Drawing.OfWindow(MainWindowOn(null));
        draws["companion-add-server"] = () => Drawing.OfWindow(MainWindowOn(null, "Add a server"));
        draws["companion-audit-log"] = () => Drawing.OfWindow(MainWindowOn("Audit Log"));
        // The SteamVR page scrolls, so its window is made tall enough to show the whole page.
        draws["companion-steamvr"] = () => Drawing.OfWindow(MainWindowOn("SteamVR", height: SteamVrWindowHeight));
        draws["companion-log"] = () => Drawing.OfWindow(MainWindowOn("Log"));
        draws["companion-credits"] = () => Drawing.OfWindow(MainWindowOn("Credits"));

        foreach (var card in Names.SettingsCards)
        {
            var title = card.Title;
            draws[card.Id] = () => Drawing.OfWindowPart(MainWindowOn("Settings", height: SettingsWindowHeight), window => CardArea(window, title));
        }
    }

    /// <summary>The main window, up on the Servers page, then taken to a page by pressing the sidebar row and then a button on it, as a person would.</summary>
    private static MainWindow MainWindowOn(string? sidebarRow, string? button = null, double? height = null)
    {
        var window = new MainWindow();

        if (height is { } tall)
            window.Height = tall;

        window.Show();
        window.Render(MadeUpSnapshot(), MainWindowActions.None);
        Dispatcher.UIThread.RunJobs();

        if (sidebarRow is not null)
            Press(window, sidebarRow);

        if (button is not null)
            Press(window, button);

        return window;
    }

    /// <summary>Presses the button that says <paramref name="label"/>, the way a click does.</summary>
    private static void Press(MainWindow window, string label)
    {
        var button = window.GetLogicalDescendants()
            .OfType<Button>()
            .First(b => b.Content as string == label || b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == label));

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Where the Settings card headed <paramref name="title"/> is in the window.</summary>
    private static Rect? CardArea(Window shown, string title)
    {
        var window = (MainWindow)shown;
        var card = window.GetLogicalDescendants()
            .OfType<Border>()
            .First(border => border.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text == title);

        var origin = card.TranslatePoint(new Point(0, 0), window) ?? throw new InvalidOperationException($"The card '{title}' is not on the page.");
        return new Rect(origin, card.Bounds.Size);
    }

    /// <summary>What the window shows: three paired servers in three states, a few events, the overlay up, the switches on.</summary>
    private static CompanionAppSnapshot MadeUpSnapshot() => CompanionAppSnapshot.Empty with
    {
        Servers =
        [
            new ServerRow("srv_sample_1", "modbot.example.invalid", "grp_sample_catlounge", Samples.Group, null, ConnectionState.Healthy, false, 0, 1284, 37, "Reporting normally.", "Live"),
            new ServerRow("srv_sample_2", "modbot.example.invalid", "grp_sample_bookclub", "Book Club", null, ConnectionState.Waiting, false, 12, 410, 3, "The server did not answer. Trying again in a minute."),
            new ServerRow("srv_sample_3", "modbot.example.invalid", "grp_sample_games", "Game Night", null, ConnectionState.Paused, true, 0, 96, 0, "Paused. Nothing is captured or sent."),
        ],
        Events =
        [
            Event(2, "Aldric Sample joined the instance", Samples.Group, JournalEntryKind.Sent),
            Event(5, "Elowen Sample was warned by Moderator Sample", Samples.Group, JournalEntryKind.Sent),
            Event(9, "Brindle Sample left the instance", "Book Club", JournalEntryKind.Waiting),
            Event(14, "Cosima Sample was kicked by Moderator Sample", Samples.Group, JournalEntryKind.Sent),
            Event(20, "Garrick Sample joined the instance", "Game Night", JournalEntryKind.Withheld),
        ],
        LogStatus = LogHealthStatus.Healthy,
        LogDetail = "Reading VRChat's log. Lines are arriving and Modbot understands them.",
        LinesRead = 48213,
        BehaviourLines = 9120,
        RecognisedEvents = 1731,
        Warnings = [new CompanionWarning(WarningSeverity.Warning, "Book Club has 12 events waiting to be sent.")],
        Startup = new StartupState(true, false, false),
        LogFolder = "C:\\Users\\Sample\\AppData\\LocalLow\\VRChat\\VRChat",
        Overlay = new OverlayStatus(
            true, "attached", "The panel is up in SteamVR.", Samples.Now.AddMinutes(-20), 4312, Samples.Now.AddSeconds(-1),
            Samples.Group, 8, "2 s ago", null, null, "srv_sample_1"),
        Credits = new CreditsList(
            [new CreditPerson("Sample Sponsor", "", "", null, null, null)],
            [new CreditPerson("Sample Early Group", "", "", "grp_sample_early", null, null)],
            [new CreditContributor("sample-contributor", "https://example.invalid/sample-contributor", "")]),
        Clips = ClipsStatus.None with { Settings = new ClipSettings(On: true, Minutes: 4) },
        DesktopOverlay = DesktopOverlayStatus.None with { Settings = DesktopOverlaySettings.Default with { On = true, Opacity = 80 } },
        DesktopNotifyOverlay = DesktopNotifySettings.Default with { Seconds = 9 },
        Listening = ListeningStatus.None with { Settings = new ListeningSettings(On: true) },
    };

    private static JournalRow Event(int minutesAgo, string summary, string server, JournalEntryKind state)
        => new(Samples.Now.AddMinutes(-minutesAgo), summary, server, state, null);
}
