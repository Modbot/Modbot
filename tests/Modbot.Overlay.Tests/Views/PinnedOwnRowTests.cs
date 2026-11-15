using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The desktop window's Instance list drawn whole and moved by pixels: the moderator's own row stays at the
/// top while the others scroll under it (and scrolls away with them when nobody asked for it to stay, as on
/// the headset), the helpers the window moves the list with, and the plus in VRChat's square button being in
/// the middle of it.
/// </summary>
public class PinnedOwnRowTests
{
    private const double PanelWidth = 520;
    private const double PanelHeight = 720;

    private static readonly DateTimeOffset Now = new(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);

    private static readonly string Moderator = "usr_" + 5;

    private static OverlayScreen Screen(double scroll, bool pin) => new(
        "Cat Lounge",
        new Cached<InstanceContext>(
            new InstanceContext(
                "39911",
                [.. Enumerable.Range(0, 30).Select(i => new RosterMember($"usr_{i}", $"Person {i:00}", RosterStanding.Ordinary, 0, []))]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh,
        ScrollOffset: scroll,
        PinOwnRow: pin,
        ModeratorId: Moderator,
        CanPlaceHeadsUps: true,
        Now: Now);

    private static OverlayLook VRChatLook()
    {
        static PaletteColour c(byte r, byte g, byte b) => new(r, g, b);

        return OverlayLook.FromColours(new OverlayColours(
            Panel: c(0x18, 0x12, 0x24),
            Bar: c(0x10, 0x0C, 0x1A),
            Button: c(0x2A, 0x20, 0x44),
            Border: c(0x38, 0x2C, 0x5A),
            Hover: c(0x30, 0x26, 0x4E),
            Selected: c(0x50, 0x3A, 0x90),
            SelectedBorder: c(0x80, 0x60, 0xE0),
            SelectedText: c(0xF5, 0xF5, 0xF5),
            Icon: c(0xA0, 0x90, 0xD0),
            Text: c(0xF0, 0xF0, 0xF8),
            Subtext: c(0xB0, 0xA8, 0xC8)));
    }

    private static T Draw<T>(OverlayScreen screen, Func<Control, T> read) => AvaloniaTestHost.Run(() =>
    {
        var root = OverlayView.Build(screen, null, VRChatLook());
        root.Measure(new Size(PanelWidth, PanelHeight));
        root.Arrange(new Rect(0, 0, PanelWidth, PanelHeight));
        return read(root);
    });

    private static double? ModeratorTop(OverlayScreen screen) => Draw(
        screen,
        root => OverlayTargets.Find(root).Where(placed => placed.Target is OverlayTarget.Person { SubjectId: var id } && id == Moderator).Select(placed => (double?)placed.Bounds.Top).FirstOrDefault());

    [Fact]
    public void WithTheOwnRowPinnedItStaysWhereItIsWhateverTheListIsScrolledTo()
    {
        var atTop = ModeratorTop(Screen(0, pin: true));
        var scrolled = ModeratorTop(Screen(300, pin: true));

        Assert.NotNull(atTop);
        Assert.Equal(atTop, scrolled);
    }

    [Fact]
    public void WithoutPinningTheOwnRowScrollsAwayWithTheList()
    {
        var atTop = ModeratorTop(Screen(0, pin: false));
        var scrolled = ModeratorTop(Screen(300, pin: false));

        Assert.NotNull(atTop);
        Assert.True(scrolled is null || scrolled < atTop);
    }

    [Fact]
    public void ThePinnedListStillScrollsTheOthers()
    {
        double? Other(double scroll) => Draw(
            Screen(scroll, pin: true),
            root => OverlayTargets.Find(root).Where(placed => placed.Target is OverlayTarget.Person { SubjectId: "usr_12" }).Select(placed => (double?)placed.Bounds.Top).FirstOrDefault());

        var near = Other(0);
        var far = Other(300);

        Assert.NotNull(far);
        Assert.True(near is null || far < near);
    }

    [Fact]
    public void TheWindowCanReadHowFarTheListGoesAndMoveIt()
    {
        var (max, set, huge, none) = AvaloniaTestHost.Run(() =>
        {
            var root = OverlayView.Build(Screen(0, pin: true), null, VRChatLook());
            root.Measure(new Size(PanelWidth, PanelHeight));
            root.Arrange(new Rect(0, 0, PanelWidth, PanelHeight));

            var max = OverlayView.MaxScroll(root);
            return (max, OverlayView.SetScroll(root, 100), OverlayView.SetScroll(root, max + 5000), OverlayView.SetScroll(new Border(), 100));
        });

        Assert.True(max > 0);
        Assert.Equal(100, set);
        Assert.InRange(huge, 0, max);
        Assert.Equal(max, huge);
        Assert.Equal(0, none);
    }

    [Fact]
    public void AWheelNotchIsRowsOfTheLooksOwnPitch()
    {
        Assert.Equal(40 + 4, OverlayView.RowPitch(VRChatLook()));
        Assert.Equal(40 + 4, OverlayView.RowPitch(OverlayLook.Headset));
    }

    [Fact]
    public void ThePlusIsInTheMiddleOfItsSquareBothWays()
    {
        var (square, plus) = Draw(Screen(0, pin: true), root =>
        {
            var button = root.GetVisualDescendants().OfType<Border>()
                .First(border => border.Tag is OverlayTarget.AddHeadsUp);
            var mark = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();

            return (button.Bounds.Size, mark.Bounds);
        });

        Assert.Equal(square.Width / 2, plus.Center.X, 1);
        Assert.Equal(square.Height / 2, plus.Center.Y, 1);
    }
}
