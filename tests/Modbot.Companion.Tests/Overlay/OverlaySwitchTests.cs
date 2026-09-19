using Modbot.Companion.Overlay;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// The overlay's on/off switch: off builds nothing, on builds it once, and flipping it takes the
/// panel down and brings it back without a restart.
/// </summary>
/// <remarks>
/// Off has to stop the work rather than hide it. The panel is a texture, a drawing loop, the
/// controllers read thirty times a second and a connection to SteamVR, and a moderator who turns
/// it off is saying they want none of that. These count the starts and stops, because a start
/// that happened while the switch was off is exactly the bug this type exists to prevent.
/// </remarks>
public class OverlaySwitchTests
{
    private int _starts;
    private int _stops;

    private OverlaySwitch Make(bool on) => new(() => _starts++, () => _stops++, on);

    [Fact]
    public void SwitchedOffAtStartNothingIsStarted()
    {
        var panel = Make(on: false);

        panel.StartIfOn();

        Assert.Equal(0, _starts);
        Assert.False(panel.Running);
        Assert.False(panel.On);
    }

    [Fact]
    public void SwitchedOnAtStartItIsStartedOnce()
    {
        var panel = Make(on: true);

        panel.StartIfOn();
        panel.StartIfOn();

        Assert.Equal(1, _starts);
        Assert.True(panel.Running);
    }

    [Fact]
    public void ARunningOverlayIsStoppedWhenTheSwitchGoesOff()
    {
        var panel = Make(on: true);
        panel.StartIfOn();

        panel.Set(false);

        Assert.Equal(1, _stops);
        Assert.False(panel.Running);
        Assert.False(panel.On);
    }

    [Fact]
    public void ItStartsAgainWhenTheSwitchComesBackOn()
    {
        var panel = Make(on: true);
        panel.StartIfOn();
        panel.Set(false);

        panel.Set(true);

        Assert.Equal(2, _starts);
        Assert.Equal(1, _stops);
        Assert.True(panel.Running);
        Assert.True(panel.On);
    }

    [Fact]
    public void AClientThatStartedSwitchedOffStartsWhenItIsTurnedOn()
    {
        // Nothing was ever built, so turning it on is the first start rather than a restart.
        var panel = Make(on: false);
        panel.StartIfOn();

        panel.Set(true);

        Assert.Equal(1, _starts);
        Assert.Equal(0, _stops);
        Assert.True(panel.Running);
    }

    [Fact]
    public void TheSameAnswerTwiceChangesNothing()
    {
        // The window redraws on a timer and re-reads its controls; a switch that restarted the
        // panel every time it was told what it already knew would tear the overlay down mid-draw.
        var panel = Make(on: true);
        panel.StartIfOn();

        panel.Set(true);
        Assert.Equal(1, _starts);

        panel.Set(false);
        panel.Set(false);
        Assert.Equal(1, _stops);
    }

    [Fact]
    public void TurningOffSomethingThatWasNeverStartedStopsNothing()
    {
        var panel = Make(on: true);

        panel.Set(false);

        Assert.Equal(0, _starts);
        Assert.Equal(0, _stops);
    }
}
