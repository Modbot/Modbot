using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The notification overlay on a monitor: which corner lands where, and the numbers kept inside
/// their bounds.
/// </summary>
/// <remarks>
/// Plain numbers and no window, which is the point of <see cref="DesktopNotifySettings.Corner"/>
/// being written that way: where a window goes on a screen is worth pinning down without a screen.
/// </remarks>
public class DesktopNotifySettingsTests
{
    /// <summary>A 1920×1080 work area with no taskbar taken out of it, and a 340×200 window.</summary>
    private static (int X, int Y) At(ScreenSpot spot, int margin = 24)
        => DesktopNotifySettings.Corner(spot, 0, 0, 1920, 1080, 340, 200, margin);

    [Fact]
    public void ItIsOffUntilSomebodyTurnsItOn()
    {
        // A window that appears over everything else on the machine is asked for rather than
        // assumed, the same rule the window over VRChat follows.
        Assert.False(DesktopNotifySettings.Default.On);
        Assert.Equal(ScreenSpot.BottomRight, DesktopNotifySettings.Default.Spot);
        Assert.Equal(6f, DesktopNotifySettings.Default.Seconds);
    }

    [Fact]
    public void TheBottomRightIsTheBottomRight()
    {
        var (x, y) = At(ScreenSpot.BottomRight);

        Assert.Equal(1920 - 340 - 24, x);
        Assert.Equal(1080 - 200 - 24, y);
    }

    [Fact]
    public void TheTopLeftIsTheMarginInFromBothEdges()
    {
        Assert.Equal((24, 24), At(ScreenSpot.TopLeft));
    }

    [Fact]
    public void TheTopRightHugsTheRightEdgeAndTheTop()
    {
        Assert.Equal((1920 - 340 - 24, 24), At(ScreenSpot.TopRight));
    }

    [Fact]
    public void TheBottomLeftHugsTheLeftEdgeAndTheBottom()
    {
        Assert.Equal((24, 1080 - 200 - 24), At(ScreenSpot.BottomLeft));
    }

    [Fact]
    public void AMiddleSpotIsCentredBetweenTheMargins()
    {
        var (top, _) = At(ScreenSpot.TopMiddle);
        var (bottom, y) = At(ScreenSpot.BottomMiddle);

        Assert.Equal(24 + ((1920 - 340 - 48) / 2), top);
        Assert.Equal(top, bottom);
        Assert.Equal(1080 - 200 - 24, y);
    }

    [Fact]
    public void ASecondMonitorsOwnOriginIsCarried()
    {
        // A screen to the right of the first one starts at 1920, and a corner on it is a corner
        // on it -- not a corner of the desktop as a whole.
        var (x, y) = DesktopNotifySettings.Corner(ScreenSpot.BottomRight, 1920, -120, 2560, 1440, 340, 200, 24);

        Assert.Equal(1920 + 2560 - 340 - 24, x);
        Assert.Equal(-120 + 1440 - 200 - 24, y);
    }

    [Fact]
    public void AWindowBiggerThanTheScreenStillLandsInsideIt()
    {
        // Nothing goes negative, so a window that will not fit is at the margin rather than off
        // the side of the screen where nobody could see it.
        var (x, y) = DesktopNotifySettings.Corner(ScreenSpot.BottomRight, 0, 0, 300, 200, 340, 400, 24);

        Assert.Equal(24, x);
        Assert.Equal(24, y);
    }

    [Fact]
    public void TheSecondsAreKeptInsideTheirBounds()
    {
        Assert.Equal(DesktopNotifySettings.MinSeconds, (DesktopNotifySettings.Default with { Seconds = 0 }).Clamped().Seconds);
        Assert.Equal(DesktopNotifySettings.MaxSeconds, (DesktopNotifySettings.Default with { Seconds = 900 }).Clamped().Seconds);
        Assert.Equal(DesktopNotifySettings.DefaultSeconds, (DesktopNotifySettings.Default with { Seconds = float.NaN }).Clamped().Seconds);
    }

    [Fact]
    public void TheDwellIsTheSecondsAfterClamping()
    {
        Assert.Equal(TimeSpan.FromSeconds(9), (DesktopNotifySettings.Default with { Seconds = 9 }).Dwell);
        Assert.Equal(TimeSpan.FromSeconds(DesktopNotifySettings.MaxSeconds), (DesktopNotifySettings.Default with { Seconds = 9000 }).Dwell);
    }

    [Fact]
    public void TheObjectGoesOutAndComesBackTheSame()
    {
        var settings = new DesktopNotifySettings(On: true, ScreenSpot.TopLeft, 12);

        Assert.Equal(settings, DesktopNotifySettings.FromJson(settings.ToJson()));
    }

    [Fact]
    public void NoObjectAtAllIsTheDefaults()
    {
        Assert.Equal(DesktopNotifySettings.Default, DesktopNotifySettings.FromJson(null));
    }

    [Fact]
    public void ASpotThisClientDoesNotKnowTakesTheDefaultOne()
    {
        var json = DesktopNotifySettings.Default.ToJson();
        json["spot"] = "somewhere else";

        Assert.Equal(DesktopNotifySettings.Default.Spot, DesktopNotifySettings.FromJson(json).Spot);
    }
}
