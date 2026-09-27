using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Modbot.Api.Features.Companion.Events;

/// <summary>How reading a batch body ended.</summary>
public enum BatchReadOutcome
{
    Read,
    TooLarge,
    Malformed,
}

/// <summary>
/// Reads a companion batch body, gzipped or not, without letting it grow past the limits.
/// </summary>
/// <remarks>
/// <para><strong>Why the handler reads its own body.</strong> The protocol has the companion gzip
/// any batch over about 4 KB (protocol §2), and this API had nothing that undid it. The framework
/// read the gzip bytes as JSON, failed, and answered <c>400</c> before the handler ran -- which the
/// companion is told means "this batch will always be wrong, drop it". Every batch big enough to
/// compress, which is every arrival burst, was lost that way. Reading it here, after the device
/// token is checked, also means nobody without a token can make the server decompress
/// anything.</para>
/// <para>Both limits are enforced while reading, not after: a body is never held beyond
/// <see cref="MaxCompressedBytes"/> as sent or <see cref="MaxDecompressedBytes"/> once expanded,
/// whatever the request claims.</para>
/// </remarks>
public static class EventBatchReader
{
    /// <summary>The body as sent. Five hundred events are about 175 KB of JSON before gzip.</summary>
    public const int MaxCompressedBytes = 1024 * 1024;

    /// <summary>The body after decompressing. Stops a small gzip that expands to gigabytes.</summary>
    public const int MaxDecompressedBytes = 4 * 1024 * 1024;

    public static async Task<(BatchReadOutcome Outcome, EventBatchDto? Batch)> ReadAsync(
        HttpRequest request, JsonSerializerOptions json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentLength > MaxCompressedBytes)
            return (BatchReadOutcome.TooLarge, null);

        var raw = await ReadCappedAsync(request.Body, MaxCompressedBytes, ct);
        if (raw is null)
            return (BatchReadOutcome.TooLarge, null);

        var body = raw;
        if (request.Headers.ContentEncoding.Any(v => v is not null && v.Contains("gzip", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await using var gzip = new GZipStream(raw, CompressionMode.Decompress);
                body = await ReadCappedAsync(gzip, MaxDecompressedBytes, ct);
            }
            catch (InvalidDataException)
            {
                return (BatchReadOutcome.Malformed, null);
            }

            if (body is null)
                return (BatchReadOutcome.TooLarge, null);
        }

        try
        {
            var batch = await JsonSerializer.DeserializeAsync<EventBatchDto>(body, json, ct);
            return batch is null ? (BatchReadOutcome.Malformed, null) : (BatchReadOutcome.Read, batch);
        }
        catch (JsonException)
        {
            return (BatchReadOutcome.Malformed, null);
        }
    }

    /// <summary>The stream's bytes, or null once more than <paramref name="cap"/> have arrived.</summary>
    private static async Task<MemoryStream?> ReadCappedAsync(Stream source, int cap, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81_920];

        while (true)
        {
            var read = await source.ReadAsync(chunk, ct);
            if (read == 0)
                break;

            if (buffer.Length + read > cap)
                return null;

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }
}
