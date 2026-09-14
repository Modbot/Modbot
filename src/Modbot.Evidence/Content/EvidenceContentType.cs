using System.Text;

namespace Modbot.Evidence.Content;

/// <summary>Why a file was refused, in terms an operator can act on.</summary>
public enum ContentRejection
{
    /// <summary>Not refused.</summary>
    None,

    /// <summary>Nothing arrived.</summary>
    Empty,

    /// <summary>Too short for any signature on the allowlist to be present.</summary>
    Truncated,

    /// <summary>
    /// Recognisably a markup or script format — SVG, HTML, XML. Called out separately because it
    /// is the interesting one, and because "unrecognised" would be a misleading thing to tell
    /// somebody who just tried to upload an SVG named <c>.png</c>.
    /// </summary>
    ScriptableMarkup,

    /// <summary>A real format, just not one on the allowlist — HEIC, AVIF, PDF, a zip.</summary>
    NotAllowed,
}

/// <param name="ContentType">The type Modbot determined. Null when nothing was accepted.</param>
/// <param name="Rejection">Why not, when nothing was accepted.</param>
/// <param name="Detail">One sentence for the moderator who is looking at an error message.</param>
public sealed record ContentTypeVerdict(string? ContentType, ContentRejection Rejection, string Detail)
{
    public bool Accepted => ContentType is not null;
}

