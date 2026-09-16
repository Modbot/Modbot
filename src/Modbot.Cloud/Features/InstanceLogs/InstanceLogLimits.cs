using Modbot.Cloud.Common;

namespace Modbot.Cloud.Features.InstanceLogs;

/// <summary>
/// Cloud's limits on log batches.
/// </summary>
/// <remarks>
/// <para>
/// Set well above what a real deployment sends. A quiet Modbot writes a few thousand lines a day; a
/// noisy one catching up after being unable to reach Cloud for a week still fits. They exist to stop
/// a broken or hostile sender, not a busy one.
/// </para>
/// <para>Held in memory, per Cloud process; a restart forgets them.</para>
/// </remarks>
public sealed class InstanceLogLimits(TimeProvider time)
{
    public const int MaxLinesPerBatch = 1_000;

    /// <summary>The request body as sent, before decompressing.</summary>
    public const int MaxCompressedBytes = 1024 * 1024;

    /// <summary>The body after decompressing. Stops a small gzip that expands to gigabytes.</summary>
    public const int MaxDecompressedBytes = 8 * 1024 * 1024;

    public const int BatchesPerMinute = 30;

    /// <summary>
    /// Four times the event ceiling: log lines are the more numerous of the two, and a deployment
    /// catching up after a day offline sends a day's worth in a few minutes.
    /// </summary>
    public const int LinesPerHour = 200_000;

    public WindowLimit Batches { get; } = new(BatchesPerMinute, TimeSpan.FromMinutes(1), time);

    public WindowLimit Lines { get; } = new(LinesPerHour, TimeSpan.FromHours(1), time);
}
