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
}
