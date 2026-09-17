using Modbot.Overlay.OpenXr;
using Silk.NET.Vulkan;

namespace Modbot.Overlay.Tests.OpenXr;

/// <summary>
/// The format choice and the channel swap (overlay-on-OpenXR spec, 3.2 step 5): BGRA when the
/// runtime offers it, RGBA with red and blue swapped on upload when it does not, nothing when
/// neither is there.
/// </summary>
public class SwapchainFormatTests
{
    [Fact]
    public void BgraIsChosenWheneverItIsOfferedWhereverItIsListed()
    {
        var chosen = SwapchainFormat.Choose([(long)Format.R8G8B8A8Srgb, (long)Format.R8G8B8A8Unorm, (long)Format.B8G8R8A8Srgb]);

        Assert.NotNull(chosen);
        Assert.Same(SwapchainFormat.Bgra, chosen);
        Assert.False(chosen.SwapChannels);
        Assert.Equal((long)Format.B8G8R8A8Srgb, chosen.VulkanFormat);
    }

    [Fact]
    public void RgbaIsTheFallbackAndSwapsTheChannels()
    {
        var chosen = SwapchainFormat.Choose([(long)Format.R8G8B8A8Unorm, (long)Format.R8G8B8A8Srgb]);

        Assert.NotNull(chosen);
        Assert.Same(SwapchainFormat.Rgba, chosen);
        Assert.True(chosen.SwapChannels);
    }

    [Fact]
    public void NeitherMeansNoFormat()
    {
        // The UNORM variants are not taken: the pixels are sRGB-encoded and the compositor must
        // be told so, or the panel comes out washed out.
        Assert.Null(SwapchainFormat.Choose([(long)Format.R8G8B8A8Unorm, (long)Format.B8G8R8A8Unorm]));
        Assert.Null(SwapchainFormat.Choose([]));
    }

    [Fact]
    public void BgraCopiesPixelsAsTheyAre()
    {
        byte[] bgra = [1, 2, 3, 4, 5, 6, 7, 8];
        var destination = new byte[8];

        SwapchainFormat.Bgra.CopyPixels(bgra, destination);

        Assert.Equal(bgra, destination);
    }

    [Fact]
    public void RgbaSwapsRedAndBlueAndKeepsGreenAndAlpha()
    {
        byte[] bgra = [1, 2, 3, 4, 5, 6, 7, 8];
        var destination = new byte[8];

        SwapchainFormat.Rgba.CopyPixels(bgra, destination);

        Assert.Equal([3, 2, 1, 4, 7, 6, 5, 8], destination);
    }
}
