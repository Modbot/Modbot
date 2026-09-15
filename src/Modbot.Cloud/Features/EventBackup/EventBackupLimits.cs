using Modbot.Cloud.Common;

namespace Modbot.Cloud.Features.EventBackup;

/// <summary>
/// Cloud's limits on event batches (cloud event backup spec 8).
/// </summary>
/// <remarks>
/// <para>
/// Set well above what a real client sends — at most 500 events a batch, a batch at most once a
/// minute while anything happens — so they only ever stop a broken or hostile client. The sample
/// log produced about thirty events an hour; a client catching up after a week offline still fits.
/// </para>
/// <para>The per-install limits are held in memory, per process, and a restart forgets them.</para>
/// </remarks>
public sealed class EventBackupLimits(TimeProvider time)
{
    public const int MaxEventsPerBatch = 1_000;

    /// <summary>The request body as sent, before decompressing.</summary>
    public const int MaxCompressedBytes = 1024 * 1024;

    /// <summary>The body after decompressing. Stops a small gzip that expands to gigabytes.</summary>
    public const int MaxDecompressedBytes = 4 * 1024 * 1024;

    public const int BatchesPerMinute = 30;

    public const int EventsPerHour = 50_000;

    public WindowLimit Batches { get; } = new(BatchesPerMinute, TimeSpan.FromMinutes(1), time);

    public WindowLimit Events { get; } = new(EventsPerHour, TimeSpan.FromHours(1), time);
}
