using Modbot.Companion.Clips;

namespace Modbot.Companion.Tests.Clips;

/// <summary>
/// What keeps a clip to VRChat's window: when a frame is copied and when the last one is written
/// again, how much of the screen is taken, and what a window that is not where it was does.
/// </summary>
/// <remarks>
/// These are the half of the recorder that can be checked without a screen. The other half —
/// Direct3D, the encoder, the calls that ask Windows where VRChat is — cannot be, which is exactly
/// why the decisions were pulled out here rather than left inside the loop.
/// </remarks>
public class ClipWindowRuleTests
{
    private static GameWindow Running(int width = 1920, int height = 1080)
        => new(Found: true, InFront: true, Minimised: false, width, height);

    [Fact]
    public void WhileVRChatIsInFrontItsPictureIsCopied()
    {
        Assert.Equal(ClipFrame.Copy, ClipWindowRule.Decide(Running()));
    }

    [Fact]
    public void WithNoVRChatWindowTheLastPictureIsWrittenAgain()
    {
        // VRChat has not started yet, or has closed. Nothing is copied, and the clip does not end
        // half way through a file.
        Assert.Equal(ClipFrame.HoldLastPicture, ClipWindowRule.Decide(GameWindow.Missing));
    }

    [Fact]
    public void WhileTheModeratorIsInAnotherProgramTheLastPictureIsWrittenAgain()
    {
        // The whole point of recording a window rather than a monitor. Alt-tab to Discord and the
        // clip holds VRChat; it does not start recording the messages.
        var away = Running() with { InFront = false };

        Assert.Equal(ClipFrame.HoldLastPicture, ClipWindowRule.Decide(away));
    }

    [Fact]
    public void AMinimisedVRChatIsHeldRatherThanCopied()
    {
        var minimised = Running() with { Minimised = true };

        Assert.False(minimised.HasPicture);
        Assert.Equal(ClipFrame.HoldLastPicture, ClipWindowRule.Decide(minimised));
    }

    [Fact]
    public void AWindowWithNoSizeIsHeldRatherThanCopied()
    {
        // Windows hands back a rectangle of nothing for a window that is being created or
        // destroyed. A zero-wide copy is a crash on somebody's PC.
        Assert.Equal(ClipFrame.HoldLastPicture, ClipWindowRule.Decide(Running(0, 0)));
        Assert.Equal(ClipFrame.HoldLastPicture, ClipWindowRule.Decide(Running(1, 1)));
    }

