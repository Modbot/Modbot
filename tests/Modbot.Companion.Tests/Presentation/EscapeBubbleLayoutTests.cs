using Modbot.Companion.Presentation;
using Xunit;

namespace Modbot.Companion.Tests.Presentation;

public sealed class EscapeBubbleLayoutTests
{
    [Theory]
    [InlineData("f1", "F1")]
    [InlineData("f9", "F9")]
    [InlineData("f12", "F12")]
    [InlineData("mod+alt+m", "Ctrl+Alt+M")]
    [InlineData("mod+shift+k", "Ctrl+Shift+K")]
    [InlineData("alt+escape", "Alt+Esc")]
    public void TheLabelIsWrittenTheWayVRChatWritesItsOwn(string shortcut, string label)
        => Assert.Equal(label, EscapeBubbleLayout.Label(shortcut));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoShortcutMeansNoLabel(string? shortcut)
        => Assert.Null(EscapeBubbleLayout.Label(shortcut));

    [Theory]
    [InlineData(1918, 1008, 0.999)]
    [InlineData(1920, 1009, 1.0)]
    [InlineData(1920, 1080, 1.0704)]
    [InlineData(2560, 1440, 1.4272)]
    [InlineData(1280, 720, 0.7136)]
    public void AnyClientAreaGivesTheScaleOfItsHeightOver1009(int width, int height, double scale)
        => Assert.Equal(scale, EscapeBubbleLayout.For(width, height)!.Scale, 3);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1920, 0)]
    [InlineData(0, 1080)]
    [InlineData(-1, 1080)]
    public void AnEmptyOrNonsensicalClientAreaStillGivesNothing(int width, int height)
        => Assert.Null(EscapeBubbleLayout.For(width, height));

    [Fact]
    public void TheRowMeasuredAt1920By1009IsWhereTheBubbleGoes()
    {
        var m = EscapeBubbleLayout.For(1920, 1009)!;

        // Measured on the real window: icons 64.5 apart, the fifth slot's centre at 313.5,
        // icons from y 40, label pills from y 78, 20 high.
        Assert.InRange(m.CentreX, 313, 314);
        Assert.Equal(40, m.Top);
        Assert.Equal(78 - 40, m.PillTop);
        Assert.Equal(20, m.PillHeight);
    }

    [Fact]
    public void TheRowMeasuredAt2560By1440GrowsWithTheHeightNotTheWidth()
    {
        var m = EscapeBubbleLayout.For(2560, 1440)!;

        // The row's pitch there was 92.3 pixels, which is 64.5 times 1440 over 1009. The slot's
        // centre is the Esc centre and four pitches: (55.5 + 4 * 64.5) * 1.4272 = 447.
        Assert.InRange(m.CentreX, 446, 448);

        // The same height at another width is the same row.
        Assert.Equal(m.CentreX, EscapeBubbleLayout.For(1920, 1440)!.CentreX);
        Assert.Equal(m.CentreX, EscapeBubbleLayout.For(3440, 1440)!.CentreX);
    }

    [Fact]
    public void ItTakesTheSlotAfterYInTheSameRowAtTheSameHeight()
    {
        var m = new EscapeBubbleMetrics(1.0);

        // Esc is at 55.5 and the row is 64.5 apart, so Y is the fourth at 249 and ours the fifth.
        Assert.Equal(314, m.CentreX);
        Assert.Equal(40, m.Top);
        Assert.Equal(20, m.PillHeight);
        Assert.Equal(41, m.PillMinWidth);
    }

    [Fact]
    public void EverythingGrowsWithTheHud()
    {
        var small = new EscapeBubbleMetrics(1.0);
        var large = new EscapeBubbleMetrics(1.074);

        Assert.True(large.CentreX > small.CentreX);
        Assert.True(large.Top > small.Top);
        Assert.True(large.FontSize > small.FontSize);
        Assert.True(large.Height > small.Height);
    }

    [Theory]
    [InlineData(10, 41)]
    [InlineData(25, 41)]
    [InlineData(70, 86)]
    public void ALongLabelWidensThePillAndAShortOneDoesNot(double text, int width)
        => Assert.Equal(width, new EscapeBubbleMetrics(1.0).Width(text));

    [Fact]
    public void ALongLabelGrowsToTheRightAndNeverBackIntoTheBubbleBesideIt()
    {
        var m = new EscapeBubbleMetrics(1.0);

        // The left edge is the same at any width, so it can only grow away from Y.
        Assert.Equal(314 - 41 / 2, m.Place().X);
    }

    [Fact]
    public void TheNudgeMovesItAndNothingElseDoes()
    {
        var m = new EscapeBubbleMetrics(1.0);
        var plain = m.Place();
        var nudged = m.Place(nudgeX: 3, nudgeY: -2);

        Assert.Equal((plain.X + 3, plain.Y - 2), nudged);
    }
}