/// <summary>
/// Decides a file's type from its leading bytes, against a closed allowlist
/// (design section 10.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never the client's <c>Content-Type</c> and never the filename.</strong> Both are
/// attacker-chosen. A moderation tool where staff routinely open files uploaded by other staff is
/// a near-ideal stored-XSS target — the attacker is already authenticated, the audience is exactly
/// the people with the most permissions, and the delivery mechanism is a feature — so the type
/// recorded in the blob record and later echoed to a browser is Modbot's determination and
/// nobody else's.
/// </para>
/// <para>
/// <strong>SVG and HTML are not on the list and never will be.</strong> SVG is a script execution
/// format wearing an image's file extension and is the single most common way an "image upload"
/// becomes an XSS. Refusing the category outright costs a moderator nothing: nobody attaches a
/// vector drawing as evidence of harassment.
/// </para>
/// <para>
/// This is a signature check, not a decoder. It reads a bounded prefix, compares bytes, and makes
/// no attempt to validate that the rest of the file is well-formed — design section 11.2 keeps
/// every media codec out of the container, and this must not smuggle one in. A signature check
/// plus an allowlist plus attachment-only delivery plus browser-native decoding is the
/// proportionate answer; it is not a claim that an MP4 is safe to decode.
/// </para>
/// </remarks>
public static class EvidenceContentType
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Webp = "image/webp";
    public const string Gif = "image/gif";
    public const string Mp4 = "video/mp4";
    public const string Webm = "video/webm";

    /// <summary>The whole allowlist. Everything else is refused (design section 10.1).</summary>
    public static readonly IReadOnlyList<string> Allowed = [Png, Jpeg, Webp, Gif, Mp4, Webm];

    /// <summary>
    /// How many leading bytes are enough to decide. Bounded, because this parser runs on hostile
    /// input before anything else has looked at it.
    /// </summary>
    public const int SignatureBytes = 512;

    /// <summary>
    /// ISO base media brands that mean "MP4 as a browser understands it".
    /// </summary>
    /// <remarks>
    /// An <c>ftyp</c> box is not by itself evidence of an MP4 — HEIC and AVIF are ISO base media
    /// files too, and both are off the allowlist. So the brand is checked rather than the box.
    /// </remarks>
    private static readonly string[] Mp4Brands =
    [
        "isom", "iso2", "iso4", "iso5", "iso6", "avc1", "mp41", "mp42", "mmp4", "dash", "M4V ", "M4VP", "mp71",
    ];

    /// <summary>Brands that are ISO base media but explicitly not video/mp4.</summary>
    private static readonly string[] RejectedIsoBrands = ["heic", "heix", "hevc", "heim", "heis", "avif", "avis", "mif1", "msf1", "crx "];

    /// <summary>
    /// Decides the type, or says why it will not.
    /// </summary>
    /// <param name="prefix">
    /// The first bytes of the file. <see cref="SignatureBytes"/> is more than enough; fewer is
    /// accepted and simply narrows what can be recognised.
    /// </param>
    public static ContentTypeVerdict Sniff(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length == 0)
            return new ContentTypeVerdict(null, ContentRejection.Empty, "The file is empty.");

        if (StartsWith(prefix, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
            return Accept(Png);

        // SOI plus the first marker. Every JPEG variant — JFIF, Exif, raw — starts this way.
        if (StartsWith(prefix, [0xFF, 0xD8, 0xFF]))
            return Accept(Jpeg);

        if (StartsWithAscii(prefix, "GIF87a") || StartsWithAscii(prefix, "GIF89a"))
            return Accept(Gif);

        // RIFF....WEBP
        if (StartsWithAscii(prefix, "RIFF") && prefix.Length >= 12 && AsciiAt(prefix, 8, "WEBP"))
            return Accept(Webp);

        if (prefix.Length >= 12 && AsciiAt(prefix, 4, "ftyp"))
            return SniffIsoBaseMedia(prefix);

        // EBML. Matroska and WebM share it, and only one of them is on the list.
        if (StartsWith(prefix, [0x1A, 0x45, 0xDF, 0xA3]))
            return SniffEbml(prefix);

        if (LooksLikeMarkup(prefix, out var what))
        {
            return new ContentTypeVerdict(
                null,
                ContentRejection.ScriptableMarkup,
                $"This file is {what}, which is not accepted.");
        }

        if (prefix.Length < 12)
        {
            return new ContentTypeVerdict(
                null,
                ContentRejection.Truncated,
                "The file is too short to be an accepted format.");
        }

        return new ContentTypeVerdict(
            null,
            ContentRejection.NotAllowed,
            "The file is not a PNG, JPEG, WebP, GIF, MP4 or WebM.");
    }

    /// <summary>
    /// Whether a client's declared type is worth beginning an upload for.
    /// </summary>
    /// <remarks>
    /// A courtesy check only — the cheapest possible refusal, before a byte moves. It decides
    /// nothing: <see cref="Sniff"/> at commit is the authority, and a file that lied here is
    /// rejected there.
    /// </remarks>
    public static bool IsPlausibleDeclaredType(string? declared)
        => declared is not null
           && Allowed.Contains(declared.Split(';')[0].Trim(), StringComparer.OrdinalIgnoreCase);

    private static ContentTypeVerdict SniffIsoBaseMedia(ReadOnlySpan<byte> prefix)
    {
        var brand = Encoding.ASCII.GetString(prefix.Slice(8, 4));

        if (Mp4Brands.Contains(brand, StringComparer.Ordinal))
            return Accept(Mp4);

        if (RejectedIsoBrands.Contains(brand, StringComparer.Ordinal))
        {
            return new ContentTypeVerdict(
                null,
                ContentRejection.NotAllowed,
                $"This is an ISO media file with brand '{brand}', which is not accepted.");
        }

        return new ContentTypeVerdict(
            null,
            ContentRejection.NotAllowed,
            $"This is an ISO media file with an unrecognised brand ('{brand}'). Only MP4 is accepted.");
    }

    private static ContentTypeVerdict SniffEbml(ReadOnlySpan<byte> prefix)
    {
        // The DocType string sits in the EBML header, within the first few dozen bytes. Searching
        // a bounded window for it is enough to separate WebM from plain Matroska without writing
        // an EBML parser, which would be a parser on hostile input for no gain.
        var window = prefix[..Math.Min(prefix.Length, 64)];

        if (IndexOfAscii(window, "webm") >= 0)
            return Accept(Webm);

        return new ContentTypeVerdict(
            null,
            ContentRejection.NotAllowed,
            IndexOfAscii(window, "matroska") >= 0
                ? "This is a Matroska file rather than a WebM one. Only WebM is accepted."
                : "This is an EBML file that does not declare itself as WebM.");
    }

    private static bool LooksLikeMarkup(ReadOnlySpan<byte> prefix, out string what)
    {
        var window = prefix[..Math.Min(prefix.Length, SignatureBytes)];

        // Leading whitespace and a BOM are both ordinary in files that are trying to look like
        // something else, so they are skipped rather than relied on.
        var start = 0;
        if (window.Length >= 3 && window[0] == 0xEF && window[1] == 0xBB && window[2] == 0xBF)
            start = 3;

        while (start < window.Length && window[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            start++;

        var text = window[start..];
        if (text.Length == 0 || text[0] != (byte)'<')
        {
            what = string.Empty;
            return false;
        }

        if (IndexOfAscii(text, "<svg") >= 0 || IndexOfAscii(text, "<SVG") >= 0)
        {
            what = "an SVG";
            return true;
        }

        if (IndexOfAscii(text, "<html") >= 0 || IndexOfAscii(text, "<HTML") >= 0
            || IndexOfAscii(text, "<!DOCTYPE html") >= 0 || IndexOfAscii(text, "<!doctype html") >= 0)
        {
            what = "HTML";
            return true;
        }

        what = "markup";
        return true;
    }

    private static ContentTypeVerdict Accept(string contentType)
        => new(contentType, ContentRejection.None, $"Recognised as {contentType} from its leading bytes.");

    private static bool StartsWith(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> signature)
        => prefix.Length >= signature.Length && prefix[..signature.Length].SequenceEqual(signature);

    private static bool StartsWithAscii(ReadOnlySpan<byte> prefix, string signature)
        => AsciiAt(prefix, 0, signature);

    private static bool AsciiAt(ReadOnlySpan<byte> prefix, int offset, string signature)
    {
        if (prefix.Length < offset + signature.Length)
            return false;

        for (var i = 0; i < signature.Length; i++)
        {
            if (prefix[offset + i] != (byte)signature[i])
                return false;
        }

        return true;
    }

    private static int IndexOfAscii(ReadOnlySpan<byte> haystack, string needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (AsciiAt(haystack, i, needle))
                return i;
        }

        return -1;
    }
}
