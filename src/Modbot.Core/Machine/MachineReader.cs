using System.Globalization;

namespace Modbot.Core.Machine;

/// <summary>
/// The counters this server can read about itself at one moment.
/// </summary>
/// <param name="ProcessorTime">
/// Total processor time this process has used since it started. A running total, not a rate: how
/// hard the processor is working can only be worked out from two of these and the time between
/// them (see <see cref="MachineUsageSampler"/>).
/// </param>
/// <param name="MemoryBytes">Memory this process is holding right now.</param>
/// <param name="MemoryLimitBytes">
/// The most memory this process is allowed, from the container's own settings. Null when nothing
/// says — an ordinary machine with no limit but its own memory.
/// </param>
/// <param name="DiskReadBytes">Bytes this process has read from disk since it started, or null when unreadable.</param>
/// <param name="DiskWrittenBytes">Bytes this process has written to disk since it started, or null when unreadable.</param>
public readonly record struct MachineCounters(
    TimeSpan? ProcessorTime,
    long? MemoryBytes,
    long? MemoryLimitBytes,
    long? DiskReadBytes,
    long? DiskWrittenBytes);

/// <summary>
/// Reads what the operating system will honestly tell Modbot about its own use of the machine.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is about <em>this process</em>, which is the only thing Modbot can measure
/// without being told about the machine it was put on. In a container that is also the useful
/// answer: the container is what the operator sized and pays for.
/// </para>
/// <para>
/// The runtime's own figures are preferred wherever there is one — <see cref="Environment.CpuUsage"/>
/// and <see cref="Environment.WorkingSet"/> are gathered by the runtime on every platform it
/// supports, and re-deriving them from <c>/proc</c> would be a second implementation to keep right.
/// Only the memory limit and the disk counters are read as files, because there is no API for
/// either, and both are wrapped: a missing file, a locked-down <c>/proc</c> or a platform that has
/// neither is a null, never an exception.
/// </para>
/// <para>
/// <strong>Never read the system clock here.</strong> A reading is stamped by the caller from
/// <c>IModbotClock</c> (foundation §4.4).
/// </para>
/// </remarks>
public static class MachineReader
{
    /// <summary>Control group version 2: the memory limit, or the word <c>max</c>.</summary>
    public const string MemoryLimitFileV2 = "/sys/fs/cgroup/memory.max";

    /// <summary>Control group version 1, which is still what some hosts mount.</summary>
    public const string MemoryLimitFileV1 = "/sys/fs/cgroup/memory/memory.limit_in_bytes";

    /// <summary>This process's own disk counters, on Linux.</summary>
    public const string DiskCountersFile = "/proc/self/io";

    /// <summary>
    /// A limit at or above this means there is no limit. Control group version 1 writes a number
    /// near the largest a 64-bit count can hold rather than saying so, and 4 EiB of memory is not
    /// a machine anybody is running Modbot on.
    /// </summary>
    private const long NoLimitFrom = 1L << 62;

    public static MachineCounters Read()
    {
        var (read, written) = DiskCounters(DiskCountersFile);

        return new MachineCounters(
            ProcessorTime(),
            MemoryHeld(),
            MemoryLimit(),
            read,
            written);
    }

    private static TimeSpan? ProcessorTime()
    {
        try
        {
            return Environment.CpuUsage.TotalTime;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static long? MemoryHeld()
    {
        try
        {
            var held = Environment.WorkingSet;
            return held > 0 ? held : null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The most memory this process may use: the container's limit where there is one, and what
    /// the runtime believes it has to work with where there is not.
    /// </summary>
    /// <remarks>
    /// The control group is asked first because it is the one that answers the question an operator
    /// is actually asking — "am I close to the memory I paid for" — and the machine's total says
    /// nothing about that inside a container. The runtime's own figure is the fallback, and it is
    /// already container-aware on the hosts where it can be; on a plain machine it is the memory
    /// fitted, which is the right answer there.
    /// </remarks>
    public static long? MemoryLimit()
    {
        var limit = MemoryLimitFrom(MemoryLimitFileV2) ?? MemoryLimitFrom(MemoryLimitFileV1);
        if (limit is not null)
            return limit;

        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available is > 0 and < NoLimitFrom ? available : null;
    }

    /// <summary>The limit in one control-group file, or null when the file is absent or says there is none.</summary>
    public static long? MemoryLimitFrom(string path)
    {
        var text = TextOf(path);
        return text is null ? null : ParseMemoryLimit(text);
    }

    /// <summary>
    /// Reads a control-group memory limit. Version 2 writes <c>max</c> for "no limit"; version 1
    /// writes a number so large it means the same thing.
    /// </summary>
    public static long? ParseMemoryLimit(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var value = text.Trim();

        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
            return null;

        return bytes is > 0 and < NoLimitFrom ? bytes : null;
    }

    /// <summary>
    /// How much this process has read from and written to disk since it started, or nulls where
    /// the counters cannot be read — which is every platform that is not Linux, and any Linux
    /// where <c>/proc</c> is not mounted for this process.
    /// </summary>
    public static (long? Read, long? Written) DiskCounters(string path)
    {
        var text = TextOf(path);
        return text is null ? (null, null) : ParseDiskCounters(text);
    }

    /// <summary>
    /// Picks the two block-device totals out of <c>/proc/self/io</c>.
    /// </summary>
    /// <remarks>
    /// <c>read_bytes</c> and <c>write_bytes</c> are what actually went to and from storage, which
    /// is the question. The <c>rchar</c> and <c>wchar</c> lines beside them count every read and
    /// write call, including ones the page cache answered without touching a disk, and reporting
    /// those as disk activity would be a number that means something else. <c>cancelled_write_bytes</c>
    /// starts with the same word as <c>write_bytes</c>, so lines are matched whole and never by
    /// what they end with.
    /// </remarks>
    public static (long? Read, long? Written) ParseDiskCounters(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        long? read = null;
        long? written = null;

        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
                continue;

            var name = line.AsSpan(0, colon).Trim();
            var rest = line.AsSpan(colon + 1).Trim();

            if (!long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) || bytes < 0)
                continue;

            if (name.SequenceEqual("read_bytes"))
                read = bytes;
            else if (name.SequenceEqual("write_bytes"))
                written = bytes;
        }

        return (read, written);
    }

    /// <summary>
    /// The contents of a file that may simply not exist. Null covers every reason it could not be
    /// read, because a diagnostic that throws on a host it was not written for is worse than one
    /// that says nothing there.
    /// </summary>
    private static string? TextOf(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
