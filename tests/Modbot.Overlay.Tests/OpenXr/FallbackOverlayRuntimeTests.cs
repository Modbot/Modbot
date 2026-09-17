using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;

namespace Modbot.Overlay.Tests.OpenXr;

/// <summary>
/// The fallback's choice for every pair of answers (overlay-on-OpenXR spec, 3.1): OpenVR first,
/// OpenXR only after "no runtime" or xrizer's InvalidApplicationType, and the more telling of
/// two failures shown.
/// </summary>
public class FallbackOverlayRuntimeTests
{
    private sealed class ScriptedRuntime(string name) : IOverlayRuntime
    {
        public OverlayRuntimeStatus Answer { get; set; } = new(OverlayRuntimeState.NoRuntime, Detail: name + ": no runtime");

        public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted, Detail: name + ": not looked");

        public int Starts { get; private set; }

        public int Polls { get; private set; }

        public int Submissions { get; private set; }

        public bool IsShowing { get; private set; }

        public bool Disposed { get; private set; }

        public bool CloseOnPoll { get; set; }

        public OverlayRuntimeStatus Start()
        {
            Starts++;
            return Status = Answer;
        }

        public void Poll()
        {
            Polls++;
            if (CloseOnPoll)
                Status = new(OverlayRuntimeState.NotStarted, Detail: name + " closed.");
        }

        public bool Submit(IOverlaySurface surface)
        {
            Submissions++;
            return true;
        }

        public List<Modbot.Companion.Overlay.OverlayPlacement> Placed { get; } = [];

        public Modbot.Overlay.Interaction.OverlayTracking ReadTracking() => Modbot.Overlay.Interaction.OverlayTracking.None;

        public void Place(Modbot.Companion.Overlay.OverlayPlacement placement) => Placed.Add(placement);

        public void Show() => IsShowing = true;

        public void Hide() => IsShowing = false;

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeSurface : IOverlaySurface
    {
        public int Width => 8;

        public int Height => 8;

        public nint TextureHandle => 0;

        public ReadOnlyMemory<byte> Pixels => new byte[8 * 8 * 4];

        public void Upload(ReadOnlySpan<byte> bgra)
        {
        }

        public void Dispose()
        {
        }
    }

    private static OverlayRuntimeStatus Answer(string name, OverlayRuntimeState state, VrInitError error = VrInitError.None)
        => new(state, error, $"{name}: {state}");

