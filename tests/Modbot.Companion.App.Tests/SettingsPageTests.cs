using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Modbot.Companion.Clips;
using Modbot.Companion.Listening;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.App.Tests;

/// <summary>
/// Opening the Settings page for the first time after the companion starts: every card is built,
/// and nothing is written to the settings file that nobody changed.
/// </summary>
/// <remarks>
/// <para>Seen on 2026.9.3-preview.1 (2026-09-25 and 09-26). The first time a slider is shown,
/// Avalonia pulls its value up to its minimum. The window heard that as a person moving it, wrote
/// the minimum into the settings — the desktop overlay's opacity, how long a notification stays,
/// and the Clips card as a whole, switch off included — and each write drew the page again from
/// inside the build that was still putting the page together. The build underneath then found the
/// log folder box already in the other build's card and stopped after Listening.</para>
/// <para>Every write goes back into the window the way the client does it, drawn at once, so a
/// write made during a build meets the window in the same state it met it in the real client.</para>
/// </remarks>
public sealed class SettingsPageTests
{
    private static readonly string[] SettingsCards =
    [
        "Settings",
        "Desktop overlay",
        "Notification overlay",
        "Notifications",
        "Tell me about",
        "Voice",
        "Clips",
        "Modbot Cloud",
        "Listening",
        "VRChat log folder",
        "Restart",
    ];

    /// <summary>A window with saved settings no slider's minimum matches, and a record of every write.</summary>
    private sealed class Harness
    {
        public MainWindow Window { get; } = new();

        public List<string> Writes { get; } = [];

        public CompanionAppSnapshot Snapshot { get; private set; } = CompanionAppSnapshot.Empty with
        {
            Clips = ClipsStatus.None with { Settings = new ClipSettings(On: true, Minutes: 4) },
            DesktopOverlay = DesktopOverlayStatus.None with
            {
                Settings = DesktopOverlaySettings.Default with { On = true, Opacity = 80 },
            },
            DesktopNotifyOverlay = DesktopNotifySettings.Default with { Seconds = 9 },
            Listening = ListeningStatus.None with { Settings = new ListeningSettings(On: true) },
            LogFolder = "C:\\VRChat",
        };

        public MainWindowActions Actions { get; }

        public Harness()
        {
            Actions = MainWindowActions.None with
            {
                SetClips = s => Wrote("clips", n => n with { Clips = n.ClipsOrNone with { Settings = s } }),
                SetDesktopOverlay = s => Wrote("desktop overlay", n => n with { DesktopOverlay = n.DesktopOverlayOrNone with { Settings = s } }),
                SetDesktopNotifyOverlay = s => Wrote("notification overlay", n => n with { DesktopNotifyOverlay = s }),
                SetListening = s => Wrote("listening", n => n with { Listening = n.ListeningOrNone with { Settings = s } }),
                SetNotifications = s => Wrote("notifications", n => n with { Notifications = s }),
                SetNotificationFilters = s => Wrote("notification filters", n => n with { NotificationFilters = s }),
                SetVoice = s => Wrote("voice", n => n with { Voice = n.VoiceOrNone with { Settings = s } }),
                SetStartWithWindows = _ => Wrote("start with Windows", n => n),
                SetLogFolder = _ => Wrote("log folder", n => n),
                SetOverlayOn = _ => Wrote("overlay on", n => n),
                SetNotifyOverlay = _ => Wrote("notify overlay", n => n),
                PlaceOverlay = _ => Wrote("overlay placement", n => n),
            };
        }

        /// <summary>What the client does with a write: keep it, and draw the window again at once.</summary>
        private void Wrote(string what, Func<CompanionAppSnapshot, CompanionAppSnapshot> change)
        {
            Writes.Add(what);
            Snapshot = change(Snapshot);
            Window.Render(Snapshot, Actions);
        }

        /// <summary>Shows the window on the Servers page, as the client opens it.</summary>
        public void Open()
        {
            Window.Show();
            Window.Render(Snapshot, Actions);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Presses a sidebar row, the way a person gets to a page.</summary>
        public void Press(string sidebarLabel)
        {
            var row = Window.GetLogicalDescendants()
                .OfType<Button>()
                .First(button => button.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == sidebarLabel));

            row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>A timer tick: the same snapshot drawn again.</summary>
        public void Tick()
        {
            Window.Render(Snapshot, Actions);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>The card headed <paramref name="title"/> on the open page.</summary>
        public Border Card(string title)
            => Window.GetLogicalDescendants()
                .OfType<Border>()
                .First(border => border.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text == title);

        public IReadOnlyList<string> Headings()
            => [.. Window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];
    }

    [Fact]
    public void OpeningSettingsTheFirstTimeWritesNothing()
    {
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness();
            harness.Open();

            harness.Press("Settings");
            harness.Tick();

            Assert.Empty(harness.Writes);
        });
    }

    [Fact]
    public void OpeningSettingsTheFirstTimeBuildsEveryCard()
    {
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness();
            harness.Open();

            harness.Press("Settings");

            var headings = harness.Headings();
            foreach (var card in SettingsCards)
                Assert.Contains(card, headings);

            // The box the half-built page was missing, in the card that holds it.
            Assert.Single(harness.Card("VRChat log folder").GetLogicalDescendants().OfType<TextBox>());
        });
    }

    [Fact]
    public void OpeningSettingsTheFirstTimeShowsWhatIsSaved()
    {
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness();
            harness.Open();

            harness.Press("Settings");

            Assert.Equal(80, Slider(harness.Card("Desktop overlay")).Value);
            Assert.Equal(9, Slider(harness.Card("Notification overlay")).Value);
            Assert.Equal(4, Slider(harness.Card("Clips")).Value);
            Assert.True(harness.Card("Clips").GetLogicalDescendants().OfType<CheckBox>().First().IsChecked);
        });
    }

    [Fact]
    public void ASliderMovedByHandIsStillSaved()
    {
        // Quiet while the page is drawn must not mean quiet afterwards.
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness();
            harness.Open();
            harness.Press("Settings");

            Slider(harness.Card("Clips")).Value = 3;

            Assert.Equal(["clips"], harness.Writes);
            Assert.Equal(3, harness.Snapshot.ClipsOrNone.Settings.Minutes);
            Assert.True(harness.Snapshot.ClipsOrNone.Settings.On);
        });
    }

    [Theory]
    [InlineData("Servers")]
    [InlineData("Audit Log")]
    [InlineData("SteamVR")]
    [InlineData("Log")]
    [InlineData("Settings")]
    [InlineData("Cloud Server")]
    [InlineData("Credits")]
    public void EveryPageOpensTheFirstTimeWithoutWritingAnything(string page)
    {
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness();
            harness.Open();

            harness.Press(page);
            harness.Tick();

            Assert.Empty(harness.Writes);
        });
    }

    private static Slider Slider(Control card) => card.GetLogicalDescendants().OfType<Slider>().First();
}
