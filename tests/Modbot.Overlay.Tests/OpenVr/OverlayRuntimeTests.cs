using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;

namespace Modbot.Overlay.Tests.OpenVr;

/// <summary>
/// Most machines running the Modbot Companion have no SteamVR and never will — presence coverage
/// comes from moderators reporting, and only some of them wear a headset. So "no runtime" is the
/// ordinary case, and it must be a state rather than an error.
/// </summary>
public class OverlayRuntimeTests
{
    private sealed class FakeSurface : IOverlaySurface
    {
        public int Width => 64;

        public int Height => 64;

        public nint TextureHandle => 42;

        public ReadOnlyMemory<byte> Pixels => ReadOnlyMemory<byte>.Empty;

        public void Upload(ReadOnlySpan<byte> bgra) { }

        public void Dispose() { }
    }

    [Fact]
    public void AMachineWithoutSteamVrGetsAStateRatherThanAnException()
    {
        using var runtime = new OpenVrOverlayRuntime();

        var status = runtime.Start();

        Assert.NotEqual(OverlayRuntimeState.Running, status.State);
        Assert.Contains(status.State, (OverlayRuntimeState[])
            [OverlayRuntimeState.NoRuntime, OverlayRuntimeState.NotStarted, OverlayRuntimeState.Refused]);
        Assert.False(string.IsNullOrWhiteSpace(status.Detail));
    }

    [Fact]
    public void SubmittingWithoutAStartedRuntimeIsRefusedRatherThanCrashing()
    {
        using var runtime = new OpenVrOverlayRuntime();
        using var surface = new FakeSurface();

        Assert.False(runtime.Submit(surface));
    }

    [Fact]
    public void ShowingAndHidingWithoutARuntimeDoNothing()
    {
        using var runtime = new OpenVrOverlayRuntime();

        runtime.Show();
        runtime.Hide();
    }

    [Fact]
    public void TheHeadlessRuntimeStandsInForAMachineWithNoHeadset()
    {
        using var runtime = new HeadlessOverlayRuntime();
        using var surface = new FakeSurface();

        Assert.Equal(OverlayRuntimeState.NoRuntime, runtime.Start().State);
        Assert.True(runtime.Submit(surface));
        Assert.Equal(1, runtime.Submissions);

        runtime.Show();
        Assert.True(runtime.IsShowing);
        runtime.Hide();
        Assert.False(runtime.IsShowing);
    }

    [Fact]
    public void TheOverlayKeyIsStableBecauseSteamVrKeepsSettingsUnderIt()
    {
        // SteamVR stores the moderator's own position and curvature adjustments against this
        // string. Changing it silently discards them, so it is pinned by a test rather than left
        // as a constant somebody might tidy.
        Assert.Equal("moe.bin.modbot.overlay", OpenVrOverlayRuntime.OverlayKey);
    }
}
