using System.Runtime.InteropServices;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;

namespace Modbot.Overlay.Tests.OpenVr;

/// <summary>
/// The pieces that let the overlay work without Direct3D -- the frame kept in memory and the
/// bytes SteamVR is handed -- and the shapes handed to native code, which nothing at run time
/// checks.
/// </summary>
[Collection(OpenVrCollection.Name)]
public class RawOverlayTests
{
    [Fact]
    public void TheMemorySurfaceKeepsTheLastFrameAndHasNoTexture()
    {
        using var surface = new MemoryOverlaySurface(2, 1);
        byte[] frame = [1, 2, 3, 4, 5, 6, 7, 8];

        surface.Upload(frame);

        Assert.Equal(0, surface.TextureHandle);
        Assert.Equal(frame, surface.Pixels.ToArray());
    }

    [Fact]
    public void TheMemorySurfaceRefusesAFrameOfTheWrongSize()
    {
        using var surface = new MemoryOverlaySurface(2, 2);

        Assert.Throws<ArgumentException>(() => surface.Upload(new byte[3]));
    }

    /// <summary>Avalonia writes BGRA; SteamVR reads the raw picture as RGBA. Red and blue swap, nothing else moves.</summary>
    [Fact]
    public void BgraBecomesRgbaPixelByPixel()
    {
        byte[] bgra = [10, 20, 30, 40, 50, 60, 70, 80];
        var rgba = new byte[bgra.Length];

        RawPixels.BgraToRgba(bgra, rgba);

        Assert.Equal([30, 20, 10, 40, 70, 60, 50, 80], rgba);
    }

    [Fact]
    public void TheConversionRefusesMismatchedBuffers()
    {
        Assert.Throws<ArgumentException>(() => RawPixels.BgraToRgba(new byte[8], new byte[4]));
        Assert.Throws<ArgumentException>(() => RawPixels.BgraToRgba(new byte[6], new byte[6]));
    }

    /// <summary>
    /// SteamVR compares the size it is handed with its own sizeof(VREvent_t) and answers nothing
    /// at all when they differ, so a wrong number here would mean a quit that is never heard.
    /// </summary>
    [Fact]
    public void TheEventStructIsTheSizeSteamVrExpects()
    {
        Assert.Equal(64, Marshal.SizeOf<VrEvent>());
        Assert.Equal(64u, VrEvent.Size);
    }

    [Fact]
    public void ATranslationMatrixIsTheIdentityWithTheOffsetInTheLastColumn()
    {
        var matrix = HmdMatrix34.Translation(0.35f, -0.28f, -1.0f);

        Assert.Equal(48, Marshal.SizeOf<HmdMatrix34>());
        Assert.Equal(1f, matrix.M00);
        Assert.Equal(1f, matrix.M11);
        Assert.Equal(1f, matrix.M22);
        Assert.Equal(0.35f, matrix.M03);
        Assert.Equal(-0.28f, matrix.M13);
        Assert.Equal(-1.0f, matrix.M23);
        Assert.Equal(0f, matrix.M01);
        Assert.Equal(0f, matrix.M10);
    }

    /// <summary>A host that was attached and lost SteamVR draws again on the next attach, not on the next change.</summary>
    [Fact]
    public void TheHeadlessRuntimeAnswersPollsWithoutChangingState()
    {
        using var runtime = new HeadlessOverlayRuntime();
        runtime.Start();

        runtime.Poll();

        Assert.Equal(OverlayRuntimeState.NoRuntime, runtime.Status.State);
    }
}
