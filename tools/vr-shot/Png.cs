using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Modbot.VrShot;

/// <summary>
/// The smallest PNG writer that does the job: 8-bit RGB, no filtering, zlib from the framework.
/// Written here so the tool needs no imaging package.
/// </summary>
internal static class Png
{
    private static readonly uint[] CrcTable = MakeCrcTable();

    /// <summary>Writes rows of 8-bit RGB (3 bytes a pixel, no padding) as a PNG file.</summary>
    internal static void WriteRgb(string path, int width, int height, ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length != width * height * 3)
            throw new ArgumentException("Pixel data does not match the size.", nameof(rgb));

        using var file = File.Create(path);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;  // bits per channel
        header[9] = 2;  // RGB
        header[10] = 0; // deflate
        header[11] = 0; // standard filters
        header[12] = 0; // not interlaced
        WriteChunk(file, "IHDR", header);

        using (var pixels = new MemoryStream())
        {
            using (var zlib = new ZLibStream(pixels, CompressionLevel.Fastest, leaveOpen: true))
            {
                var stride = width * 3;
                for (var y = 0; y < height; y++)
                {
                    zlib.WriteByte(0); // filter: none
                    zlib.Write(rgb.Slice(y * stride, stride));
                }
            }

            WriteChunk(file, "IDAT", pixels.GetBuffer().AsSpan(0, (int)pixels.Length));
        }

        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream file, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        file.Write(four);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        file.Write(typeBytes);
        file.Write(data);

        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(four, crc);
        file.Write(four);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
