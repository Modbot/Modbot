using Modbot.Cloud.Common;

namespace Modbot.Cloud.Features.LogBackup;

/// <summary>
/// Cloud's limits on log batches (cloud log backup spec 8).
/// </summary>
/// <remarks>
/// <para>
/// Set well above what a real client sends — 1,000 lines and 512 KB a batch, about one a minute —
/// so they only ever stop a broken or hostile one. A real log runs at about 16,000 lines an hour.
/// </para>
/// <para>
/// The per-install limits are held in memory, per process, and a restart forgets them.
/// </para>
/// </remarks>
public sealed class LogBackupLimits(TimeProvider time)
{
    public const int MaxLinesPerBatch = 2_000;

    /// <summary>The request body as sent, before decompressing.</summary>
    public const int MaxCompressedBytes = 2 * 1024 * 1024;

    /// <summary>The body after decompressing. Stops a small gzip that expands to gigabytes.</summary>
    public const int MaxDecompressedBytes = 8 * 1024 * 1024;

    public const int BatchesPerMinute = 30;

    public const int LinesPerHour = 200_000;

    public WindowLimit Batches { get; } = new(BatchesPerMinute, TimeSpan.FromMinutes(1), time);

    public WindowLimit Lines { get; } = new(LinesPerHour, TimeSpan.FromHours(1), time);
}
