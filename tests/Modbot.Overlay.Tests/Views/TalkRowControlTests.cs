using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Modbot.Companion.Clips;
using Modbot.Companion.Overlay;
using Modbot.Companion.Talk;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The Talk icons beside Clip on the panel: where they are, when they are there at all, what each
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
            Assert.True(placed.Bounds.Width >= 56, $"Too narrow to hit in VR: {placed.Bounds.Width}");
            Assert.True(placed.Bounds.Height >= 40, $"Too short to hit in VR: {placed.Bounds.Height}");
        });
    }

    private static OverlayScreen WithClip(TalkRow talk) => Screen(talk) with { Clips = new ClipButton(ClipButtonState.Ready, "Clip", CanPress: true) };

    [Fact]
    public void TheIconsSitOnTheSameRowAsClip()
    {
        var targets = Targets(WithClip(Showing));

        var save = Assert.Single(targets, t => t.Target is OverlayTarget.SaveClip);
        var icons = targets.Where(t => t.Target is OverlayTarget.Talk).ToList();

        Assert.Equal(3, icons.Count);
        Assert.All(icons, icon =>
        {
            Assert.True(save.Bounds.Right <= icon.Bounds.Left, "An icon is not to the right of Clip.");
            Assert.True(icon.Bounds.Center.Y > save.Bounds.Top && icon.Bounds.Center.Y < save.Bounds.Bottom, "An icon is not on Clip's row.");
        });
    }

    [Fact]
    public void ClipIsOnlyAsWideAsItsIconAndWord()
    {
        var save = Assert.Single(Targets(WithClip(Showing)), t => t.Target is OverlayTarget.SaveClip);

        // The icon, the word and the padding: not the room the audio icons leave.
        Assert.True(save.Bounds.Width < 160, $"Clip is stretched: {save.Bounds.Width}");
        Assert.True(save.Bounds.Height >= 40, $"Too short to hit in VR: {save.Bounds.Height}");
    }

    [Fact]
    public void ClipAndTheAudioIconsAreTwoGroupsWithAGapBetween()
    {
        var targets = Targets(WithClip(Showing));
        var save = Assert.Single(targets, t => t.Target is OverlayTarget.SaveClip);
        var icons = targets.Where(t => t.Target is OverlayTarget.Talk).OrderBy(t => t.Bounds.Left).ToList();

        Assert.True(icons[0].Bounds.Left - save.Bounds.Right >= 24, "No clear gap between Clip and the audio icons.");

        // The audio icons are close together, much closer than they are to Clip.
        Assert.True(icons[1].Bounds.Left - icons[0].Bounds.Right <= 12);
        Assert.True(icons[2].Bounds.Left - icons[1].Bounds.Right <= 12);
    }

    [Fact]
    public void OnVRChatsSideTheMuteIsVRChatsAndTheDeafenIsGoneButKeepsItsPlace()
    {
        var discord = Buttons(WithClip(Showing));
        var vrchat = Buttons(WithClip(Showing with { Side = TalkSide.VRChat }));

        Assert.Equal([TalkButton.Swap, TalkButton.VRChatMute], vrchat.Select(b => ((OverlayTarget.Talk)b.Target).Button));

        // Nothing moves: the swap and the mute are where they were, and the Deafen's place is left empty.
        Assert.Equal(discord[0].Bounds, vrchat[0].Bounds);
        Assert.Equal(discord[1].Bounds, vrchat[1].Bounds);

        var (hidden, deafenSpace) = AvaloniaTestHost.Run(() =>
        {
            var panel = LaidOut(WithClip(Showing with { Side = TalkSide.VRChat })).GetVisualDescendants().OfType<StackPanel>()
                .Single(p => p.Children.OfType<Border>().Count(b => b.Tag is OverlayTarget.Talk) == 2);
            var last = panel.Children.OfType<Border>().Last();
            // Both are measured inside the same row, so the comparison is between neighbours.
            var mute = panel.Children.OfType<Border>().ElementAt(1);
            return (last.Opacity == 0 && last.Tag is null, last.Bounds.Left >= mute.Bounds.Right);
        });

        Assert.True(hidden);
        Assert.True(deafenSpace);
    }

    [Fact]
    public void OnVRChatsSideTheMuteSaysItIsVRChatsAndIsStruckWhileVRChatIsMuted()
    {
        string? Name(bool muted) => AvaloniaTestHost.Run(() => AutomationProperties.GetName(
            LaidOut(Screen(new TalkRow(true, TalkSide.VRChat, false, false, null, VRChatMuted: muted)))
                .GetVisualDescendants().OfType<Border>().Single(b => b.Tag is OverlayTarget.Talk { Button: TalkButton.VRChatMute })));

        Assert.Equal("VRChat Mute", Name(false));
        Assert.Equal("VRChat Muted", Name(true));
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

    [Theory]
    [InlineData(TalkSide.Discord)]
    [InlineData(TalkSide.VRChat)]
    public void TheSwapButtonShowsALogoAndNoLetter(TalkSide side)
    {
        var (words, filled) = AvaloniaTestHost.Run(() =>
        {
            var swap = LaidOut(Screen(new TalkRow(true, side, false, false, null)))
                .GetVisualDescendants().OfType<Border>().Single(b => b.Tag is OverlayTarget.Talk { Button: TalkButton.Swap });
            return (
                swap.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList(),
                swap.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(p => p.Fill is not null));
        });

        Assert.Empty(words);

        // Always a solid mark, never lines.
        Assert.True(filled);
    }

    [Fact]
    public void TheDiscordAndVRChatLogosAreDifferentMarks()
    {
        var shapes = new[] { TalkSide.Discord, TalkSide.VRChat }.Select(side => AvaloniaTestHost.Run(() =>
            LaidOut(Screen(new TalkRow(true, side, false, false, null)))
                .GetVisualDescendants().OfType<Border>().Single(b => b.Tag is OverlayTarget.Talk { Button: TalkButton.Swap })
                .GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single().Data!.Bounds)).ToList();

        Assert.NotEqual(shapes[0], shapes[1]);
    }

    [Fact]
    public void ItSaysWhereThingsStand()
    {
        // The icons carry the words they used to say as their automation names.
        var names = AvaloniaTestHost.Run(() => LaidOut(Screen(new TalkRow(true, TalkSide.Discord, true, true, null)))
            .GetVisualDescendants().OfType<Border>().Where(b => b.Tag is OverlayTarget.Talk)
            .Select(b => AutomationProperties.GetName(b)).ToList());

        Assert.Contains("Talking in Discord", names);
        Assert.Contains("Discord Muted", names);
        Assert.Contains("Discord Deafened", names);
    }

    [Fact]
    public void AFailureIsTheOneLineUnderTheButtons()
    {
        var texts = AvaloniaTestHost.Run(() => LaidOut(Screen(new TalkRow(true, TalkSide.Discord, false, false, "VRChat didn't answer.")))
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
