using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Core.Users;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// What the notification overlay draws, and — more importantly — what it does not. It sits in the
/// corner of a moderator's eye for their whole session, so an empty stack has to come out as
/// nothing at all rather than as a faint dark square.
/// </summary>
public class NotificationViewTests
{
    private const int Size = 160;

    private static byte[] Render(NotificationScreen screen) => AvaloniaTestHost.Run(() =>
    {
        using var renderer = new AvaloniaFrameRenderer(Size, Size);
        return renderer.Render(NotificationView.Build(screen)).ToArray();
    });

    private static PopUp Flagged(string id = "a1") =>
        new(id, "Cat Lounge", "Rin", "kicked before", PopUpTone.Flagged);

    [Fact]
    public void AnEmptyStackDrawsNothingAtAll()
    {
        var pixels = Render(NotificationScreen.Empty);

        Assert.Equal(Size * Size * 4, pixels.Length);
        Assert.True(pixels.All(b => b == 0), "An empty notification overlay must be fully transparent.");
    }

    [Fact]
    public void APopUpDrawsSomething()
    {
        var pixels = Render(new NotificationScreen([Flagged()]));

        Assert.Contains(pixels, b => b != 0);
    }

    [Fact]
    public void EveryToneDrawsWithoutThrowing()
    {
        foreach (var tone in Enum.GetValues<PopUpTone>())
        {
            var pixels = Render(new NotificationScreen(
                [new PopUp("p", "Modbot", "Something happened", "and here is a little more", tone)]));

            Assert.Contains(pixels, b => b != 0);
        }
    }

    [Fact]
    public void AJoinCardWithARankAndTheMarkDrawsOnTheHeadsetAndOnAMonitor()
    {
        var joined = new PopUp("joined:usr_rin", "Joined", "Rin", null, PopUpTone.Plain, TrustRank.TrustedUser, EighteenPlus: true);
        var plain = new PopUp("joined:usr_rin", "Joined", "Rin", null, PopUpTone.Plain);

        foreach (var tokens in new[] { DesignTokens.Vr, DesignTokens.Desktop })
        {
            var (marked, bare) = AvaloniaTestHost.Run(() =>
            {
                using var renderer = new AvaloniaFrameRenderer(Size, Size);
                var one = renderer.Render(NotificationView.Build(new NotificationScreen([joined]), tokens)).ToArray();
                var two = renderer.Render(NotificationView.Build(new NotificationScreen([plain]), tokens)).ToArray();
                return (one, two);
            });

            // The rank's dot, its name and the chip are drawn: the card is not the bare one.
            Assert.False(marked.SequenceEqual(bare));
        }
    }

    [Theory]
    [InlineData("left:usr_rin", "Left", null, PopUpTone.Plain)]
    [InlineData("changed avatar:usr_rin", "Changed avatar", "Tall Cat", PopUpTone.Plain)]
    [InlineData("alert:a1", "Flagged user joined", "kicked before", PopUpTone.Flagged)]
    public void EveryCardAboutAPersonDrawsItsMarks(string id, string heading, string? detail, PopUpTone tone)
    {
        var marked = new PopUp(id, heading, "Rin", detail, tone, TrustRank.KnownUser, EighteenPlus: true, SubjectId: "usr_rin");
        var bare = marked with { Rank = null, EighteenPlus = false };

        foreach (var tokens in new[] { DesignTokens.Vr, DesignTokens.Desktop })
        {
            var (withMarks, without) = AvaloniaTestHost.Run(() =>
            {
                using var renderer = new AvaloniaFrameRenderer(Size, Size);
                var one = renderer.Render(NotificationView.Build(new NotificationScreen([marked]), tokens)).ToArray();
                var two = renderer.Render(NotificationView.Build(new NotificationScreen([bare]), tokens)).ToArray();
                return (one, two);
            });

            Assert.False(withMarks.SequenceEqual(without));
        }
    }

    [Fact]
    public void TwoCardsDifferingOnlyInTheirMarksDoNotLookTheSame()
    {
        // A rank filled in while the card is up has to be drawn.
        var bare = new NotificationScreen([new PopUp("joined:usr_rin", "Joined", "Rin", null, PopUpTone.Plain)]);
        var ranked = new NotificationScreen([new PopUp("joined:usr_rin", "Joined", "Rin", null, PopUpTone.Plain, TrustRank.User)]);

        Assert.False(bare.LooksTheSameAs(ranked));
    }

    [Fact]
    public void ANameMadeOfNewlinesCannotPushTheRestOffThePanel()
    {
        // Display names are arbitrary user-controlled text. One line each, trimmed, or a name
        // built out of newlines would shove the pop-ups out of the headset's view.
        var hostile = new PopUp("p", "Cat Lounge", new string('\n', 40) + "pushed", "detail", PopUpTone.Flagged);

        var pixels = Render(new NotificationScreen([hostile, Flagged("a2")]));

        Assert.Equal(Size * Size * 4, pixels.Length);
        Assert.Contains(pixels, b => b != 0);
    }

    [Fact]
    public void TwoScreensWithTheSamePopUpsLookTheSame()
    {
        var one = new NotificationScreen([Flagged(), Flagged("a2")]);
        var two = new NotificationScreen([Flagged(), Flagged("a2")]);

        Assert.True(one.LooksTheSameAs(two));
        Assert.False(one.LooksTheSameAs(new NotificationScreen([Flagged("a2"), Flagged()])));
        Assert.False(one.LooksTheSameAs(NotificationScreen.Empty));
    }

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

    private static byte[] RenderLook(NotificationScreen screen, OverlayLook look, double scale = 1) => AvaloniaTestHost.Run(() =>
    {
        using var renderer = new AvaloniaFrameRenderer(Size, Size);
        return renderer.Render(NotificationView.Build(screen, look, scale)).ToArray();
    });

    [Fact]
    public void TheModbotLookDrawsWhatItAlwaysDid()
    {
        var screen = new NotificationScreen([Flagged()]);

        Assert.True(Render(screen).SequenceEqual(RenderLook(screen, OverlayLook.Headset)));
    }

    [Fact]
    public void VRChatsLookDrawsTheCardsInItsOwnColours()
    {
        var screen = new NotificationScreen([Flagged()]);

        var modbot = RenderLook(screen, OverlayLook.Headset);
        var vrchat = RenderLook(screen, VRChatLook());

        Assert.Contains(vrchat, b => b != 0);
        Assert.False(modbot.SequenceEqual(vrchat));
    }

    [Fact]
    public void AnEmptyStackDrawsNothingInVRChatsLookEither()
    {
        var pixels = RenderLook(NotificationScreen.Empty, VRChatLook(), NotificationView.DesktopScale);

        Assert.True(pixels.All(b => b == 0), "An empty notification overlay must be fully transparent.");
    }

    [Fact]
    public void ADesktopPopUpInVRChatsLookIsDrawnSmallerThanItIsLaidOut()
    {
        var screen = new NotificationScreen([Flagged()]);

        var (laidOut, drawn) = AvaloniaTestHost.Run(() =>
        {
            var plain = NotificationView.Build(screen, VRChatLook());
            var smaller = NotificationView.Build(screen, VRChatLook(), NotificationView.DesktopScale);
            plain.Measure(new Avalonia.Size(340, double.PositiveInfinity));
            smaller.Measure(new Avalonia.Size(340, double.PositiveInfinity));
            return (plain.DesiredSize.Height, smaller.DesiredSize.Height);
        });

        Assert.True(drawn < laidOut);
    }
}
