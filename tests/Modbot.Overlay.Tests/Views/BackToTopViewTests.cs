using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The arrow that takes a scrolled list back to its top: not there at the top, there once the list is
/// scrolled down (by pixels on the headset, by rows on the desktop window), its own target above the rows,
/// and carrying no words.
/// </summary>
public class BackToTopViewTests
{
    private const int Width = 480;
    private const int Height = 640;

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 18, 30, 0, TimeSpan.Zero);

    private static InstanceContext Roster(int people) => new(
        "39911",
        [.. Enumerable.Range(0, people).Select(i => new RosterMember($"usr_{i}", $"Person {i}", RosterStanding.Member, 0, []))]);

    private static OverlayScreen Screen(OverlayPage page = OverlayPage.Instance, double? scroll = null, int skip = 0, int eventSkip = 0, int events = 0) => new(
        "Cat Lounge",
        new Cached<InstanceContext>(Roster(30), Freshness.Fresh, TimeSpan.Zero),
        Freshness.Fresh,
        RosterSkip: skip,
        Page: page,
        Events: [.. Enumerable.Range(0, events).Select(i => new LiveEvent(
            i.ToString(),
            i.ToString(),
            LiveEventKinds.PersonJoined,
            Now.AddMinutes(-i),
            "39911",
            new LivePerson($"usr_{i}", $"Person {i}", "Trusted User", RosterStanding.Ordinary, 0, []),
            false,
            null,
            false))],
        ScrollOffset: scroll,
        EventSkip: eventSkip,
        Now: Now);

    private static IReadOnlyList<PlacedTarget> Targets(OverlayScreen screen) => AvaloniaTestHost.Run(() =>
    {
        var root = OverlayView.Build(screen);
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        return OverlayTargets.Find(root);
    });

    private static List<OverlayTarget.BackToTop> Arrows(OverlayScreen screen)
        => [.. Targets(screen).Select(placed => placed.Target).OfType<OverlayTarget.BackToTop>()];

    [Fact]
    public void AListAtItsTopHasNoArrow()
    {
        Assert.Empty(Arrows(Screen(scroll: 0)));
    }

    [Fact]
    public void AListScrolledDownShowsTheArrowOnceAsItsOwnTarget()
    {
        var arrow = Assert.Single(Arrows(Screen(scroll: 300)));

        Assert.Equal(OverlayPage.Instance, arrow.List);
    }

    [Fact]
    public void TheArrowFloatsAtTheTopOfTheListAndIsNotMovedWithTheRows()
    {
        PlacedTarget Arrow(double scroll) => Assert.Single(Targets(Screen(scroll: scroll)), placed => placed.Target is OverlayTarget.BackToTop);

        var near = Arrow(300);
        var far = Arrow(500);

        Assert.Equal(near.Bounds, far.Bounds);
    }

    [Fact]
    public void APointOnTheArrowIsTheArrowAndNotTheRowUnderIt()
    {
        var found = AvaloniaTestHost.Run(() =>
        {
            var root = OverlayView.Build(Screen(scroll: 300));
            root.Measure(new Size(Width, Height));
            root.Arrange(new Rect(0, 0, Width, Height));

            var arrow = OverlayTargets.Find(root).Single(placed => placed.Target is OverlayTarget.BackToTop);
            return OverlayTargets.At(root, arrow.Bounds.Center);
        });

        Assert.IsType<OverlayTarget.BackToTop>(found);
    }

    [Fact]
    public void TheArrowHasNoWords()
    {
        var words = AvaloniaTestHost.Run(() =>
        {
            var root = OverlayView.Build(Screen(scroll: 300));
            root.Measure(new Size(Width, Height));
            root.Arrange(new Rect(0, 0, Width, Height));

            var arrow = root.GetVisualDescendants().OfType<Border>().Single(border => border.Tag is OverlayTarget.BackToTop);
            return arrow.GetVisualDescendants().OfType<TextBlock>().Count();
        });

        Assert.Equal(0, words);
    }

    [Fact]
    public void TheDesktopRosterShowsTheArrowOnlyOnceRowsAreScrolledPast()
    {
        Assert.Empty(Arrows(Screen(skip: 0)));

        var arrow = Assert.Single(Arrows(Screen(skip: 2)));
        Assert.Equal(OverlayPage.Instance, arrow.List);
    }

    [Fact]
    public void TheDesktopAuditLogShowsTheArrowOnlyOnceRowsAreScrolledPast()
    {
        Assert.Empty(Arrows(Screen(OverlayPage.Events, events: 12, eventSkip: 0)));

        var arrow = Assert.Single(Arrows(Screen(OverlayPage.Events, events: 12, eventSkip: 3)));
        Assert.Equal(OverlayPage.Events, arrow.List);
    }

    [Fact]
    public void TheArrowIsOnTheMoreAboveLineInVRChatsLookToo()
    {
        var look = OverlayLook.FromColours(new OverlayColours(
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

        int Count(int skip) => AvaloniaTestHost.Run(() =>
        {
            var root = OverlayView.Build(Screen(skip: skip), null, look);
            root.Measure(new Size(Width, Height));
            root.Arrange(new Rect(0, 0, Width, Height));
            return OverlayTargets.Find(root).Count(placed => placed.Target is OverlayTarget.BackToTop);
        });

        Assert.Equal(0, Count(0));
        Assert.Equal(1, Count(2));
    }
}
