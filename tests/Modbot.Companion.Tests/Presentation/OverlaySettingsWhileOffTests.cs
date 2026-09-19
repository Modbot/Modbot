using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// An overlay that is switched off still has settings, and the page still shows them.
/// </summary>
/// <remarks>
/// The SteamVR page used to replace itself with the switch when the overlay was off, so the only
/// way to arrange where the panel would sit was to turn it on, move it, and turn it off again.
/// The rows that describe a running panel go away while it is off; the placement does not
/// (two overlay modes design §6.1).
/// </remarks>
public class OverlaySettingsWhileOffTests
{
    [Fact]
    public void ASwitchedOffOverlayStillCarriesWhereThePanelWillSit()
    {
        var left = OverlayPlacement.Default with { Anchor = OverlayAnchor.LeftHand, Width = 0.7f };

        var off = OverlayStatus.Off with { Placement = left };

        Assert.False(off.On);
        Assert.Equal(left, off.PlacementOrDefault);
    }

    [Fact]
    public void AnOverlayThatCouldNotBeSetUpStillCarriesIt()
    {
        var wall = OverlayPlacement.Default with { Anchor = OverlayAnchor.World };

        var none = OverlayStatus.None with { Placement = wall };

        Assert.Equal(wall, none.PlacementOrDefault);
    }

    [Fact]
    public void WithNothingSaidTheDefaultIsWhatTheCardShows()
    {
        Assert.Equal(OverlayPlacement.Default, OverlayStatus.None.PlacementOrDefault);
        Assert.Equal(NotifyOverlaySettings.Default, NotifyOverlayStatus.None.SettingsOrDefault);
    }

    [Fact]
    public void TheNotificationOverlaysSwitchLivesInItsOwnSettings()
    {
        // One object, written whole: the switch cannot be lost in a write, because nothing but
        // the settings page ever writes it.
        var off = NotifyOverlayStatus.None with { Settings = NotifyOverlaySettings.Default with { On = false } };

        Assert.False(off.On);
        Assert.Equal(ScreenSpot.TopRight, off.SettingsOrDefault.Spot);
    }

    [Fact]
    public void TheSnapshotCarriesBothOverlaysSettingsWhicheverWayTheSwitchesAreSet()
    {
        var state = new CompanionAppState(
            new TestSupport.FakeClock(),
            new Companion.Journal.SentJournal(
                Path.Combine(Path.GetTempPath(), "modbot-overlay-off-tests", Guid.NewGuid().ToString("n"), "sent.jsonl"),
                new TestSupport.FakeClock()),
            CompanionSettings.Default with
            {
                OverlayOn = false,
                Overlay = OverlayPlacement.Default with { Width = 0.9f },
                NotifyOverlay = NotifyOverlaySettings.Default with { On = false, Spot = ScreenSpot.BottomMiddle },
            })
        {
            Overlay = OverlayStatus.Off with { Placement = OverlayPlacement.Default with { Width = 0.9f } },
        };

        var snapshot = state.Snapshot();

        Assert.False(snapshot.OverlayOrNone.On);
        Assert.Equal(0.9f, snapshot.OverlayOrNone.PlacementOrDefault.Width, 3);
        Assert.False(snapshot.NotifyOverlayOrNone.On);
        Assert.Equal(ScreenSpot.BottomMiddle, snapshot.NotifyOverlayOrNone.SettingsOrDefault.Spot);
    }
}
