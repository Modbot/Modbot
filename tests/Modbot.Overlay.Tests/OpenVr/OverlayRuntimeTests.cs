using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;

namespace Modbot.Overlay.Tests.OpenVr;

/// <summary>
/// Most machines running the Modbot Companion have no SteamVR and never will — presence coverage
/// comes from moderators reporting, and only some of them wear a headset. So "no runtime" is the
/// ordinary case, and it must be a state rather than an error.
/// </summary>
[Collection(OpenVrCollection.Name)]
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
        Assert.SkipWhen(OpenVrSession.IsSteamVrInstalled, "This says what happens with no SteamVR; this PC has one.");

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
    public void EachPopupChatIsItsOwnOverlayAndNumberOneKeepsTheOldKey()
    {
        using var one = new OpenVrOverlayRuntime(OverlayKind.Chat);
        using var two = new OpenVrOverlayRuntime(OverlayKind.Chat, chatNumber: 2);
        using var twelve = new OpenVrOverlayRuntime(OverlayKind.Chat, chatNumber: 12);

        Assert.Equal(OpenVrOverlayRuntime.ChatOverlayKey, one.Key);
        Assert.Equal(OpenVrOverlayRuntime.ChatOverlayKey + ".2", two.Key);
        Assert.Equal(OpenVrOverlayRuntime.ChatOverlayKey + ".12", twelve.Key);
        Assert.Equal("Modbot chat", OpenVrOverlayRuntime.ChatOverlayName(1));
        Assert.Equal("Modbot chat 2", OpenVrOverlayRuntime.ChatOverlayName(2));
    }

    [Fact]
    public void ANumberMeansNothingToThePanelsThatAreNotChats()
    {
        using var main = new OpenVrOverlayRuntime(chatNumber: 3);
        using var button = new OpenVrOverlayRuntime(OverlayKind.Button, chatNumber: 3);

        Assert.Equal(OpenVrOverlayRuntime.OverlayKey, main.Key);
        Assert.Equal(OpenVrOverlayRuntime.ButtonOverlayKey, button.Key);
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
