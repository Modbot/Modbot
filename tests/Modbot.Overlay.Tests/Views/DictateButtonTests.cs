using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The Dictate button at the top of the headset panel: there while the card offers Dictate, gone
/// while it does not, in both looks and on the panel outside a group's instance.
/// </summary>
public class DictateButtonTests
{
    private const int Width = 480;
    private const int Height = 640;

    private static OverlayLook VRChatLook() => OverlayLook.FromColours(new OverlayColours(
        Panel: new PaletteColour(0x18, 0x12, 0x24),
        Bar: new PaletteColour(0x10, 0x0C, 0x1A),
        Button: new PaletteColour(0x2A, 0x20, 0x44),
        Border: new PaletteColour(0x38, 0x2C, 0x5A),
        Hover: new PaletteColour(0x30, 0x26, 0x4E),
        Selected: new PaletteColour(0x50, 0x3A, 0x90),
        SelectedBorder: new PaletteColour(0x80, 0x60, 0xE0),
        SelectedText: new PaletteColour(0xF5, 0xF5, 0xF5),
        Icon: new PaletteColour(0xA0, 0x90, 0xD0),
        Text: new PaletteColour(0xF0, 0xF0, 0xF8),
        Subtext: new PaletteColour(0xB0, 0xA8, 0xC8)));

    private static InstanceContext Roster() => new(
        "39911",
        [.. Enumerable.Range(0, 3).Select(i => new RosterMember($"usr_{i}", $"Person {i}", RosterStanding.Member, 0, []))]);

    private static OverlayScreen Screen(ButtonScreen? dictate) => new(
        "Cat Lounge",
        new Cached<InstanceContext>(Roster(), Freshness.Fresh, TimeSpan.Zero),
        Freshness.Fresh,
        Dictate: dictate);

    private static ButtonScreen Offered => ButtonScreen.Shown with { Dictate = DictateState.Idle };

    private static IReadOnlyList<PlacedTarget> Targets(Func<Control> build)
        => AvaloniaTestHost.Run(() =>
        {
            var root = build();
            root.Measure(new Size(Width, Height));
            root.Arrange(new Rect(0, 0, Width, Height));
            return OverlayTargets.Find(root);
        });

    [Fact]
    public void WithDictateNotOfferedThePanelHasNoButton()
    {
        var targets = Targets(() => OverlayView.Build(Screen(null)));

        Assert.DoesNotContain(targets, t => t.Target is OverlayTarget.DictateButton);
    }

    [Fact]
    public void WithDictateOfferedThePanelHasOneButton()
    {
        var targets = Targets(() => OverlayView.Build(Screen(Offered)));

        Assert.Single(targets, t => t.Target is OverlayTarget.DictateButton);
    }

    [Fact]
    public void ItIsOnTheVRChatLookToo()
    {
        var offered = Targets(() => OverlayView.BuildForHeadset(Screen(Offered), null, VRChatLook(), Width));
        var notOffered = Targets(() => OverlayView.BuildForHeadset(Screen(null), null, VRChatLook(), Width));

        Assert.Single(offered, t => t.Target is OverlayTarget.DictateButton);
        Assert.DoesNotContain(notOffered, t => t.Target is OverlayTarget.DictateButton);
    }

    [Fact]
    public void TheHeadsetStripHasOneCloseButtonAtItsRightEdgeBesideDictate()
    {
        var targets = Targets(() => OverlayView.BuildForHeadset(Screen(Offered), null, VRChatLook(), 1024));

        var close = Assert.Single(targets, t => t.Target is OverlayTarget.ClosePanel);
        var dictate = Assert.Single(targets, t => t.Target is OverlayTarget.DictateButton);

        Assert.True(close.Bounds.Left >= dictate.Bounds.Right, "Close sits to the right of Dictate");
        Assert.True(close.Bounds.Right <= OverlayView.HeadsetPanelWidth(Screen(Offered), VRChatLook(), 1024) + 0.5, "Close is inside the frame");
        Assert.True(close.Bounds.Top < 100, "Close is in the strip at the top");
    }

