using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Talk;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The row of Talk buttons on the panel: where it is, when it is there at all, what each button
/// is called, and that a hand in a headset can hit each one.
/// </summary>
public class TalkRowControlTests
{
    private const int Width = 480;
    private const int Height = 640;

    private static InstanceContext Roster() => new(
        "39911",
        [.. Enumerable.Range(0, 3).Select(i => new RosterMember($"usr_{i}", $"Person {i}", RosterStanding.Member, 0, []))]);

    private static OverlayScreen Screen(TalkRow talk = default, OverlayPage page = OverlayPage.Instance) => new(
        "Cat Lounge",
        new Cached<InstanceContext>(Roster(), Freshness.Fresh, TimeSpan.Zero),
        Freshness.Fresh,
        Page: page,
        Talk: talk);

    private static TalkRow Showing => new(true, TalkSide.Discord, false, false, null);

    private static Control LaidOut(OverlayScreen screen)
    {
        var root = OverlayView.Build(screen);
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        return root;
    }

    private static IReadOnlyList<PlacedTarget> Targets(OverlayScreen screen)
        => AvaloniaTestHost.Run(() => OverlayTargets.Find(LaidOut(screen)));

    private static IReadOnlyList<PlacedTarget> Buttons(OverlayScreen screen)
        => [.. Targets(screen).Where(t => t.Target is OverlayTarget.Talk)];

    [Fact]
    public void WithTalkOffThereIsNoRowOnThePanel()
    {
        Assert.Empty(Buttons(Screen()));
    }

    [Theory]
    [InlineData(OverlayPage.Instance)]
    [InlineData(OverlayPage.Events)]
    public void TheThreeButtonsAreOnEveryScreenRatherThanBehindAPage(OverlayPage page)
    {
        var buttons = Buttons(Screen(Showing, page)).Select(b => ((OverlayTarget.Talk)b.Target).Button).ToList();

        Assert.Equal([TalkButton.Swap, TalkButton.DiscordMute, TalkButton.DiscordDeafen], buttons);
    }

    [Fact]
    public void TheButtonsSitSideBySideInOrderAndNearTheTop()
    {
        var buttons = Buttons(Screen(Showing));

        Assert.True(buttons[0].Bounds.Right <= buttons[1].Bounds.Left);
        Assert.True(buttons[1].Bounds.Right <= buttons[2].Bounds.Left);
        Assert.All(buttons, b => Assert.True(b.Bounds.Top < Height / 3.0, $"Too far down the panel: {b.Bounds.Top}"));
    }

    [Fact]
    public void EachButtonIsBigEnoughForAHandInAHeadset()
    {
        Assert.All(Buttons(Screen(Showing)), placed =>
        {
            Assert.True(placed.Bounds.Width >= 100, $"Too narrow to hit in VR: {placed.Bounds.Width}");
            Assert.True(placed.Bounds.Height >= 40, $"Too short to hit in VR: {placed.Bounds.Height}");
        });
    }

    [Fact]
    public void ARowThatIsOnlyTheGapsBetweenTheButtonsStillCountsAsThePanel()
    {
        // The cursor stays on the panel between the buttons, as it does between the tabs.
        var buttons = Buttons(Screen(Showing));
        var gap = new Point((buttons[0].Bounds.Right + buttons[1].Bounds.Left) / 2, buttons[0].Bounds.Center.Y);

        var drawn = AvaloniaTestHost.Run(() => OverlayTargets.Drawn(LaidOut(Screen(Showing)), gap));

        Assert.True(drawn);
    }

    [Fact]
    public void ItSaysWhereThingsStand()
    {
        var texts = AvaloniaTestHost.Run(() => LaidOut(Screen(new TalkRow(true, TalkSide.VRChat, true, true, null)))
            .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList());

        Assert.Contains("Talking in VRChat", texts);
        Assert.Contains("Discord Muted", texts);
        Assert.Contains("Discord Deafened", texts);
    }

    [Fact]
    public void AFailureIsTheOneLineUnderTheButtons()
    {
        var texts = AvaloniaTestHost.Run(() => LaidOut(Screen(new TalkRow(true, TalkSide.Neither, false, false, "VRChat didn't answer.")))
            .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList());

        Assert.Contains("VRChat didn't answer.", texts);
    }

    [Fact]
    public void OutsideAGroupInstanceTheRowIsStillThere()
    {
        // The microphone is swapped in a public instance as often as in a group one.
        var idle = OverlayScreen.Idle with { Talk = Showing };
        var targets = Targets(idle);

        Assert.Equal(3, targets.Count(t => t.Target is OverlayTarget.Talk));
        Assert.DoesNotContain(targets, t => t.Target is OverlayTarget.Roster);
        Assert.DoesNotContain(targets, t => t.Target is OverlayTarget.GoTo);
    }

    [Fact]
    public void ARedrawIsNeededWhenOnlyTheRowChanges()
    {
        var before = Screen(Showing);
        var after = before with { Talk = Showing with { DiscordMuted = true } };

        Assert.False(before.LooksTheSameAs(after));
        Assert.True(before.LooksTheSameAs(before with { }));
    }
}
