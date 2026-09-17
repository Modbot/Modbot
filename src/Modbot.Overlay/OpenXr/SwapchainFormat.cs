using Modbot.Overlay.OpenVr;
using Silk.NET.Vulkan;

namespace Modbot.Overlay.OpenXr;

/// <summary>
/// The pixel format the overlay's swapchain is made in, and what that means for the upload.
/// </summary>
/// <param name="VulkanFormat">The <c>VkFormat</c> value, as OpenXR lists it.</param>
/// <param name="SwapChannels">
/// Whether the red and blue channels must be swapped on the way up. Avalonia hands out BGRA; a
/// runtime that only offers RGBA gets the channels swapped by <see cref="RawPixels.BgraToRgba"/>.
/// </param>
/// <param name="Name">The format's name, for the log.</param>
public sealed record SwapchainFormat(long VulkanFormat, bool SwapChannels, string Name)
{
    /// <summary>What Avalonia produces, so nothing stands between the renderer and the headset.</summary>
    public static readonly SwapchainFormat Bgra = new((long)Format.B8G8R8A8Srgb, false, "B8G8R8A8_SRGB");

    /// <summary>The fallback: the same eight bits a channel, with red and blue the other way round.</summary>
    public static readonly SwapchainFormat Rgba = new((long)Format.R8G8B8A8Srgb, true, "R8G8B8A8_SRGB");

    /// <summary>
    /// Picks from what the runtime offers: BGRA when it is there, else RGBA, else nothing. Both
    /// are the sRGB variants, because the pixels Avalonia draws are sRGB-encoded and the
    /// compositor must be told so to blend them right.
    /// </summary>
    public static SwapchainFormat? Choose(IEnumerable<long> offered)
    {
        ArgumentNullException.ThrowIfNull(offered);

        var set = offered as ISet<long> ?? new HashSet<long>(offered);
        if (set.Contains(Bgra.VulkanFormat))
            return Bgra;
        if (set.Contains(Rgba.VulkanFormat))
            return Rgba;
        return null;
    }

    /// <summary>Copies one frame of BGRA into the staging memory, in this format's channel order.</summary>
    public void CopyPixels(ReadOnlySpan<byte> bgra, Span<byte> destination)
    {
        if (SwapChannels)
            RawPixels.BgraToRgba(bgra, destination);
        else
            bgra.CopyTo(destination);
    }
}
