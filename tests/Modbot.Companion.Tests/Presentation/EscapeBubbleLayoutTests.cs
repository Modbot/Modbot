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
    [InlineData(1918, 1008, 1.0)]
    [InlineData(1918, 1030, 1.074)]
    public void TheKnownClientAreasGiveTheirScale(int width, int height, double scale)
        => Assert.Equal(scale, EscapeBubbleLayout.For(width, height)!.Scale);

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(1918, 1029)]
    public void AnyOtherClientAreaStaysAwayRatherThanSitInTheWrongPlace(int width, int height)
        => Assert.Null(EscapeBubbleLayout.For(width, height));

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