    /// <summary>
    /// Every pair. Columns: OpenVR's answer, OpenXR's answer, who ends up attached, whose status
    /// is shown, and whether OpenXR was asked at all.
    /// </summary>
    public static TheoryData<OverlayRuntimeState, VrInitError, OverlayRuntimeState, string, string, bool> Pairs()
    {
        var data = new TheoryData<OverlayRuntimeState, VrInitError, OverlayRuntimeState, string, string, bool>();
        OverlayRuntimeState[] openXrAnswers =
            [OverlayRuntimeState.Running, OverlayRuntimeState.NoRuntime, OverlayRuntimeState.NotStarted, OverlayRuntimeState.Refused];

        // OpenVR running, not running, or refusing for its own reasons: final, OpenXR never asked.
        foreach (var xr in openXrAnswers)
        {
            data.Add(OverlayRuntimeState.Running, VrInitError.None, xr, "openvr", "openvr", false);
            data.Add(OverlayRuntimeState.NotStarted, VrInitError.Init_NoServerForBackgroundApp, xr, "none", "openvr", false);
            data.Add(OverlayRuntimeState.Refused, VrInitError.Unknown, xr, "none", "openvr", false);
        }

        // Nothing installed for OpenVR: OpenXR is asked, and a not-running or refusal from it says
        // more than "not installed" did; two not-installed answers show SteamVR's.
        data.Add(OverlayRuntimeState.NoRuntime, VrInitError.None, OverlayRuntimeState.Running, "openxr", "openxr", true);
        data.Add(OverlayRuntimeState.NoRuntime, VrInitError.None, OverlayRuntimeState.NoRuntime, "none", "openvr", true);
        data.Add(OverlayRuntimeState.NoRuntime, VrInitError.None, OverlayRuntimeState.NotStarted, "none", "openxr", true);
        data.Add(OverlayRuntimeState.NoRuntime, VrInitError.None, OverlayRuntimeState.Refused, "none", "openxr", true);

        // xrizer refused: OpenXR is asked. Its refusal wins over xrizer's; its not-running wins
        // too, because that is what the companion's ten-second look waits on, and xrizer's
        // refusal is by design and never changes. Only a missing loader leaves xrizer's words up.
        data.Add(OverlayRuntimeState.Refused, VrInitError.Init_InvalidApplicationType, OverlayRuntimeState.Running, "openxr", "openxr", true);
        data.Add(OverlayRuntimeState.Refused, VrInitError.Init_InvalidApplicationType, OverlayRuntimeState.NoRuntime, "none", "openvr", true);
        data.Add(OverlayRuntimeState.Refused, VrInitError.Init_InvalidApplicationType, OverlayRuntimeState.NotStarted, "none", "openxr", true);
        data.Add(OverlayRuntimeState.Refused, VrInitError.Init_InvalidApplicationType, OverlayRuntimeState.Refused, "none", "openxr", true);

        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void EveryPairOfAnswersEndsWhereTheSpecSays(
        OverlayRuntimeState openVrState, VrInitError openVrError, OverlayRuntimeState openXrState,
        string attached, string statusFrom, bool openXrAsked)
    {
        var openVr = new ScriptedRuntime("openvr") { Answer = Answer("openvr", openVrState, openVrError) };
        var openXr = new ScriptedRuntime("openxr") { Answer = Answer("openxr", openXrState) };
        using var fallback = new FallbackOverlayRuntime(openVr, openXr);

        var status = fallback.Start();

        Assert.Equal(1, openVr.Starts);
        Assert.Equal(openXrAsked ? 1 : 0, openXr.Starts);
        Assert.StartsWith(statusFrom, status.Detail);
        Assert.Same(status, fallback.Status);
        Assert.Same(
            attached switch { "openvr" => openVr, "openxr" => openXr, _ => null },
            fallback.Attached);
        Assert.Equal(attached != "none", status.State is OverlayRuntimeState.Running);
    }

    [Fact]
    public void ARunningRuntimeIsNotAskedAgain()
    {
        var openVr = new ScriptedRuntime("openvr") { Answer = Answer("openvr", OverlayRuntimeState.Running) };
        var openXr = new ScriptedRuntime("openxr");
        using var fallback = new FallbackOverlayRuntime(openVr, openXr);

        fallback.Start();
        fallback.Start();

        Assert.Equal(1, openVr.Starts);
        Assert.Equal(0, openXr.Starts);
    }

    [Fact]
    public void PollSubmitShowAndHideGoToWhicheverIsAttached()
    {
        var openVr = new ScriptedRuntime("openvr") { Answer = Answer("openvr", OverlayRuntimeState.NoRuntime) };
        var openXr = new ScriptedRuntime("openxr") { Answer = Answer("openxr", OverlayRuntimeState.Running) };
        using var fallback = new FallbackOverlayRuntime(openVr, openXr);
        using var surface = new FakeSurface();

        // Nothing attached yet: nothing to submit to.
        Assert.False(fallback.Submit(surface));

        fallback.Start();
        Assert.True(fallback.Submit(surface));
        fallback.Show();
        fallback.Poll();
        fallback.Hide();

        Assert.Equal(1, openXr.Submissions);
        Assert.Equal(0, openVr.Submissions);
        Assert.Equal(1, openXr.Polls);
        Assert.Equal(0, openVr.Polls);
        Assert.False(openXr.IsShowing);
    }

    [Fact]
    public void ARuntimeThatClosesIsLetGoAndTheNextStartBeginsFromTheTop()
    {
        var openVr = new ScriptedRuntime("openvr") { Answer = Answer("openvr", OverlayRuntimeState.NoRuntime) };
        var openXr = new ScriptedRuntime("openxr") { Answer = Answer("openxr", OverlayRuntimeState.Running), CloseOnPoll = true };
        using var fallback = new FallbackOverlayRuntime(openVr, openXr);

        Assert.Equal(OverlayRuntimeState.Running, fallback.Start().State);

        fallback.Poll();

        Assert.Equal(OverlayRuntimeState.NotStarted, fallback.Status.State);
        Assert.Equal("openxr closed.", fallback.Status.Detail);
        Assert.Null(fallback.Attached);

        // SteamVR might have been installed since; it is asked first again.
        openXr.CloseOnPoll = false;
        fallback.Start();
        Assert.Equal(2, openVr.Starts);
        Assert.Equal(2, openXr.Starts);
        Assert.Same(openXr, fallback.Attached);
    }

    [Fact]
    public void DisposingLetsBothGo()
    {
        var openVr = new ScriptedRuntime("openvr");
        var openXr = new ScriptedRuntime("openxr");
        var fallback = new FallbackOverlayRuntime(openVr, openXr);

        fallback.Dispose();

        Assert.True(openVr.Disposed);
        Assert.True(openXr.Disposed);
        Assert.Equal(OverlayRuntimeState.NotStarted, fallback.Status.State);
    }

    [Fact]
    public void TheHostUsesTheFallbackByDefault()
    {
        // Not started: constructing the host only wires the runtime up, so this checks the choice
        // without touching SteamVR or an OpenXR loader.
        AvaloniaTestHost.Run(() =>
        {
            using var host = OverlayHost.Create(16);
            Assert.Equal(OverlayRuntimeState.NotStarted, host.Status.State);
            Assert.Equal("No VR runtime has been looked for yet.", host.Status.Detail);
        });
    }

    [Fact]
    public void ThePlacementReachesWhicheverRuntimeAttaches()
    {
        var openVr = new ScriptedRuntime("OpenVR") { Answer = new(OverlayRuntimeState.NoRuntime, Detail: "none") };
        var openXr = new ScriptedRuntime("OpenXR") { Answer = new(OverlayRuntimeState.Running, Detail: "Attached to WiVRn.") };
        var fallback = new FallbackOverlayRuntime(openVr, openXr);
        var wall = Modbot.Companion.Overlay.OverlayPlacement.Default with { Anchor = Modbot.Companion.Overlay.OverlayAnchor.World };

        fallback.Place(wall);
        Assert.Empty(openXr.Placed);

        fallback.Start();

        Assert.Equal([wall], openXr.Placed);
        Assert.Empty(openVr.Placed);
    }
}
