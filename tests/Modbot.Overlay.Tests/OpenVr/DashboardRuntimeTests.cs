using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;

namespace Modbot.Overlay.Tests.OpenVr;

/// <summary>
/// The dashboard tab with no SteamVR to put it in: a state, never an exception, and nothing
/// handed anywhere.
/// </summary>
[Collection(OpenVrCollection.Name)]
public class DashboardRuntimeTests
{
    [Fact]
    public void AMachineWithoutSteamVrGetsAStateRatherThanAnException()
    {
        Assert.SkipWhen(OpenVrSession.IsSteamVrInstalled, "This says what happens with no SteamVR; this PC has one.");

        using var runtime = new OpenVrDashboardRuntime(DashboardHost.PageWidth, DashboardHost.PageHeight);

        var status = runtime.Start();

        Assert.NotEqual(OverlayRuntimeState.Running, status.State);
        Assert.False(string.IsNullOrWhiteSpace(status.Detail));
    }

    [Fact]
    public void NothingIsHandedOverBeforeTheTabIsMade()
    {
        using var runtime = new OpenVrDashboardRuntime(DashboardHost.PageWidth, DashboardHost.PageHeight);
        using var surface = new MemoryOverlaySurface(4, 4);

        runtime.Poll();

        Assert.False(runtime.Submit(surface));
        Assert.False(runtime.SubmitThumbnail(surface));
        Assert.Empty(runtime.TakePointer());
    }
}
