using System.Text.Json;

namespace Modbot.Cloud.Features.Showcase;

/// <summary>
/// Fetches a showcase row's picture once, when an administrator saves the row, and hands back the
/// bytes Cloud will serve from then on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it will accept.</strong> An <c>http</c> or <c>https</c> address, answering with at
/// most <see cref="ShowcasePicture.MaxBytes"/> of a PNG, JPEG, GIF or WebP. The format is decided by
/// the first bytes of the answer, not by the header the other host sent, so a page, a script or a zip
/// labelled <c>image/png</c> is refused. Anything else — a refusal, a timeout, a redirect loop, a
/// file that is too big — comes back as nothing, and the caller keeps whatever picture the row
/// already had.
/// </para>
/// <para>
/// <strong>What it sends.</strong> One GET to the address an administrator typed in, with no
/// credential and nothing about Cloud, its accounts or the Modbots that read the showcase. It runs
/// only while somebody is saving a row in Cloud admin; no reader ever causes a fetch.
/// </para>
/// </remarks>
public sealed class ShowcasePictures(HttpClient client)
{
    public const string HttpClientName = "showcase-pictures";

    /// <summary>Long enough for a slow picture host, short enough that saving a row does not hang.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The picture at an address, or null when there is nothing usable there.
    /// </summary>
    /// <param name="now">From the caller's <see cref="TimeProvider"/>; never the machine's clock.</param>
    public async Task<ShowcasePicture?> FetchAsync(string? url, DateTimeOffset now, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(Timeout);

            using var response = await client
                .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, stop.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            // Refused on the header when the other host is honest about the size, and again on the
            // bytes when it is not.
            if (response.Content.Headers.ContentLength is > ShowcasePicture.MaxBytes)
                return null;

            var bytes = await ReadAtMostAsync(response, stop.Token).ConfigureAwait(false);
            if (bytes is null || PictureType(bytes) is not { } contentType)
                return null;

            return new ShowcasePicture
            {
                Id = Guid.CreateVersion7(),
                SourceUrl = url.Trim(),
                ContentType = contentType,
                Bytes = bytes,
                SavedAt = now,
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException
                                    or JsonException or IOException
                                  && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The whole answer, or null when it is over the cap.</summary>
    private static async Task<byte[]?> ReadAtMostAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
                break;

            if (buffer.Length + read > ShowcasePicture.MaxBytes)
                return null;

            buffer.Write(chunk, 0, read);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>
    /// The picture format the bytes actually are, or null when they are not a picture at all.
    /// </summary>
    /// <remarks>
    /// Read from the bytes rather than the other host's <c>Content-Type</c>, because that header is
    /// whatever somebody else chose to write and these bytes are then served from Cloud's own
    /// domain. Serving an HTML page from Cloud's domain because a header called it a picture is the
    /// mistake this exists to prevent.
    /// </remarks>
    public static string? PictureType(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        ReadOnlySpan<byte> jpeg = [0xFF, 0xD8, 0xFF];

        if (bytes.Length < 12)
            return null;

        if (bytes.StartsWith(png))
            return "image/png";

        if (bytes.StartsWith(jpeg))
            return "image/jpeg";

        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
            return "image/gif";

        if (bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";

        return null;
    }
}
