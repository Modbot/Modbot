using Modbot.Companion.Clips;
using Modbot.Companion.Presentation;
using Xunit;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// Where VRChat's menu is for any window size, and where the desktop overlay fits beside it
/// (Escape Menu design §3.3.1). The numbers were measured on one real VRChat window at 1920 by
/// 1009 and at 2560 by 1440, plus pictures at 1918 by 1008; only the Launch Pad page of the Esc
/// menu was measured, and nobody has seen the result on a live window.
/// </summary>
public sealed class VRChatHudLayoutTests
{
    [Theory]
    [InlineData(1918, 1008, 0.999)]
    [InlineData(1920, 1009, 1.0)]
    [InlineData(1920, 1080, 1.0704)]
    [InlineData(2560, 1440, 1.4272)]
    [InlineData(1280, 720, 0.7136)]
    public void TheHudScaleIsTheClientsHeightOver1009(int width, int height, double scale)
        => Assert.Equal(scale, VRChatHudLayout.For(width, height)!.Scale, 3);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1920, 0)]
    [InlineData(0, 1080)]
    [InlineData(-5, 1080)]
    [InlineData(1920, -1)]
    public void AnEmptyOrNonsensicalClientAreaGivesNothing(int width, int height)
        => Assert.Null(VRChatHudLayout.For(width, height));

    [Theory]
    [InlineData(1918, 1008, 467)]
    [InlineData(1920, 1080, 433)]
    [InlineData(2560, 1440, 578)]
    [InlineData(1280, 720, 289)]
    public void TheFreeStripBesideTheEscMenuIsHalfTheWidthLessTheMenusHalf(int width, int height, int strip)
    {
        var layout = VRChatHudLayout.For(width, height)!;

        // Pixels are whole and the menu's edge is rounded, so a pixel either way is the same strip.
        Assert.InRange(layout.RightStrip.Width, strip - 1, strip + 1);
        Assert.Equal(width, layout.RightStrip.Right);
        Assert.Equal(layout.EscMenuRight, layout.RightStrip.Left);
    }

    [Fact]
    public void TheEscMenuReachesAsFarRightAsItWasSeenToAt1918By1008()
    {
        var layout = VRChatHudLayout.For(1918, 1008)!;

        // Seen on the Launch Pad page: the right Wings panel ends at 959 + 493 = 1452.
        Assert.InRange(layout.EscMenuRight, 1450, 1453);
        Assert.Equal(1918 - layout.EscMenuRight, layout.EscMenuLeft);
    }

    [Fact]
    public void TheTopRightColumnIsWhereTheMicAndTheTwoFunctionKeysAre()
    {
        var column = VRChatHudLayout.For(1918, 1008)!.TopRightColumn;

        // Its left edge at x 1800 and its right edge 23 in from the client's right, 198 down.
        Assert.InRange(column.Left, 1798, 1802);
        Assert.InRange(column.Right, 1894, 1896);
        Assert.Equal(0, column.Top);
        Assert.InRange(column.Bottom, 196, 200);
    }

    [Fact]
    public void TheNotificationBandIsTheMiddleOfTheTopEdge()
    {
        var band = VRChatHudLayout.For(1920, 1009)!.NotificationBand;

        Assert.Equal(660, band.Left);
        Assert.Equal(1160, band.Right);
        Assert.Equal(0, band.Top);
        Assert.Equal(175, band.Bottom);
    }

    [Theory]
    [InlineData(1918, 1008, 443, 613)]
    [InlineData(1920, 1009, 444, 614)]
    [InlineData(1920, 1080, 407, 563)]
    [InlineData(2560, 1440, 543, 751)]
    [InlineData(1280, 720, 271, 375)]
    public void TheOverlayTakesTheStripLessAGapEitherSideAtThePanelsOwnShape(int width, int height, int overlayWidth, int overlayHeight)
    {
        var fit = VRChatHudLayout.For(width, height)!.Overlay()!.Value;

        Assert.InRange(fit.Width, overlayWidth - 1, overlayWidth + 1);
        Assert.InRange(fit.Height, overlayHeight - 1, overlayHeight + 1);

        // The scale is how much the 520 by 720 panel is drawn bigger or smaller.
        Assert.Equal(fit.Width / VRChatHudLayout.OverlayDesignWidth, fit.Scale, 6);
        Assert.InRange(
            fit.Height / (double)fit.Width,
            (VRChatHudLayout.OverlayDesignHeight / VRChatHudLayout.OverlayDesignWidth) - 0.01,
            (VRChatHudLayout.OverlayDesignHeight / VRChatHudLayout.OverlayDesignWidth) + 0.01);
    }

    [Theory]
    [InlineData(1918, 1008)]
    [InlineData(1920, 1009)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1200)]
    [InlineData(3840, 2160)]
    [InlineData(1200, 400)]
    public void TheOverlayNeverOverlapsVRChatsMenuOrTheCornerColumnAndStaysInsideTheWindow(int width, int height)
    {
        var layout = VRChatHudLayout.For(width, height)!;
        var fit = layout.Overlay()!.Value;
        var box = fit.Box;

        Assert.False(box.Overlaps(layout.EscMenu), "overlaps VRChat's Esc menu");
        Assert.False(box.Overlaps(layout.TopRightColumn), "overlaps the top-right column");
        Assert.False(box.Overlaps(layout.NotificationBand), "overlaps the notification band");

        Assert.True(box.Left >= layout.EscMenuRight, "starts left of the menu's right edge");
        Assert.True(box.Left >= layout.RightStrip.Left);
        Assert.True(box.Top >= layout.TopRightColumn.Bottom, "starts above the bottom of the corner column");
        Assert.True(box.Right <= width, "reaches past the right edge");
        Assert.True(box.Bottom <= height, "reaches past the bottom edge");
    }

    [Theory]
    [InlineData(1918, 1008)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(1280, 720)]
    public void TheOverlayKeepsAGapOfTwelveHudPixelsFromTheMenuAndTheEdge(int width, int height)
    {
        var layout = VRChatHudLayout.For(width, height)!;
        var fit = layout.Overlay()!.Value;
        var gap = VRChatHudLayout.Gap * layout.Scale;

        Assert.True(fit.X - layout.EscMenuRight >= gap - 1, "too close to the menu");
        Assert.True(width - fit.Box.Right >= gap - 1, "too close to the right edge");
        Assert.True(height - fit.Box.Bottom >= gap - 1, "too close to the bottom edge");
    }

    [Fact]
    public void ARoomyWindowIsLimitedByTheStripsWidthAndATallNarrowOneByItsHeight()
    {
        // 1920 by 1080: the strip (433) is the limit, the panel's 720 tall fits in the 842 free.
        var wide = VRChatHudLayout.For(1920, 1080)!.Overlay()!.Value;
        Assert.InRange(wide.Y + wide.Height, 0, 1080 - 12);

        // 1200 by 400: the strip is 405 wide, but at that scale (0.396) only about 312 is free
        // down the side, between the corner column and the bottom gap, so the panel shrinks to
        // fit that rather than run off the bottom.
        var squat = VRChatHudLayout.For(1200, 400)!.Overlay()!.Value;
        Assert.InRange(squat.Height, 308, 312);
        Assert.True(squat.Width < 405 - (2 * 12 * VRChatHudLayout.ScaleFor(400)));
        Assert.Equal(1200 - (int)Math.Floor(12 * VRChatHudLayout.ScaleFor(400)), squat.Box.Right);
    }

    [Fact]
    public void ABiggerWindowGivesABiggerOverlay()
    {
        var small = VRChatHudLayout.For(1280, 720)!.Overlay()!.Value;
        var normal = VRChatHudLayout.For(1920, 1080)!.Overlay()!.Value;
        var large = VRChatHudLayout.For(2560, 1440)!.Overlay()!.Value;

        Assert.True(small.Scale < normal.Scale);
        Assert.True(normal.Scale < large.Scale);
    }

    [Theory]
    [InlineData(1000, 1009)]
    [InlineData(800, 1000)]
    [InlineData(10, 10)]
    public void AWindowTooNarrowForAStripGivesNoPlaceRatherThanOneOnTheMenu(int width, int height)
        => Assert.Null(VRChatHudLayout.For(width, height)!.Overlay());

    [Fact]
    public void WithNoVRChatWindowTheOverlayStaysWhereItAlwaysWas()
        => Assert.Null(VRChatHudLayout.OverlayFor(GameWindow.Missing));

    [Fact]
    public void WithAMinimisedVRChatTheOverlayStaysWhereItAlwaysWas()
        => Assert.Null(VRChatHudLayout.OverlayFor(new GameWindow(Found: true, InFront: false, Minimised: true, Width: 1920, Height: 1080)));

    [Fact]
    public void WithAFoundWindowWhoseSizeWindowsDidNotGiveTheOverlayStaysWhereItAlwaysWas()
        => Assert.Null(VRChatHudLayout.OverlayFor(new GameWindow(Found: true, InFront: true, Minimised: false, Width: 0, Height: 0)));

    [Fact]
    public void WithVRChatInFrontOrBehindTheOverlayIsBesideItsMenuEitherWay()
    {
        var front = VRChatHudLayout.OverlayFor(new GameWindow(Found: true, InFront: true, Minimised: false, Width: 1920, Height: 1080));
        var behind = VRChatHudLayout.OverlayFor(new GameWindow(Found: true, InFront: false, Minimised: false, Width: 1920, Height: 1080));

        Assert.NotNull(front);
        Assert.Equal(front, behind);
    }
}
