using Modbot.Companion.Overlay;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.TestRemote;

/// <summary>What the test remote says about a panel's place includes how big it has been made.</summary>
public class PlacementReportTests
{
    [Fact]
    public void ThePlacementReportSaysHowWideAndTallThePanelWasMade()
    {
        var report = TestRemoteState.Placement(OverlayPlacement.Default with { Wide = 1.5f, Tall = 2f });

        Assert.Equal(1.5, (double)report["wide"]!, 3);
        Assert.Equal(2.0, (double)report["tall"]!, 3);
    }

    [Fact]
    public void AnOrdinaryPanelReportsOneAndOne()
    {
        var report = TestRemoteState.Placement(OverlayPlacement.Default);

        Assert.Equal(1.0, (double)report["wide"]!, 3);
        Assert.Equal(1.0, (double)report["tall"]!, 3);
    }
}
