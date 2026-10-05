using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Ingest;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.App.Tests;

/// <summary>
/// The Settings page's Modbot Cloud box and the Cloud Server page: one setting in two places, a
/// choice of what is sent, and the rule that at least one event stays on.
/// </summary>
/// <remarks>
/// Every write goes back into the window the way the client does it, drawn at once, so a write made
/// by a click meets the window in the same state it meets it in the real client.
/// </remarks>
public sealed class CloudPageTests
{
    private const string Warning = "At least one event must be on for Modbot Cloud to work.";

    private sealed class Harness
    {
        public MainWindow Window { get; } = new();

        public List<string> Writes { get; } = [];

        public CompanionAppSnapshot Snapshot { get; private set; } = CompanionAppSnapshot.Empty;

        public MainWindowActions Actions { get; }

        public CloudWaiting Waiting { get; set; } = CloudWaiting.None;

        public Harness()
        {
            Actions = MainWindowActions.None with
            {
                SetCloudOn = on => Wrote("on", n => n with { Cloud = n.CloudOrDefault with { Disabled = !on } }),
                SetCloudChoices = c => Wrote("choices", n => n with { Cloud = n.CloudOrDefault with { Choices = c } }),
                NextCloudBatch = _ => Waiting,
            };
        }

        public void Use(Func<CompanionAppSnapshot, CompanionAppSnapshot> change) => Snapshot = change(Snapshot);

        private void Wrote(string what, Func<CompanionAppSnapshot, CompanionAppSnapshot> change)
        {
            Writes.Add(what);
            Snapshot = change(Snapshot);
            Window.Render(Snapshot, Actions);
        }

        public void Open()
        {
            Window.Show();
            Window.Render(Snapshot, Actions);
            Dispatcher.UIThread.RunJobs();
        }

        public void Press(string sidebarLabel)
        {
            var row = Window.GetLogicalDescendants()
                .OfType<Button>()
                .First(button => button.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == sidebarLabel));

            row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        public CheckBox Box(string name)
            => Window.GetLogicalDescendants().OfType<CheckBox>().First(box => box.Name == name);

        public IReadOnlyList<string> Words()
            => [.. Window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];

        /// <summary>Whether the text is in a control that is actually showing.</summary>
        public bool Shows(string text)
            => Window.GetLogicalDescendants().OfType<TextBlock>()
                .Where(t => t.Text == text)
                .Any(t => t.GetLogicalAncestors().OfType<Control>().All(c => c.IsVisible));
    }

    private static CloudChoices OnlyJoinedInGroups => new(new CloudSection(CloudEventKinds.Joined), new CloudSection(CloudEventKinds.None));

