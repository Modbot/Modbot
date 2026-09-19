using Modbot.Companion.Overlay;
using Modbot.Companion.Sounds;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// The notification overlay's stack. A pop-up is about a moment: it goes up, it stays for the few
/// seconds the moderator chose, and then it is gone. Nothing is queued, because news delivered
/// twenty minutes late interrupts somebody with an instance that has since emptied.
/// </summary>
public class PopUpsTests
{
    private static PopUp Made(string id = "a1") => new(id, "Cat Lounge", "Rin", "kicked before", PopUpTone.Flagged);

    [Fact]
    public void NothingIsUpToStartWith()
    {
        var clock = new FakeClock();

        Assert.Empty(new PopUps(clock).Current());
    }

    [Fact]
    public void OneGoesUpAndComesDownWhenItsTimeIsUp()
    {
        var clock = new FakeClock();
        var popUps = new PopUps(clock) { Dwell = TimeSpan.FromSeconds(6) };

        popUps.Show(Made());
        Assert.Single(popUps.Current());

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(popUps.Current());

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(popUps.Current());
    }

    [Fact]
    public void TheNewestIsFirst()
    {
        var clock = new FakeClock();
        var popUps = new PopUps(clock);

        popUps.Show(Made("a1"));
        clock.Advance(TimeSpan.FromSeconds(1));
        popUps.Show(Made("a2"));

        Assert.Equal(["a2", "a1"], popUps.Current().Select(p => p.Id));
    }

    [Fact]
    public void TheSameThingAgainRestartsItsTimeRatherThanStackingUp()
    {
        // A server that repeats an event, or a link that reconnects and replays one, must not put
        // the same card on the panel twice.
        var clock = new FakeClock();
        var popUps = new PopUps(clock) { Dwell = TimeSpan.FromSeconds(6) };

        popUps.Show(Made("a1"));
        clock.Advance(TimeSpan.FromSeconds(5));
        popUps.Show(Made("a1"));

        Assert.Single(popUps.Current());

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(popUps.Current());
    }

    [Fact]
    public void WhenThereIsNoRoomTheOldestGoes()
    {
        var clock = new FakeClock();
        var popUps = new PopUps(clock) { MostAtOnce = 2 };

        popUps.Show(Made("a1"));
        popUps.Show(Made("a2"));
        popUps.Show(Made("a3"));

        Assert.Equal(["a3", "a2"], popUps.Current().Select(p => p.Id));
    }

    [Fact]
    public void OneCanBeTakenAwayEarly()
    {
        var clock = new FakeClock();
        var popUps = new PopUps(clock);

        popUps.Show(Made("problem"));
        popUps.Show(Made("a1"));
        popUps.Clear("problem");

        Assert.Equal(["a1"], popUps.Current().Select(p => p.Id));
    }

    [Fact]
    public void LeavingClearsTheLot()
    {
        var clock = new FakeClock();
        var popUps = new PopUps(clock);

        popUps.Show(Made("a1"));
        popUps.Show(Made("a2"));
        popUps.ClearAll();

        Assert.Empty(popUps.Current());
    }

    [Fact]
    public void ALongerDwellKeepsThemUpLonger()
    {
        var clock = new FakeClock();
        var popUps = new PopUps(clock) { Dwell = TimeSpan.FromSeconds(30) };

        popUps.Show(Made());
        clock.Advance(TimeSpan.FromSeconds(20));

        Assert.Single(popUps.Current());
    }

    [Fact]
    public void TheSettingsDwellIsWhatTheStackUses()
    {
        var settings = NotifyOverlaySettings.Default with { Seconds = 9f };

        Assert.Equal(TimeSpan.FromSeconds(9), settings.Dwell);
    }

    [Fact]
    public void EachSurfaceShowsItForItsOwnNumberOfSeconds()
    {
        // Two surfaces read this stack -- the notification overlay in a headset and the one on a
        // monitor -- and a moderator can want one to linger and the other to be brief.
        var clock = new FakeClock();
        var popUps = new PopUps(clock) { Dwell = TimeSpan.FromSeconds(20) };

        popUps.Show(Made());
        clock.Advance(TimeSpan.FromSeconds(7));

        Assert.Empty(popUps.Current(TimeSpan.FromSeconds(6)));
        Assert.Single(popUps.Current(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void TheShorterSurfaceAskingDoesNotTakeItOffTheLongerOne()
    {
        // The stack is pruned at the longest anybody wants, so the surface with the short seconds
        // asking first must not drop a card the other one is still showing.
        var clock = new FakeClock();
        var popUps = new PopUps(clock) { Dwell = TimeSpan.FromSeconds(20) };

        popUps.Show(Made());
        clock.Advance(TimeSpan.FromSeconds(7));

        popUps.Current(TimeSpan.FromSeconds(6));

        Assert.Single(popUps.Current(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void AskingForLongerThanTheStackKeepsThemGetsWhatTheStackHas()
    {
        var clock = new FakeClock();
        var popUps = new PopUps(clock) { Dwell = TimeSpan.FromSeconds(6) };

        popUps.Show(Made());
        clock.Advance(TimeSpan.FromSeconds(7));

        Assert.Empty(popUps.Current(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void AKindTheModeratorDidNotTickNeverGoesUpOnEitherSurface()
    {
        // One gate, asked once, whichever surface would have drawn the card. A moderator who
        // asked not to be shown arrivals asked once, not once per screen.
        var clock = new FakeClock();
        var popUps = new PopUps(clock)
        {
            Wanted = kind => kind is NotificationKind.FlaggedJoin,
        };

        popUps.Show(Made("joined:usr_a"), NotificationKind.Joined);
        Assert.Empty(popUps.Current(TimeSpan.FromSeconds(6)));

        popUps.Show(Made("alert:a1"), NotificationKind.FlaggedJoin);
        Assert.Single(popUps.Current(TimeSpan.FromSeconds(6)));
    }
}
