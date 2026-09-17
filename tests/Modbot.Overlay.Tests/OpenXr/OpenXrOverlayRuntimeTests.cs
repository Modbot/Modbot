using Modbot.Overlay.OpenVr;
using Modbot.Overlay.OpenXr;
using Modbot.Overlay.Rendering;
using Silk.NET.OpenXR;

namespace Modbot.Overlay.Tests.OpenXr;

/// <summary>
/// The OpenXR runtime without a runtime: absence is a state, a picture handed over early is
/// kept, and nothing throws. No test here starts a real session; that is done on a WiVRn
/// machine from source, with the log saying what happened at each step.
/// </summary>
public class OpenXrOverlayRuntimeTests
{
    private sealed class WrongSizeSurface : IOverlaySurface
    {
        public int Width => 3;

        public int Height => 3;

        public nint TextureHandle => 0;

        public ReadOnlyMemory<byte> Pixels => new byte[3 * 3 * 4];

        public void Upload(ReadOnlySpan<byte> bgra)
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void AMachineWithoutAnOpenXrRuntimeGetsAStateRatherThanAnException()
    {
        using var runtime = new OpenXrOverlayRuntime(16);

        var status = runtime.Start();

        // A machine with WiVRn or Monado up cannot make this case; say so rather than fail.
        Assert.SkipWhen(status.State is OverlayRuntimeState.Running, "An OpenXR overlay runtime is running on this machine.");

        Assert.Contains(status.State, (OverlayRuntimeState[])
            [OverlayRuntimeState.NoRuntime, OverlayRuntimeState.NotStarted, OverlayRuntimeState.Refused]);
        Assert.False(string.IsNullOrWhiteSpace(status.Detail));
        Assert.Same(status, runtime.Status);

        // Asking again is the companion's ten-second look, and must be just as harmless.
        Assert.Equal(status.State, runtime.Start().State);
    }

    [Fact]
    public void BeforeStartTheStatusSaysSo()
    {
        using var runtime = new OpenXrOverlayRuntime(16);

        Assert.Equal(OverlayRuntimeState.NotStarted, runtime.Status.State);
        Assert.Equal("OpenXR has not been looked for yet.", runtime.Status.Detail);
    }

    [Fact]
    public void ASubmitBeforeStartIsKeptForTheFirstFrame()
    {
        using var runtime = new OpenXrOverlayRuntime(8);
        using var surface = new MemoryOverlaySurface(8, 8);
        surface.Upload(new byte[8 * 8 * 4]);

        Assert.True(runtime.Submit(surface));
        Assert.True(runtime.HasPendingFrame);
    }

    [Fact]
    public void ASurfaceOfAnotherSizeIsRefusedRatherThanUploadedTorn()
    {
        using var runtime = new OpenXrOverlayRuntime(8);
        using var surface = new WrongSizeSurface();

        Assert.False(runtime.Submit(surface));
        Assert.False(runtime.HasPendingFrame);
    }

    [Fact]
    public void ShowHideAndDisposeWithoutARuntimeDoNothing()
    {
        var runtime = new OpenXrOverlayRuntime(8);

        runtime.Show();
        runtime.Hide();
        runtime.Poll();
        runtime.Dispose();

        Assert.Equal(OverlayRuntimeState.NotStarted, runtime.Status.State);
    }

    [Fact]
    public void TheLoaderIsLookedForByItsVersionedNameOnLinux()
    {
        // The unversioned libopenxr_loader.so symlink comes only with a -dev package; every
        // machine with a runtime has libopenxr_loader.so.1. Silk.NET's own list is what XR.GetApi
        // searches, so the binding's list is pinned here rather than trusted.
        var container = typeof(XR).Assembly.GetTypes().Single(t => t.Name == "OpenXRLibraryNameContainer");
        var names = (string[])container.GetProperty("Linux")!.GetValue(Activator.CreateInstance(container))!;

        Assert.Contains("libopenxr_loader.so.1", names);
    }

    [Fact]
    public void TheExtensionsAskedForAreTheOverlayAndVulkanOnes()
    {
        Assert.Equal("XR_EXTX_overlay", OpenXrOverlayRuntime.OverlayExtension);
        Assert.Equal("XR_KHR_vulkan_enable2", OpenXrOverlayRuntime.VulkanExtension);
    }
}
