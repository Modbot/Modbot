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

    [Fact]
    public void AWindowThatHasNotChangedFillsTheWholeFrame()
    {
        var (width, height) = ClipWindowRule.RecordedSize(1920, 1080);
        var level = ClipWindowRule.FitLevel(1920, 1080, width, height);

        Assert.Equal((width, height), ClipWindowRule.FittedSize(1920, 1080, width, height, level));
    }

    [Fact]
    public void AWindowDraggedBiggerIsShrunkFurtherRatherThanOverflowingTheFrame()
    {
        // The encoder is told a frame size once and a video file cannot change size part way
        // through, so a resize costs a smaller picture — never a broken file.
        var (width, height) = ClipWindowRule.RecordedSize(1280, 720);
        var level = ClipWindowRule.FitLevel(2560, 1440, width, height);
        var (fitWidth, fitHeight) = ClipWindowRule.FittedSize(2560, 1440, width, height, level);

        Assert.True(fitWidth <= width);
        Assert.True(fitHeight <= height);
    }

    [Fact]
    public void AWindowDraggedSmallerLeavesTheRestOfTheFrameEmpty()
    {
        var (width, height) = ClipWindowRule.RecordedSize(1920, 1080);
        var level = ClipWindowRule.FitLevel(960, 540, width, height);
        var (fitWidth, fitHeight) = ClipWindowRule.FittedSize(960, 540, width, height, level);

        Assert.Equal(0, level);
        Assert.True(fitWidth < width);
        Assert.True(fitHeight < height);
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
