using Vortice.DXGI;

namespace Modbot.VrShot;

/// <summary>Turns a mapped texture into tightly packed 8-bit RGB, dropping alpha.</summary>
internal static class Pixels
{
    /// <summary>
    /// Copies rows from a mapped texture into <paramref name="rgb"/>. False for a format it does not
    /// read. 8-bit formats are copied as stored (an _SRGB texture already holds sRGB bytes);
    /// half-float is linear and is encoded to sRGB.
    /// </summary>
    internal static unsafe bool ToRgb(Format format, nint data, int rowPitch, int width, int height, byte[] rgb)
    {
        switch (format)
        {
            case Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb or Format.R8G8B8A8_Typeless:
                Copy8(data, rowPitch, width, height, rgb, red: 0, blue: 2);
                return true;

            case Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb or Format.B8G8R8A8_Typeless
                or Format.B8G8R8X8_UNorm or Format.B8G8R8X8_UNorm_SRgb or Format.B8G8R8X8_Typeless:
                Copy8(data, rowPitch, width, height, rgb, red: 2, blue: 0);
                return true;

            case Format.R16G16B16A16_Float or Format.R16G16B16A16_Typeless:
                for (var y = 0; y < height; y++)
                {
                    var row = (Half*)(data + (y * rowPitch));
                    for (var x = 0; x < width; x++)
                    {
                        var o = ((y * width) + x) * 3;
                        rgb[o] = Srgb((float)row[(x * 4) + 0]);
                        rgb[o + 1] = Srgb((float)row[(x * 4) + 1]);
                        rgb[o + 2] = Srgb((float)row[(x * 4) + 2]);
                    }
                }

                return true;

            default:
                return false;
        }
    }

    private static unsafe void Copy8(nint data, int rowPitch, int width, int height, byte[] rgb, int red, int blue)
    {
        for (var y = 0; y < height; y++)
        {
            var row = (byte*)(data + (y * rowPitch));
            for (var x = 0; x < width; x++)
            {
                var o = ((y * width) + x) * 3;
                rgb[o] = row[(x * 4) + red];
                rgb[o + 1] = row[(x * 4) + 1];
                rgb[o + 2] = row[(x * 4) + blue];
            }
        }
    }

    private static byte Srgb(float linear)
    {
        var c = Math.Clamp(linear, 0f, 1f);
        var s = c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1f / 2.4f)) - 0.055f;
        return (byte)Math.Round(s * 255f);
    }
}
