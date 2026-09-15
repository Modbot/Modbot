using System.IO.Compression;
using System.Text.Json;

namespace Modbot.Cloud.Features.LogBackup;

/// <summary>How reading a request body ended.</summary>
public enum LogBatchReadOutcome
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
/// <see cref="LogBackupLimits.MaxCompressedBytes"/> as sent or
/// <see cref="LogBackupLimits.MaxDecompressedBytes"/> once expanded, whatever the request claims.
/// </remarks>
public static class LogBatchReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<(LogBatchReadOutcome Outcome, LogBatch? Batch)> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentLength > LogBackupLimits.MaxCompressedBytes)
            return (LogBatchReadOutcome.TooLarge, null);

        var raw = await ReadCappedAsync(request.Body, LogBackupLimits.MaxCompressedBytes, ct);
        if (raw is null)
            return (LogBatchReadOutcome.TooLarge, null);

        var body = raw;
        if (IsGzip(request))
        {
            try
            {
                await using var gzip = new GZipStream(raw, CompressionMode.Decompress);
                body = await ReadCappedAsync(gzip, LogBackupLimits.MaxDecompressedBytes, ct);
            }
            catch (InvalidDataException)
            {
                return (LogBatchReadOutcome.Malformed, null);
            }

            if (body is null)
                return (LogBatchReadOutcome.TooLarge, null);
        }

        try
        {
            var batch = await JsonSerializer.DeserializeAsync<LogBatch>(body, Json, ct);
            return batch is null ? (LogBatchReadOutcome.Malformed, null) : (LogBatchReadOutcome.Read, batch);
        }
        catch (JsonException)
        {
            return (LogBatchReadOutcome.Malformed, null);
        }
    }

    private static bool IsGzip(HttpRequest request) =>
        request.Headers.ContentEncoding.Any(v => v is not null && v.Contains("gzip", StringComparison.OrdinalIgnoreCase));

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