    [Fact]
    public void TheCloudServerPageNamesEveryControlAndNothingElse()
    {
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness();
            harness.Open();
            harness.Press("Cloud Server");

            var words = harness.Words();
            foreach (var expected in new[]
                     {
                         "Modbot Cloud", "Instance Data", "Group instances", "Non-group instances", "Always sent",
                         "Joined", "Already here", "Left", "Avatar changed", "Modbot stopped logging",
                         "World ID", "Instance ID", "Group", "Avatar name",
                         "Display name", "User ID", "App version", "Unique ID", "Event Type", "Time / Offset to Cloud",
                         "5 of 5 events", "Next batch", "Nothing is sent.",
                     })
            {
                Assert.Contains(expected, words);
            }

            Assert.Empty(harness.Writes);
        });
    }

    [Fact]
    public void TheBoxOnSettingsAndTheOneOnTheCloudServerPageAreOneSetting()
    {
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness();
            harness.Open();

            harness.Press("Settings");
            Assert.True(harness.Box("cloud-on-settings").IsChecked);

            harness.Box("cloud-on-settings").IsChecked = false;

            Assert.Equal(["on"], harness.Writes);
            Assert.True(harness.Snapshot.CloudOrDefault.Disabled);

            harness.Press("Cloud Server");
            Assert.False(harness.Box("cloud-on-page").IsChecked);

            harness.Box("cloud-on-page").IsChecked = true;

            Assert.Equal(["on", "on"], harness.Writes);
            Assert.False(harness.Snapshot.CloudOrDefault.Disabled);

            harness.Press("Settings");
            Assert.True(harness.Box("cloud-on-settings").IsChecked);
        });
    }

    [Fact]
    public void SwitchingItOffGreysEveryChoiceKeepsItsTicksAndUnticksWhatIsRequired()
    {
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness();
            harness.Open();
            harness.Press("Cloud Server");

            Assert.True(harness.Box("cloud-group-section").IsChecked);
            Assert.True(harness.Box("cloud-always-0").IsChecked);

            harness.Box("cloud-on-page").IsChecked = false;

            foreach (var name in new[] { "cloud-group-Joined", "cloud-other-Left", "cloud-group-WorldId", "cloud-other-AvatarName" })
            {
                Assert.False(harness.Box(name).IsEnabled);
                Assert.True(harness.Box(name).IsChecked);
            }

            Assert.False(harness.Box("cloud-group-section").IsChecked);
            Assert.False(harness.Box("cloud-other-section").IsChecked);
            Assert.All(Enumerable.Range(0, 6), i => Assert.False(harness.Box($"cloud-always-{i}").IsChecked));

            // Every section is folded, and the one below Instance Data can still be opened to read.
            Assert.False(harness.Shows("Joined"));
            Assert.False(harness.Shows("Display name"));
        });
    }

    [Fact]
    public void AnEventBoxWritesTheWholeChoices()
    {
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness();
            harness.Open();
            harness.Press("Cloud Server");

            harness.Box("cloud-other-Left").IsChecked = false;

            Assert.Equal(["choices"], harness.Writes);
            Assert.False(harness.Snapshot.CloudOrDefault.Choices.NonGroup.Sends(CloudEventKinds.Left));
            Assert.True(harness.Snapshot.CloudOrDefault.Choices.Group.Sends(CloudEventKinds.Left));
            Assert.Contains("4 of 5 events", harness.Words());
        });
    }

    [Fact]
    public void TheLastEventThatIsOnStaysTickedAndTheWarningShowsUntilAnythingElseChanges()
    {
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness();
            harness.Use(n => n with { Cloud = n.CloudOrDefault with { Choices = OnlyJoinedInGroups } });
            harness.Open();
            harness.Press("Cloud Server");

            Assert.False(harness.Shows(Warning));

            harness.Box("cloud-group-Joined").IsChecked = false;

            Assert.Empty(harness.Writes);
            Assert.True(harness.Box("cloud-group-Joined").IsChecked);
            Assert.True(harness.Shows(Warning));

            // Any other change clears it.
            harness.Box("cloud-group-WorldId").IsChecked = false;

            Assert.Equal(["choices"], harness.Writes);
            Assert.False(harness.Shows(Warning));
        });
    }

    [Fact]
    public void ASectionWithNoEventsReportsNothingOnAndItsOwnBoxCannotBeTicked()
    {
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness();
            harness.Use(n => n with { Cloud = n.CloudOrDefault with { Choices = OnlyJoinedInGroups } });
            harness.Open();
            harness.Press("Cloud Server");

            Assert.True(harness.Box("cloud-group-section").IsChecked);
            Assert.False(harness.Box("cloud-group-section").IsEnabled);
            Assert.False(harness.Box("cloud-other-section").IsChecked);
            Assert.Contains("1 of 5 events", harness.Words());
            Assert.Contains("0 of 5 events", harness.Words());
        });
    }

    [Fact]
    public void ABoxLockedByTheEnvironmentShowsTheAnswerAndCannotBeChanged()
    {
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness();
            harness.Use(n => n with { Cloud = n.CloudOrDefault with { Disabled = true, SwitchLocked = true } });
            harness.Open();

            harness.Press("Settings");
            Assert.False(harness.Box("cloud-on-settings").IsChecked);
            Assert.False(harness.Box("cloud-on-settings").IsEnabled);

            harness.Press("Cloud Server");
            Assert.False(harness.Box("cloud-on-page").IsChecked);
            Assert.False(harness.Box("cloud-on-page").IsEnabled);
        });
    }

    [Fact]
    public void TheNextBatchDrawsWhatIsWaitingWithWhatEachWillCarry()
    {
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness
            {
                Waiting = new CloudWaiting(
                    [
                        new CloudQueuedEvent(
                            "0123456789abcdef", CompanionEventType.InstanceJoined,
                            new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero), "usr_x", "Rin",
                            WorldId: null, InstanceId: "39911", GroupId: "grp_cats", AvatarName: null, InGroup: true),
                    ],
                    TimeSpan.FromMilliseconds(120)),
            };

            harness.Open();
            harness.Press("Cloud Server");

            var words = harness.Words();
            Assert.Contains("Group instance", words);
            Assert.Contains("usr_x", words);
            Assert.Contains("39911", words);
            Assert.Contains("grp_cats", words);

            // The world was left out, and the card says so where its value would be.
            Assert.Contains("not sent", words);
            Assert.DoesNotContain("Nothing is sent.", words);
        });
    }

    [Fact]
    public void NothingIsSentIsSaidWhileCloudIsOff()
    {
        AvaloniaTestHost.Run(() =>
        {
            var harness = new Harness
            {
                Waiting = new CloudWaiting(
                    [
                        new CloudQueuedEvent(
                            "0123456789abcdef", CompanionEventType.InstanceJoined,
                            new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero), "usr_x", "Rin",
                            "wrld_1", "1", null, null, InGroup: false),
                    ],
                    TimeSpan.Zero),
            };

            harness.Use(n => n with { Cloud = n.CloudOrDefault with { Disabled = true } });
            harness.Open();
            harness.Press("Cloud Server");

            Assert.Contains("Nothing is sent.", harness.Words());
            Assert.DoesNotContain("usr_x", harness.Words());
        });
    }
}
