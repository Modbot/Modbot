using System.Buffers.Binary;

namespace Modbot.Core.Files;

/// <summary>
/// What kind of video some bytes are, read from their first bytes and never from what a host or a
/// browser said they are, the way <see cref="PictureFormats"/> does it for pictures.
/// </summary>
/// <remarks>
/// A link somebody typed can answer with anything and call it anything, so the type a video goes to
/// Bluesky with is the one its bytes have. Bluesky takes MP4, MPEG, WebM and QuickTime (MOV).
/// </remarks>
public static class VideoFormats
{
    /// <summary>
    /// The largest video Modbot keeps or fetches: 50 MB, Modbot's own cap (a kept video sits in the
    /// database and a linked one is held in memory while it goes up). What Bluesky takes from an
    /// account was NOT MEASURED; a video it refuses is shown on the post.
    /// </summary>
    public const int MaxBytes = 50 * 1024 * 1024;

    /// <summary>
    /// The video type of <paramref name="bytes"/> (<c>video/mp4</c>, <c>video/quicktime</c>,
    /// <c>video/webm</c> or <c>video/mpeg</c>), or null when they are none of those.
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> bytes)
    {
        // ISO media files: a box size, "ftyp", then the brand that says what is inside. A picture
        // brand (HEIC, AVIF) or a sound-only one is not a video.
        if (bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = bytes[8..12];

            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)
                || brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8)
                || brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8)
                || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8)
                || brand.SequenceEqual("M4A "u8) || brand.SequenceEqual("M4B "u8) || brand.SequenceEqual("F4A "u8))
            {
                return null;
            }

            return brand.SequenceEqual("qt  "u8) ? "video/quicktime" : "video/mp4";
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
            return "video/webm";

        // MPEG program stream (pack header) or video sequence header.
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0xBA]) || bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0xB3]))
            return "video/mpeg";

        return null;
    }

    /// <summary>
    /// The width and height of an MP4 or QuickTime video, read from its first video track's header
    /// (turned the right way up for a phone's sideways video), or null for any other kind or bytes
    /// with no such track. Bluesky wants the shape of a video beside it so it can reserve the room.
    /// </summary>
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> bytes)
    {
        if (Sniff(bytes) is not ("video/mp4" or "video/quicktime"))
            return null;

        return FindIn(bytes, 0, bytes.Length, depth: 0);
    }

    /// <summary>Boxes between <paramref name="start"/> and <paramref name="end"/>: down into moov and trak, to tkhd.</summary>
    private static (int Width, int Height)? FindIn(ReadOnlySpan<byte> bytes, int start, int end, int depth)
    {
        var at = start;

        while (at + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(bytes[at..]);
            var header = 8;

            if (size == 1)
            {
                if (at + 16 > end)
                    return null;

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes[(at + 8)..]);
                header = 16;
            }
            else if (size == 0)
            {
                size = end - at;
            }

            if (size < header || at + size > end)
                return null;

            var body = at + header;
            var stop = (int)(at + size);
            var type = bytes.Slice(at + 4, 4);

            if (type.SequenceEqual("tkhd"u8))
            {
                if (TrackSize(bytes[body..stop]) is { } found)
                    return found;
            }
            else if (depth < 3 && (type.SequenceEqual("moov"u8) || type.SequenceEqual("trak"u8)))
            {
                if (FindIn(bytes, body, stop, depth + 1) is { } found)
                    return found;
            }

            at = stop;
        }

        return null;
    }

    /// <summary>A track header's width and height, or null for a track with none (a sound track).</summary>
    private static (int Width, int Height)? TrackSize(ReadOnlySpan<byte> tkhd)
    {
        if (tkhd.Length < 4)
            return null;

        // Version 0 has 32-bit times and a 32-bit length; version 1 has 64-bit ones.
        var matrix = tkhd[0] == 1 ? 52 : 40;
        var size = matrix + 36;

        if (tkhd.Length < size + 8)
            return null;

        // The size is 16.16 fixed point: the whole pixels are the top half.
        var width = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd[size..]) >> 16);
        var height = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd[(size + 4)..]) >> 16);

        if (width <= 0 || height <= 0)
            return null;

        // A matrix that turns the picture a quarter turn: a and d are 0, b and c are not.
        var a = BinaryPrimitives.ReadInt32BigEndian(tkhd[matrix..]);
        var b = BinaryPrimitives.ReadInt32BigEndian(tkhd[(matrix + 4)..]);
        var c = BinaryPrimitives.ReadInt32BigEndian(tkhd[(matrix + 12)..]);
        var d = BinaryPrimitives.ReadInt32BigEndian(tkhd[(matrix + 16)..]);

        return a == 0 && d == 0 && b != 0 && c != 0 ? (height, width) : (width, height);
    }
}
