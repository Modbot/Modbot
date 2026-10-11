using Avalonia;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The header buttons (the tabs, then the filters) sit on a slim backing, in both looks: a ray
/// between two buttons is still on the panel, so the cursor stays and the press is caught instead
/// of passing through to VRChat. Space beside a row stays clear.
/// </summary>
public class HeaderBackingTests
{
    private const int Size = 512;

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

    private static OverlayScreen Screen(OverlayPage page) => new(
        "Cat Lounge",
        new Cached<InstanceContext>(
            new InstanceContext("39911", [new RosterMember("usr_kai", "Kai", RosterStanding.Member, 0, [])]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh,
        Page: page,
        Events:
        [
            new LiveEvent(
                "e1",
                "e1",
                LiveEventKinds.PersonJoined,
                DateTimeOffset.UnixEpoch,
                "39911",
                new LivePerson("usr_kai", "Kai", null, RosterStanding.Member, 0, []),
                false,
                null,
                false),
        ]);

    /// <summary>Lays the screen out and says whether the point between two neighbouring buttons is drawn.</summary>
    private static bool GapIsDrawn(OverlayScreen screen, bool inVRChatLook, Func<OverlayTarget, bool> isButton)
        => AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(Size, Size);
            var root = OverlayView.Build(screen, null, inVRChatLook ? VRChatLook() : OverlayLook.Headset);
            renderer.Render(root);

            var buttons = OverlayTargets.Find(root).Where(t => isButton(t.Target)).Select(t => t.Bounds).ToList();

            // The first two buttons on one line: the gap between them is the one the cursor fell into.
            var first = buttons[0];
            var second = buttons.First(b => b.Left > first.Right && Math.Abs(b.Center.Y - first.Center.Y) < 2);
            Assert.True(second.Left - first.Right > 1, "the buttons have a gap between them");

            return OverlayTargets.Drawn(root, new Point((first.Right + second.Left) / 2, first.Center.Y));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCursorStaysBetweenTheTabs(bool inVRChatLook)
        => Assert.True(GapIsDrawn(Screen(OverlayPage.Instance), inVRChatLook, t => t is OverlayTarget.GoTo));

    [Theory]
    [InlineData(OverlayPage.Instance, false)]
    [InlineData(OverlayPage.Instance, true)]
    [InlineData(OverlayPage.Events, false)]
    [InlineData(OverlayPage.Events, true)]
    public void TheCursorStaysBetweenTheFilters(OverlayPage page, bool inVRChatLook)
        => Assert.True(GapIsDrawn(Screen(page), inVRChatLook, t => t is OverlayTarget.Filter));

    [Fact]
    public void TheBackingIsAsWideAsTheTabsAndNoWiderInTheHeadsetLook()
    {
        var (beside, on) = AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(Size, Size);
            var root = OverlayView.Build(Screen(OverlayPage.Instance));
            renderer.Render(root);

            var tabs = OverlayTargets.Find(root).Where(t => t.Target is OverlayTarget.GoTo).Select(t => t.Bounds).ToList();
            var last = tabs.MaxBy(b => b.Right);

            return (
                OverlayTargets.Drawn(root, new Point(last.Right + 60, last.Center.Y)),
                OverlayTargets.Drawn(root, new Point(last.Right + 2, last.Center.Y)));
        });

        Assert.True(on);
        Assert.False(beside);
    }

    /// <summary>The Instance list with its Rank choices open.</summary>
    private static OverlayScreen WithChoices(OverlayPage page) => Screen(page) with
    {
        RosterFilters = page is OverlayPage.Instance ? new ListFilters(Open: FilterPart.Rank) : null,
        EventFilters = page is OverlayPage.Events ? new ListFilters(Open: FilterPart.Kind) : null,
    };

    [Theory]
    [InlineData(OverlayPage.Instance, false)]
    [InlineData(OverlayPage.Instance, true)]
    [InlineData(OverlayPage.Events, false)]
    [InlineData(OverlayPage.Events, true)]
    public void TheCursorStaysBetweenTheFilterRowAndItsOpenChoices(OverlayPage page, bool inVRChatLook)
    {
        var drawn = AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(Size, Size);
            var root = OverlayView.Build(WithChoices(page), null, inVRChatLook ? VRChatLook() : OverlayLook.Headset);
            renderer.Render(root);

            var targets = OverlayTargets.Find(root);
            var filters = targets.Where(t => t.Target is OverlayTarget.Filter).Select(t => t.Bounds).ToList();
            var picks = targets.Where(t => t.Target is OverlayTarget.Pick).Select(t => t.Bounds).ToList();

            // Halfway between the lowest filter button and the choices' first button: the space the
            // cursor used to fall through.
            var above = filters.MaxBy(b => b.Bottom);
            var below = picks.MinBy(b => b.Top);
            Assert.True(below.Top - above.Bottom > 1, "the filters and their choices have a gap between them");

            return OverlayTargets.Drawn(root, new Point(above.Center.X, (above.Bottom + below.Top) / 2));
        });

        Assert.True(drawn);
    }
}
