using Modbot.Companion.Overlay;
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
}
