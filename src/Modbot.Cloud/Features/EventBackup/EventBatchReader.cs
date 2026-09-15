using System.IO.Compression;
using System.Text.Json;

namespace Modbot.Cloud.Features.EventBackup;

/// <summary>How reading a request body ended.</summary>
public enum BatchReadOutcome
{
    Read,
    TooLarge,
    Malformed,
}

/// <summary>
/// Reads a batch body, gzipped or not, without letting it grow past the limits.
/// </summary>
/// <remarks>
/// Both limits are enforced while reading, not after: a body is never held beyond
/// <see cref="EventBackupLimits.MaxCompressedBytes"/> as sent or
/// <see cref="EventBackupLimits.MaxDecompressedBytes"/> once expanded, whatever the request claims.
/// </remarks>
public static class EventBatchReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<(BatchReadOutcome Outcome, EventBatch? Batch)> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentLength > EventBackupLimits.MaxCompressedBytes)
            return (BatchReadOutcome.TooLarge, null);

        var raw = await ReadCappedAsync(request.Body, EventBackupLimits.MaxCompressedBytes, ct);
        if (raw is null)
            return (BatchReadOutcome.TooLarge, null);

        var body = raw;
        if (request.Headers.ContentEncoding.Any(v => v is not null && v.Contains("gzip", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await using var gzip = new GZipStream(raw, CompressionMode.Decompress);
                body = await ReadCappedAsync(gzip, EventBackupLimits.MaxDecompressedBytes, ct);
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
            var batch = await JsonSerializer.DeserializeAsync<EventBatch>(body, Json, ct);
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
