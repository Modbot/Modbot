using Avalonia;
using Avalonia.Controls;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The shared frame as the desktop window hosts it, in both looks, and the lock and hand bar under it.
/// </summary>
public class WindowFrameTests
{
    private const int Width = 520;
    private const int Height = 720;

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

    private static OverlayScreen Screen() => new(
        "Cat Lounge",
        new Cached<InstanceContext>(
            new InstanceContext("39911", [.. Enumerable.Range(0, 3).Select(i => new RosterMember($"usr_{i}", $"Person {i}", RosterStanding.Member, 0, []))]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh,
        Dictate: ButtonScreen.Shown with { Dictate = DictateState.Idle });

    private static IReadOnlyList<PlacedTarget> Targets(Func<Control> build)
        => AvaloniaTestHost.Run(() =>
        {
            var root = build();
            root.Measure(new Size(Width, Height));
            root.Arrange(new Rect(0, 0, Width, Height));
            return OverlayTargets.Find(root);
        });

    private static OverlayView.WindowFrame Window => new(255, true);

    public static TheoryData<bool> Looks => new() { true, false };

    private static OverlayLook Look(bool vrchat) => vrchat ? VRChatLook() : OverlayLook.Headset;

    [Theory]
    [MemberData(nameof(Looks))]
    public void TheWindowFrameHasCloseAndDictateInBothLooks(bool vrchat)
    {
        var targets = Targets(() => OverlayView.BuildFramed(Screen(), null, Look(vrchat), window: Window));

        Assert.Single(targets, t => t.Target is OverlayTarget.ClosePanel);
        Assert.Single(targets, t => t.Target is OverlayTarget.DictateButton);
    }

    [Fact]
    public void TheModbotLookHasNoSecondDictateInTheBody()
    {
        var targets = Targets(() => OverlayView.BuildFramed(Screen(), null, OverlayLook.Headset, window: Window));

        Assert.Single(targets, t => t.Target is OverlayTarget.DictateButton);
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void TheGroupNameIsOnTheStripOnlyNotInTheBodyToo(bool vrchat)
    {
        var count = AvaloniaTestHost.Run(() =>
        {
            var root = OverlayView.BuildFramed(Screen(), null, Look(vrchat), window: Window);
            root.Measure(new Size(Width, Height));
            root.Arrange(new Rect(0, 0, Width, Height));

            return Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root)
                .OfType<TextBlock>()
                .Count(t => t.Text == "Cat Lounge");
        });

        Assert.Equal(1, count);
    }

    [Fact]
    public void TheModbotLookIsStillNotFramedWithoutTheWindow()
    {
        var targets = Targets(() => OverlayView.BuildFramed(Screen(), null, OverlayLook.Headset));

        Assert.DoesNotContain(targets, t => t.Target is OverlayTarget.ClosePanel);
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void TheStripCanBeFoundByItsName(bool vrchat)
    {
        var onStrip = AvaloniaTestHost.Run(() =>
        {
            var root = OverlayView.BuildFramed(Screen(), null, Look(vrchat), window: Window);
            root.Measure(new Size(Width, Height));
            root.Arrange(new Rect(0, 0, Width, Height));

            var close = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root)
                .OfType<Control>()
                .First(c => c.Tag is OverlayTarget.ClosePanel);

            return OverlayView.OnStrip(close);
        });

        Assert.True(onStrip);
        Assert.False(OverlayView.OnStrip(null));
    }

    [Fact]
    public void TheEditBarHasLockAndHand()
    {
        var targets = Targets(() => PanelFrame.EditBar(locked: false, clickThrough: false));

        Assert.Single(targets, t => t.Target is OverlayTarget.Lock);
        Assert.Single(targets, t => t.Target is OverlayTarget.ClickThrough);
    }
}