    [Fact]
    public void TheCloseButtonIsThereWithDictateNotOfferedAndNotInTheModbotLook()
    {
        var vrchat = Targets(() => OverlayView.BuildForHeadset(Screen(null), null, VRChatLook(), 1024));
        var modbot = Targets(() => OverlayView.BuildForHeadset(Screen(Offered), null, OverlayLook.Headset, 1024));

        Assert.Single(vrchat, t => t.Target is OverlayTarget.ClosePanel);
        Assert.DoesNotContain(modbot, t => t.Target is OverlayTarget.ClosePanel);
    }

    [Fact]
    public void TheSharedFrameIsLaidOutAtTheDesktopDesignWidth()
    {
        var width = AvaloniaTestHost.Run(() =>
        {
            var framed = OverlayView.BuildFramed(Screen(Offered), null, VRChatLook());
            framed.Measure(new Size(2000, 2000));
            return framed.DesiredSize.Width;
        });

        Assert.Equal(VRChatHudLayout.OverlayDesignWidth, width, 1);
    }

    [Fact]
    public void ItSitsAtTheTopOfThePanel()
    {
        var placed = Assert.Single(
            Targets(() => OverlayView.Build(Screen(Offered))),
            t => t.Target is OverlayTarget.DictateButton);

        Assert.True(placed.Bounds.Top < Height / 4.0, $"Too far down the panel: {placed.Bounds.Top}");
    }

    [Fact]
    public void OutsideAGroupInstanceItIsTheOnlyThingDrawn()
    {
        var idle = OverlayScreen.Idle with { Dictate = Offered };
        var targets = Targets(() => OverlayView.Build(idle));

        Assert.Single(targets, t => t.Target is OverlayTarget.DictateButton);
        Assert.DoesNotContain(targets, t => t.Target is OverlayTarget.Roster);
    }

    [Fact]
    public void ATurnOfThePictureRedrawsOnlyTheButtonNotThePanel()
    {
        var (found, outsideKept, outsideCount, buttonKept, before, after) =AvaloniaTestHost.Run(() =>
        {
            var writing = ButtonScreen.Shown with { Dictate = DictateState.Writing };
            var root = OverlayView.BuildFramed(Screen(writing), null, VRChatLook());
            root.Measure(new Size(Width, Height));
            root.Arrange(new Rect(0, 0, Width, Height));

            var oldButton = root.GetVisualDescendants().OfType<Border>().Single(b => b.Tag is OverlayTarget.DictateButton);
            var inside = oldButton.GetVisualDescendants().ToHashSet();
            var outside = root.GetVisualDescendants().Where(v => v != oldButton && !inside.Contains(v)).ToList();
            var dotsBefore = oldButton.GetLogicalDescendants().OfType<Ellipse>().Select(e => e.Opacity).ToList();

            var redrawn = OverlayView.RedrawDictate(root, writing with { Frame = 3 }, VRChatLook());

            var now = root.GetVisualDescendants().ToHashSet();
            var newButton = root.GetVisualDescendants().OfType<Border>().Single(b => b.Tag is OverlayTarget.DictateButton);
            var dotsAfter = newButton.GetLogicalDescendants().OfType<Ellipse>().Select(e => e.Opacity).ToList();

            return (redrawn, outside.All(now.Contains), outside.Count, ReferenceEquals(newButton, oldButton), dotsBefore, dotsAfter);
        });

        Assert.True(found);
        Assert.True(outsideKept, "every other control of the panel is the same one as before");
        Assert.True(outsideCount > 5, "the panel had more than the button in it");
        Assert.True(buttonKept, "the button's own border is kept, so what lights it stays");
        Assert.Equal(8, before.Count);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ATurnOfThePictureOnAPanelWithNoButtonSaysSoAndChangesNothing()
    {
        var redrawn = AvaloniaTestHost.Run(() =>
        {
            var root = OverlayView.BuildFramed(Screen(null), null, VRChatLook());
            return OverlayView.RedrawDictate(root, Offered with { Dictate = DictateState.Writing, Frame = 2 }, VRChatLook());
        });

        Assert.False(redrawn);
    }

    [Fact]
    public void OutsideAGroupInstanceWithDictateNotOfferedThePanelIsStillBlank()
    {
        var targets = Targets(() => OverlayView.Build(OverlayScreen.Idle));

        Assert.Empty(targets);
    }
}