    [Theory]
    [InlineData(2560, 1440, 1280, 720)]
    [InlineData(1920, 1080, 960, 540)]
    [InlineData(1280, 720, 1280, 720)]
    [InlineData(1024, 768, 1024, 768)]
    public void TheRecordedSizeIsTheWindowHalvedUntilItFits(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), ClipWindowRule.RecordedSize(width, height));
    }

    [Fact]
    public void TheRecordedSizeIsAlwaysEven()
    {
        // H.264 will not take odd sizes, and a window can be any size a moderator drags it to.
        var (width, height) = ClipWindowRule.RecordedSize(1001, 777);

        Assert.Equal(0, width % 2);
        Assert.Equal(0, height % 2);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(1280, 720)]
    [InlineData(1024, 768)]
    [InlineData(3840, 2160)]
    [InlineData(2560, 1080)]
    [InlineData(3440, 1440)]
    public void AWindowThatHasNotChangedFillsTheWholeFrame(int windowWidth, int windowHeight)
    {
        var (width, height) = ClipWindowRule.RecordedSize(windowWidth, windowHeight);
        var fit = ClipWindowRule.Fit(windowWidth, windowHeight, width, height);

        Assert.Equal(width, fit.Width);
        Assert.Equal(height, fit.Height);
        Assert.Equal(0, fit.Left);
        Assert.Equal(0, fit.Top);

        // And the graphics card's own halving landed exactly on it, so nothing has to be scaled
        // afterwards at all. That is every frame of an ordinary clip.
        Assert.True(fit.Exact);
    }

    [Fact]
    public void AWindowWhoseHalfIsOddStillFillsTheFrame()
    {
        // The bug a moderator reported on 2026-09-19, in numbers. A 1366 x 768 window is halved
        // once to fit the 1280 cap, and 1366 / 2 is 683 — odd, and H.264 will not take odd sizes,
        // so the frame was trimmed to 682. The old rule then compared the un-trimmed 683 against
        // that 682, found it did not fit, and halved again: the picture landed at 340 x 192 in the
        // top-left corner of a 682 x 384 frame, filling a quarter of it, with the rest black.
        //
        // One pixel. It is worth a test of its own because nothing about the clip looked wrong
        // until somebody opened it.
        var (width, height) = ClipWindowRule.RecordedSize(1366, 768);

        Assert.Equal((682, 384), (width, height));

        var fit = ClipWindowRule.Fit(1366, 768, width, height);

        Assert.Equal(682, fit.Width);
        Assert.True(fit.Height >= 380, $"The picture is {fit.Height} tall in a {height} frame.");
        Assert.Equal(1, fit.Level);

        // Nowhere near a quarter of the frame: what is left over is a strip, not three of them.
        Assert.True(fit.LeftOver(width, height) < width * height / 20);
    }

    [Theory]
    [InlineData(1680, 1050)]
    [InlineData(2880, 1620)]
    [InlineData(1366, 768)]
    [InlineData(1442, 802)]
    public void AnOddPixelNeverCostsHalfThePicture(int windowWidth, int windowHeight)
    {
        // The whole family the screenshot came from: any window whose halved size is odd. Each one
        // used to drop the picture to half the width and half the height of the frame.
        var (width, height) = ClipWindowRule.RecordedSize(windowWidth, windowHeight);
        var fit = ClipWindowRule.Fit(windowWidth, windowHeight, width, height);

        Assert.True(fit.Width >= width - 2, $"{fit.Width} of {width} across.");
        Assert.True(fit.Height >= height - 2, $"{fit.Height} of {height} down.");
    }

    [Fact]
    public void AWindowDraggedBiggerIsScaledDownRatherThanLeavingMoreBlack()
    {
        // The encoder is told a frame size once and a video file cannot change size part way
        // through. So a window that grows changes the scale, not the amount of black: it still
        // fills the frame.
        var (width, height) = ClipWindowRule.RecordedSize(1280, 720);
        var fit = ClipWindowRule.Fit(2560, 1440, width, height);

        Assert.Equal((width, height), (fit.Width, fit.Height));
        Assert.Equal((0, 0), (fit.Left, fit.Top));
    }

    [Fact]
    public void AWindowDraggedSmallerIsScaledUpRatherThanLeavingTheFrameEmpty()
    {
        // A 1920-wide window records into a 960 x 540 frame. Dragged down to 800 x 450 it is
        // still the same shape, so it is stretched back out to fill the frame rather than sitting
        // in the corner of it.
        var (width, height) = ClipWindowRule.RecordedSize(1920, 1080);
        var fit = ClipWindowRule.Fit(800, 450, width, height);

        Assert.Equal(0, fit.Level);
        Assert.Equal((width, height), (fit.Width, fit.Height));
        Assert.False(fit.Exact);
    }

    [Fact]
    public void AWindowOfADifferentShapeIsCentredRatherThanPutInACorner()
    {
        // The one case where something has to be left over: the window's shape stopped matching
        // the frame's, and nothing can be done about that without stretching somebody. What is
        // left is split evenly, so the picture stays in the middle.
        var fit = ClipWindowRule.Fit(1080, 1920, 960, 540);

        Assert.Equal(540, fit.Height);
        Assert.True(fit.Width < 960);
        Assert.Equal(0, fit.Top);
        Assert.True(fit.Left > 0);

        // Even on both sides, to within the even-pixel rounding.
        Assert.True(Math.Abs((960 - fit.Width - fit.Left) - fit.Left) <= 2);
    }

    [Fact]
    public void ThePictureIsNeverStretchedOutOfShape()
    {
        // Scaled by the same amount across and down, whatever the window is doing.
        foreach (var (windowWidth, windowHeight) in new[] { (1920, 1080), (2560, 1440), (1366, 768), (800, 600) })
        {
            var fit = ClipWindowRule.Fit(windowWidth, windowHeight, 960, 540);
            var across = fit.Width / (double)windowWidth;
            var down = fit.Height / (double)windowHeight;

            Assert.True(Math.Abs(across - down) < 0.01, $"{windowWidth}x{windowHeight}: {across} across, {down} down.");
        }
    }

    [Fact]
    public void ThePictureNeverRunsPastTheFrame()
    {
        foreach (var windowWidth in new[] { 2, 17, 640, 1366, 1920, 4096 })
        {
            foreach (var windowHeight in new[] { 2, 9, 360, 768, 1080, 2160 })
            {
                var fit = ClipWindowRule.Fit(windowWidth, windowHeight, 960, 540);

                Assert.True(fit.Left >= 0 && fit.Top >= 0);
                Assert.True(fit.Left + fit.Width <= 960, $"{windowWidth}x{windowHeight} ran past the frame.");
                Assert.True(fit.Top + fit.Height <= 540, $"{windowWidth}x{windowHeight} ran past the frame.");
                Assert.True(fit.Width >= 2 && fit.Height >= 2);

                // And the copy it is read from is never smaller than one pixel, whatever level it
                // landed on.
                Assert.True(fit.SourceWidth >= 1 && fit.SourceHeight >= 1);
            }
        }
    }

    [Fact]
    public void TheGraphicsCardDoesAsMuchOfTheShrinkingAsItCan()
    {
        // The level is the last one that is still no smaller than the size being drawn, so the
        // processor is never handed more than about four pixels for each one it writes.
        var fit = ClipWindowRule.Fit(3000, 1688, 960, 540);

        Assert.True(fit.SourceWidth >= fit.Width);
        Assert.True(fit.SourceHeight >= fit.Height);
        Assert.True(fit.SourceWidth < fit.Width * 2);
        Assert.True(fit.SourceHeight < fit.Height * 2);
    }

    [Fact]
    public void AWindowSittingOnTheMonitorIsTakenWhereItIs()
    {
        var box = ClipWindowRule.BoxOnMonitor(
            windowLeft: 100, windowTop: 60, windowWidth: 800, windowHeight: 600,
            monitorLeft: 0, monitorTop: 0, monitorWidth: 1920, monitorHeight: 1080);

        Assert.Equal((100, 60, 800, 600), box);
    }

    [Fact]
    public void AWindowOnTheSecondMonitorIsTakenRelativeToThatMonitor()
    {
        // The second screen starts at 1920 across, and the duplication's picture starts at zero.
        var box = ClipWindowRule.BoxOnMonitor(
            windowLeft: 2020, windowTop: 40, windowWidth: 800, windowHeight: 600,
            monitorLeft: 1920, monitorTop: 0, monitorWidth: 1920, monitorHeight: 1080);

        Assert.Equal((100, 40, 800, 600), box);
    }

    [Fact]
    public void AWindowHangingOffTheEdgeIsCutAtTheEdge()
    {
        // Never past it: a box that ran past the end of the monitor's own picture would be a copy
        // off the end of a texture, which is a crash rather than a wrong pixel.
        var box = ClipWindowRule.BoxOnMonitor(
            windowLeft: 1600, windowTop: 900, windowWidth: 800, windowHeight: 600,
            monitorLeft: 0, monitorTop: 0, monitorWidth: 1920, monitorHeight: 1080);

        Assert.Equal(1600, box.X);
        Assert.Equal(900, box.Y);
        Assert.True(box.X + box.Width <= 1920);
        Assert.True(box.Y + box.Height <= 1080);
    }

    [Fact]
    public void AWindowOnAnotherMonitorEntirelyIsNothingToCopy()
    {
        var box = ClipWindowRule.BoxOnMonitor(
            windowLeft: 3000, windowTop: 100, windowWidth: 800, windowHeight: 600,
            monitorLeft: 0, monitorTop: 0, monitorWidth: 1920, monitorHeight: 1080);

        Assert.Equal((0, 0, 0, 0), box);
    }

    [Fact]
    public void AWindowDraggedOffToTheLeftIsCutAtZero()
    {
        var box = ClipWindowRule.BoxOnMonitor(
            windowLeft: -200, windowTop: -50, windowWidth: 800, windowHeight: 600,
            monitorLeft: 0, monitorTop: 0, monitorWidth: 1920, monitorHeight: 1080);

        Assert.Equal(0, box.X);
        Assert.Equal(0, box.Y);
        Assert.Equal(600, box.Width);
        Assert.Equal(550, box.Height);
    }
}
