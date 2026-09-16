using System.IO.Compression;
using System.Text.Json;
using Modbot.Cloud.Features.EventBackup;

namespace Modbot.Cloud.Features.InstanceLogs;

/// <summary>
/// Reads a log batch body, gzipped or not, without letting it grow past the limits.
/// </summary>
/// <remarks>
/// The same shape as <see cref="EventBatchReader"/> and for the same reason: both caps are enforced
/// <em>while</em> reading, so a body is never held beyond
/// <see cref="InstanceLogLimits.MaxCompressedBytes"/> as sent or
/// <see cref="InstanceLogLimits.MaxDecompressedBytes"/> once expanded, whatever the request claims.
/// Log batches have their own, larger decompressed cap: text compresses far better than the event
/// documents do, so the same megabyte on the wire carries several times as much.
/// </remarks>
public static class LogBatchReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<(BatchReadOutcome Outcome, LogBatch? Batch)> ReadAsync(
        HttpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentLength > InstanceLogLimits.MaxCompressedBytes)
            return (BatchReadOutcome.TooLarge, null);

        var raw = await ReadCappedAsync(request.Body, InstanceLogLimits.MaxCompressedBytes, ct);
        if (raw is null)
            return (BatchReadOutcome.TooLarge, null);

        var body = raw;

        if (request.Headers.ContentEncoding.Any(
                v => v is not null && v.Contains("gzip", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await using var gzip = new GZipStream(raw, CompressionMode.Decompress);
                body = await ReadCappedAsync(gzip, InstanceLogLimits.MaxDecompressedBytes, ct);
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
            var batch = await JsonSerializer.DeserializeAsync<LogBatch>(body, Json, ct);
            return batch is null ? (BatchReadOutcome.Malformed, null) : (BatchReadOutcome.Read, batch);
        }
        catch (JsonException)
        {
            return (BatchReadOutcome.Malformed, null);
        }
    }

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
