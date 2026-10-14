namespace Modbot.Core.Files;

/// <summary>
/// What kind of picture some bytes are, read from their first bytes and never from what a host or
/// a browser said they are (calendar design §15.2, added 2026-10-02).
/// </summary>
/// <remarks>
/// A link somebody typed can answer with anything and call it anything. A page that says it is a
/// PNG and is HTML is how an image turns into a script once Modbot serves it from its own address,
/// so the type a picture goes on with is the one its bytes have. SVG is never a picture here: it is
/// a document that can carry scripts, and the evidence store and the VRChat file route refuse it for
/// the same reason.
/// </remarks>
public static class PictureFormats
{
    /// <summary>
    /// The picture type of <paramref name="bytes"/> (<c>image/png</c>, <c>image/jpeg</c>,
    /// <c>image/gif</c>, <c>image/webp</c>, <c>image/bmp</c>, <c>image/avif</c> or
    /// <c>image/heic</c>), or null when they are none of those.
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return "image/png";

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return "image/jpeg";

        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
            return "image/gif";

        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";

        if (bytes.StartsWith("BM"u8) && bytes.Length >= 26)
            return "image/bmp";

        // ISO media files: a box size, "ftyp", then the brand that says what is inside.
        if (bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = bytes[8..12];

            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
                return "image/avif";

            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8)
                || brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8)
                || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8))
            {
                return "image/heic";
            }
        }

        return null;
    }

    /// <summary>Whether Discord takes this type as an event cover or a file on a card.</summary>
    public static bool DiscordTakes(string? type) =>
        type is "image/png" or "image/jpeg" or "image/gif" or "image/webp";

    /// <summary>
    /// The width and height of a PNG or JPEG, read from its header, or null for any other kind or
    /// bytes cut short. Bluesky wants the shape of a picture beside it so it can reserve the room
    /// before the picture loads.
    /// </summary>
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> bytes)
    {
        if (Sniff(bytes) == "image/png")
        {
            // 8 bytes of signature, then the IHDR chunk: length, "IHDR", width, height.
            if (bytes.Length < 24 || !bytes[12..16].SequenceEqual("IHDR"u8))
                return null;

            return Positive(ReadInt(bytes[16..20]), ReadInt(bytes[20..24]));
        }

        if (!bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return null;

        // Segments from the top, until the one that says how big the picture is.
        var at = 2;
        while (at + 4 <= bytes.Length)
        {
            if (bytes[at] != 0xFF)
                return null;

            var marker = bytes[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            var length = (bytes[at + 2] << 8) | bytes[at + 3];

            // Start of frame (not the table, arithmetic-coding and reserved markers in the same range).
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                return at + 9 <= bytes.Length
                    ? Positive((bytes[at + 7] << 8) | bytes[at + 8], (bytes[at + 5] << 8) | bytes[at + 6])
                    : null;
            }

            if (marker == 0xDA || length < 2)
                return null;

            at += 2 + length;
        }

        return null;
    }

    private static int ReadInt(ReadOnlySpan<byte> four) => (four[0] << 24) | (four[1] << 16) | (four[2] << 8) | four[3];

    private static (int Width, int Height)? Positive(int width, int height) =>
        width > 0 && height > 0 ? (width, height) : null;
}
